using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Core;

/// <summary>
/// A SQLite 3 database, opened for reading with <see cref="FromPath"/> or one of the <c>FromStream</c> methods. Its
/// tables are found with <see cref="GetTable"/>, <see cref="TryGetTable"/> and <see cref="GetTables"/>. Dispose it to
/// close its files.
/// </summary>
/// <remarks>
/// The database can be used from several threads at once. What it learns at open never changes after, each call
/// allocates what it reads into, and each stream is read under a lock, so nothing else may read or move the streams
/// while the database is open. An enumeration of <see cref="Sqlite3Table.EnumerateRows()"/>, and the rows it returns,
/// belong to the thread that runs it. In Lenient, <see cref="Sqlite3Settings.OnValidationIssue"/> is called on the
/// reading thread.
/// </remarks>
public sealed class Sqlite3Database : IDisposable
{
    private readonly ReaderBase _reader;

    private readonly Sqlite3MasterTable _masterTable;
    private readonly Sqlite3SchemaRow _masterSchemaRow;

    // What FindTable resolves a name to, built once at open: the first row per name that made a table, and the first
    // error per name. Never changed after, so lookups need no lock.
    private readonly Dictionary<string, Sqlite3SchemaRow> _tablesByName;
    private readonly Dictionary<string, SqliteCorruptException>? _errorsByName;

    internal DatabaseHeader Header => _reader.Header;

    /// <summary>
    /// The encoding that text values are read with: the header's text encoding field, read as SQLite reads it (the low
    /// two bits, where 0 means UTF-8), or <see cref="Sqlite3Settings.FallbackEncoding"/> when the field is 0
    /// </summary>
    public SqliteEncoding TextEncoding => _reader.TextEncoding;

    internal Sqlite3Settings Settings => _reader.Settings;

    /// <summary>
    /// True when the database is in WAL mode (file format read version 2, header byte 19). Such a database may have
    /// committed changes in a <c>-wal</c> file next to it, which only <see cref="FromPath"/> and
    /// <see cref="FromStreamWithWal"/> read.
    /// </summary>
    /// <remarks>
    /// The flag stays set after the last connection closes and SQLite deletes the <c>-wal</c> file, so it does not mean
    /// a <c>-wal</c> file exists. SQLite itself looks for one on every open, whatever the flag says.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3341 (lockBtree)
    /// </remarks>
    public bool IsWalMode => _reader.Header.ReadVersion == FileReadVersion.WAL;

    /// <summary>
    /// Opens the database in <paramref name="main"/>, with its optional rollback journal and WAL. The database owns the
    /// files and disposes them, also when the open fails.
    /// </summary>
    internal Sqlite3Database(IFileView main, IFileView? journal, IFileView? wal, Sqlite3Settings settings)
    {
        // A copy, so the caller changing the settings later can't change how this database reads
        _reader = ReaderBase.Open(main, journal, wal, settings.Clone());

        try
        {
            _masterTable = OpenMasterTable(out _masterSchemaRow);
            _tablesByName = IndexTables(_masterTable.Tables, out _errorsByName);
        }
        catch
        {
            DisposeQuietly(_reader);
            throw;
        }
    }

    /// <summary>
    /// Opens a database from <paramref name="database"/>, a seekable stream of the main database file, and nothing
    /// else: it must have no <c>-wal</c> file and no hot <c>-journal</c> next to it.
    /// </summary>
    /// <remarks>
    /// SQLite keeps committed changes in a <c>-wal</c> file until a checkpoint copies them into the main file, and a
    /// crashed write in rollback mode leaves a <c>-journal</c> file that must be played back. Neither leaves a mark in
    /// the main file that this method could check, so a database opened without them may read stale or half-written
    /// data. Use <see cref="FromStreamWithWal"/> or <see cref="FromStreamWithJournal"/> when such a file exists. The
    /// database must not be written to while it is open.
    /// </remarks>
    /// <param name="database">The main database file, a seekable stream</param>
    /// <param name="settings">Settings, or null for the defaults. They are copied, so later changes have no effect.</param>
    /// <param name="leaveOpen">True to leave <paramref name="database"/> open when the database is disposed, or when opening it fails</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null</exception>
    /// <exception cref="ArgumentException"><paramref name="database"/> is not readable, or is not seekable</exception>
    /// <exception cref="SqliteCorruptException">The database is corrupt</exception>
    /// <exception cref="SqliteInvalidEncodingException">The settings' fallback encoding is not a text encoding</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings' validation is not Strict or Lenient</exception>
    public static Sqlite3Database FromStreamWithNoJournal(Stream database, Sqlite3Settings? settings = null, bool leaveOpen = false)
    {
        return FromStreams(database, null, null, settings, leaveOpen);
    }

    /// <summary>
    /// Opens a database in WAL mode from <paramref name="database"/>, the main database file, and
    /// <paramref name="wal"/>, its <c>-wal</c> file. Pages that a committed transaction in the WAL wrote are read from
    /// the WAL, as SQLite does before a checkpoint copies them into the main file.
    /// </summary>
    /// <remarks>
    /// The <c>-shm</c> file is not needed: it only caches an index of the WAL, which is rebuilt by reading the WAL, as
    /// SQLite does when the <c>-shm</c> file is missing. Frames after the last commit, and frames from before the WAL
    /// was last restarted, are ignored, as are a WAL with a broken header and an empty one. A WAL that belongs to
    /// another database gives wrong results; this cannot be detected, as SQLite cannot detect it either. Neither file
    /// may be written to while the database is open.
    /// </remarks>
    /// <param name="database">The main database file, a seekable stream</param>
    /// <param name="wal">The database's <c>-wal</c> file, a seekable stream</param>
    /// <param name="settings">Settings, or null for the defaults</param>
    /// <param name="leaveOpen">True to leave both streams open when the database is disposed, or when opening it fails</param>
    /// <exception cref="ArgumentNullException">A stream is null</exception>
    /// <exception cref="ArgumentException">A stream is not readable, or is not seekable</exception>
    /// <exception cref="SqliteCorruptException">The database is corrupt, the WAL has an unknown format version, or (in Strict) its page size differs from the database's</exception>
    /// <exception cref="SqliteInvalidEncodingException">The settings' fallback encoding is not a text encoding</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings' validation is not Strict or Lenient</exception>
    public static Sqlite3Database FromStreamWithWal(Stream database, Stream wal, Sqlite3Settings? settings = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(wal);

        return FromStreams(database, null, wal, settings, leaveOpen);
    }

    /// <summary>
    /// Opens a database in a rollback journal mode (DELETE, TRUNCATE, PERSIST) from <paramref name="database"/>, the
    /// main database file, and <paramref name="journal"/>, its <c>-journal</c> file. When the journal is hot, left by a
    /// write that never finished, it is played back in memory, so the database reads as it was before that write. The
    /// files are not changed.
    /// </summary>
    /// <remarks>
    /// A read-only SQLite connection refuses to read a database with a hot journal (SQLITE_READONLY_ROLLBACK); a
    /// writable one plays the journal back into the file first. This method gives what the writable connection would
    /// read. A journal that is not hot (empty, or with a zeroed header after a commit) is ignored. A journal of a
    /// transaction over several databases names a super-journal; it is played back whether or not the super-journal still
    /// exists, as only this one database is read. If that transaction did commit, the database reads as it was just
    /// before it. A journal that belongs to another database gives wrong results. Neither file may be written to
    /// while the database is open.
    /// </remarks>
    /// <param name="database">The main database file, a seekable stream</param>
    /// <param name="journal">The database's <c>-journal</c> file, a seekable stream</param>
    /// <param name="settings">Settings, or null for the defaults</param>
    /// <param name="leaveOpen">True to leave both streams open when the database is disposed, or when opening it fails</param>
    /// <exception cref="ArgumentNullException">A stream is null</exception>
    /// <exception cref="ArgumentException">A stream is not readable, or is not seekable</exception>
    /// <exception cref="SqliteCorruptException">The database is corrupt</exception>
    /// <exception cref="SqliteInvalidEncodingException">The settings' fallback encoding is not a text encoding</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings' validation is not Strict or Lenient</exception>
    public static Sqlite3Database FromStreamWithJournal(Stream database, Stream journal, Sqlite3Settings? settings = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(journal);

        return FromStreams(database, journal, null, settings, leaveOpen);
    }

    /// <summary>
    /// Opens the database file at <paramref name="path"/>, with the <c>-wal</c> or <c>-journal</c> file next to it
    /// when one exists, the way SQLite finds them. The <c>-shm</c> file is not read.
    /// </summary>
    /// <remarks>
    /// The files are opened for reading, allowing others to read them too. The database must not be written to while
    /// it is open, and its side files must belong to it: SQLite leaves no mark in the main file that says a side file
    /// should exist, so a deleted or misplaced journal cannot be detected. See <see cref="FromStreamWithWal"/> and
    /// <see cref="FromStreamWithJournal"/> for how each side file is read.
    /// </remarks>
    /// <param name="path">The path of the main database file</param>
    /// <param name="settings">Settings, or null for the defaults</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null</exception>
    /// <exception cref="IOException">A file could not be opened</exception>
    /// <exception cref="SqliteCorruptException">The database is corrupt, the WAL has an unknown format version, or (in Strict) its page size differs from the database's</exception>
    /// <exception cref="SqliteInvalidEncodingException">The settings' fallback encoding is not a text encoding</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings' validation is not Strict or Lenient</exception>
    public static Sqlite3Database FromPath(string path, Sqlite3Settings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        Stream? database = null, journal = null, wal = null;
        try
        {
            database = OpenRead(path);

            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5180 (hasHotJournal) and
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3385 (pagerOpenWalIfPresent)
            // SQLite looks for both on every open, whatever the header's journal mode says
            if (File.Exists(path + "-journal"))
                journal = OpenRead(path + "-journal");
            if (File.Exists(path + "-wal"))
                wal = OpenRead(path + "-wal");
        }
        catch
        {
            foreach (Stream? stream in new[] { database, journal, wal })
                stream?.Dispose();
            throw;
        }

        return FromStreams(database, journal, wal, settings, false);
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>
    /// Wraps the streams in file views and opens the database. Unless <paramref name="leaveOpen"/>, the streams are
    /// disposed when the open fails, and the database disposes them otherwise.
    /// </summary>
    private static Sqlite3Database FromStreams(Stream database, Stream? journal, Stream? wal, Sqlite3Settings? settings, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(database);

        IFileView main;
        IFileView? journalView = null, walView = null;
        try
        {
            CheckStream(database, nameof(database));
            if (journal != null)
                CheckStream(journal, nameof(journal));
            if (wal != null)
                CheckStream(wal, nameof(wal));

            // A caller error: the reader tests only for Lenient, so an undefined value would quietly read as Strict
            if (settings != null && settings.Validation is not (SqliteValidation.Strict or SqliteValidation.Lenient))
                throw new ArgumentOutOfRangeException(nameof(settings), settings.Validation, "settings.Validation must be Strict or Lenient");

            main = new StreamFileView(database, leaveOpen);
            journalView = journal != null ? new StreamFileView(journal, leaveOpen) : null;
            walView = wal != null ? new StreamFileView(wal, leaveOpen) : null;
        }
        catch
        {
            if (!leaveOpen)
            {
                foreach (Stream? stream in new[] { database, journal, wal })
                    if (stream != null)
                        DisposeQuietly(stream);
            }

            throw;
        }

        return new Sqlite3Database(main, journalView, walView, settings ?? new Sqlite3Settings());
    }

    private static void CheckStream(Stream stream, string parameterName)
    {
        // A disposed stream can neither read nor seek, so check reading first to give it the clearer message
        if (!stream.CanRead)
            throw new ArgumentException("The stream must be readable, and not disposed", parameterName);
        if (!stream.CanSeek)
            throw new ArgumentException("The stream must be seekable", parameterName);
    }

    /// <summary>
    /// Disposes after a failed open, without letting an error from the dispose hide the error that failed the open
    /// </summary>
    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch
        {
            // The open's own error is the one to report
        }
    }

    private Sqlite3MasterTable OpenMasterTable(out Sqlite3SchemaRow schemaRow)
    {
        // Parse table on Page 1, the sqlite_master table
        BTreePage rootBTree = BTreePage.Parse(_reader, 1);

        // PAGE-18: the schema table is a rowid table, read with a table cursor, so page 1 must be a table b-tree page.
        // moveToRoot in btree.c:
        //   if( pRoot->isInit==0 || (pCur->pKeyInfo==0)!=pRoot->intKey ){
        //     return SQLITE_CORRUPT_PAGE(pCur->pPage);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5609 (moveToRoot)
        Checks.Strict(rootBTree is not BTreeIndexPage, "PAGE-18", $"page 1 is an index b-tree page, but holds the schema table",
            1, "b-tree page header");

        // Fake the schema for the sqlite_master table
        const string schemaSql = "CREATE TABLE sqlite_master (type TEXT, name TEXT, tbl_name TEXT, rootpage INTEGER, sql TEXT);";
        schemaRow = new Sqlite3SchemaRow(this, "table", "sqlite_master", "sqlite_master", rootBTree.Page, schemaSql)
        {
            Kind = Sqlite3SchemaKind.Table,
            Table = SchemaParser.ParseCreateTable(schemaSql, "sqlite_master", _reader.Settings)
        };

        Sqlite3Table table = new Sqlite3Table(_reader, rootBTree, schemaRow);
        return new Sqlite3MasterTable(this, _reader, table);
    }

    /// <summary>
    /// Finds a table by name. Like SQLite, the name is matched case-insensitively against <c>sqlite_master.name</c>,
    /// and <c>sqlite_master</c> / <c>sqlite_schema</c> resolve to the schema table itself.
    /// Virtual tables cannot be read and are not returned; see <see cref="Sqlite3SchemaRow.IsVirtual"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null</exception>
    /// <exception cref="SqliteCorruptException">In Lenient, the row by that name failed to load; or the table's root
    /// page is malformed, or is a b-tree page of the other kind than its SQL declares (PAGE-18)</exception>
    public bool TryGetTable(string name, [NotNullWhen(true)] out Sqlite3Table? table)
    {
        Sqlite3SchemaRow? tbl = FindTable(name);

        if (tbl == null || tbl.IsVirtual)
        {
            table = null;
            return false;
        }

        table = OpenTable(tbl);
        return true;
    }

    /// <summary>
    /// Gets a table by name, see <see cref="TryGetTable"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null</exception>
    /// <exception cref="KeyNotFoundException">No table with that name exists</exception>
    /// <exception cref="NotSupportedException">The table is a virtual table</exception>
    /// <exception cref="SqliteCorruptException">In Lenient, the row by that name failed to load; or the table's root
    /// page is malformed, or is a b-tree page of the other kind than its SQL declares (PAGE-18)</exception>
    public Sqlite3Table GetTable(string name)
    {
        Sqlite3SchemaRow? tbl = FindTable(name);

        if (tbl == null)
            throw new KeyNotFoundException("Unable to find table named " + name);

        if (tbl.IsVirtual)
            throw new NotSupportedException($"Table {tbl.Name} is a virtual table ({tbl.Sql}). Virtual tables have no data in the database file and cannot be read, though their shadow tables may be.");

        return OpenTable(tbl);
    }

    /// <exception cref="SqliteCorruptException">In Lenient, the row by that name failed to load</exception>
    private Sqlite3SchemaRow? FindTable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Names match as in SQLite, ignoring the case of ASCII letters only (sqlite3StrICmp in util.c), so tables named
        // "é" and "É" are two tables
        AsciiCaseInsensitiveComparer names = AsciiCaseInsensitiveComparer.Instance;

        if (names.Equals(name, "sqlite_master") || names.Equals(name, "sqlite_schema"))
            return _masterSchemaRow;

        if (_tablesByName.TryGetValue(name, out Sqlite3SchemaRow? tbl))
            return tbl;

        // In Lenient, a row that failed to load made nothing in the schema, as with writable_schema in SQLite, so a
        // later row may still make a table of its name. The error is thrown only when no table has the name.
        return _errorsByName != null && _errorsByName.TryGetValue(name, out SqliteCorruptException? error) ? throw error : null;
    }

    private static Dictionary<string, Sqlite3SchemaRow> IndexTables(List<Sqlite3SchemaRow> rows, out Dictionary<string, SqliteCorruptException>? errors)
    {
        AsciiCaseInsensitiveComparer names = AsciiCaseInsensitiveComparer.Instance;
        Dictionary<string, Sqlite3SchemaRow> tables = new Dictionary<string, Sqlite3SchemaRow>(names);
        errors = null;

        foreach (Sqlite3SchemaRow row in rows)
        {
            // A table is known by what its statement made, as in SQLite. The type column must agree in Strict
            // (SCHEMA-26), in any case, so "TABLE" is a table too.
            if (row.Error != null)
            {
                errors ??= new Dictionary<string, SqliteCorruptException>(names);
                if (!errors.ContainsKey(row.Name))
                    errors.Add(row.Name, row.Error);
            }
            else if (row.IsTable && !tables.ContainsKey(row.Name))
                tables.Add(row.Name, row);
        }

        return tables;
    }

    private Sqlite3Table OpenTable(Sqlite3SchemaRow tbl)
    {
        // Parsed to check it and learn its kind, into an array the table keeps for reading its rows
        BTreePage root = BTreePage.Parse(_reader, tbl.RootPage);

        // PAGE-18: a table cursor needs a table b-tree root, and a WITHOUT ROWID (index) cursor an index b-tree root,
        // so a record is never decoded with the other kind's column order. moveToRoot in btree.c:
        //   if( pRoot->isInit==0 || (pCur->pKeyInfo==0)!=pRoot->intKey ){
        //     return SQLITE_CORRUPT_PAGE(pCur->pPage);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5609 (moveToRoot)
        // SQLite reports it on the table's first row read; here the root is read when the table is opened, as are
        // the root page's other checks.
        // A table row that failed to parse has an Error, which FindTable threw before the table got here
        bool sqlWithoutRowId = tbl.Table!.Has(ParsedTable.TF_WithoutRowid);
        Checks.Strict(sqlWithoutRowId == root is BTreeIndexPage, "PAGE-18",
            $"the SQL of table {tbl.Name} declares {(sqlWithoutRowId ? "WITHOUT ROWID" : "a rowid table")}, but its root page is {(root is BTreeIndexPage ? "an index" : "a table")} b-tree page",
            tbl.RootPage, "b-tree page header");

        return new Sqlite3Table(_reader, root, tbl);
    }

    /// <summary>
    /// Every row of the schema table (<c>sqlite_schema</c>), in its row order, which is the order SQLite loads them,
    /// whatever their kind: tables, virtual tables, indexes, views and triggers, and in
    /// <see cref="SqliteValidation.Lenient"/> rows that failed to load. Each row's
    /// <see cref="Sqlite3SchemaRow.Kind"/> says what its statement made. The schema table itself is not a row.
    /// </summary>
    public IEnumerable<Sqlite3SchemaRow> GetSchema() => _masterTable.Tables;

    /// <summary>
    /// The rows of <see cref="GetSchema"/> that made a table: ordinary tables, which include a virtual table's shadow
    /// tables, and virtual tables, which <see cref="GetTable"/> cannot read (see <see cref="Sqlite3SchemaRow.IsVirtual"/>).
    /// Indexes, views, triggers and rows that made nothing are left out.
    /// </summary>
    public IEnumerable<Sqlite3SchemaRow> GetTables() => GetSchema().Where(s => s.IsTable);

    /// <summary>
    /// Disposes the database's files: the streams it was opened from, unless they were passed with
    /// <c>leaveOpen</c>, or the files <see cref="FromPath"/> opened. Its tables must not be read afterwards.
    /// </summary>
    public void Dispose()
    {
        _reader.Dispose();
    }
}