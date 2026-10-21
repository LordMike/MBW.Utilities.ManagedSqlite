using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Core;

public sealed class Sqlite3Database : IDisposable
{
    private readonly ReaderBase _reader;
    private readonly Dictionary<string, object> _propertyStore;

    private readonly Sqlite3MasterTable _masterTable;
    private readonly Sqlite3SchemaRow _masterSchemaRow;

    internal DatabaseHeader Header => _reader.Header;

    public SqliteEncoding TextEncoding => _reader.TextEncoding;

    /// <summary>
    /// True when the database is in WAL mode (file format read version 2, header byte 19). Such a database may have
    /// committed changes in a <c>-wal</c> file next to it, which this reader does not see.
    /// </summary>
    /// <remarks>
    /// The flag stays set after the last connection closes and SQLite deletes the <c>-wal</c> file, so it does not mean
    /// a <c>-wal</c> file exists. SQLite itself looks for one on every open, whatever the flag says.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3341 (lockBtree)
    /// </remarks>
    public bool IsWalMode => _reader.Header.ReadVersion == FileReadVersion.WAL;

    internal Sqlite3Database(IFileView file, Sqlite3Settings settings)
    {
        _propertyStore = new Dictionary<string, object>();

        try
        {
            try
            {
                _reader = ReaderBase.Open(file, settings);
            }
            catch
            {
                DisposeQuietly(file);
                throw;
            }

            try
            {
                _masterTable = OpenMasterTable(out _masterSchemaRow);
            }
            catch
            {
                DisposeQuietly(_reader);
                throw;
            }
        }
        catch (Exception e) when (CorruptionHelper.IsCorruptionShaped(e))
        {
            throw new SqliteCorruptException("unable to read the database header or schema", innerException: e);
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
    /// data. The database must not be written to while it is open.
    /// </remarks>
    /// <param name="database">The main database file, a seekable stream</param>
    /// <param name="settings">Settings, or null for the defaults</param>
    /// <param name="leaveOpen">True to leave <paramref name="database"/> open when the database is disposed, or when opening it fails</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null</exception>
    /// <exception cref="ArgumentException"><paramref name="database"/> is not readable and seekable</exception>
    /// <exception cref="SqliteCorruptException">The database is corrupt</exception>
    public static Sqlite3Database FromStreamWithNoJournal(Stream database, Sqlite3Settings? settings = null, bool leaveOpen = false)
    {
        if (database == null)
            throw new ArgumentNullException(nameof(database));

        StreamFileView file;
        try
        {
            file = new StreamFileView(database, leaveOpen);
        }
        catch
        {
            if (!leaveOpen)
                DisposeQuietly(database);
            throw;
        }

        return new Sqlite3Database(file, settings ?? new Sqlite3Settings());
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
        BTreePage rootBtree = BTreePage.Parse(_reader, 1);

        // Fake the schema for the sqlite_master table
        schemaRow = new Sqlite3SchemaRow(this, "table", "sqlite_master", "sqlite_master", rootBtree.Page,
            "CREATE TABLE sqlite_master (type TEXT, name TEXT, tbl_name TEXT, rootpage INTEGER, sql TEXT);");

        Sqlite3Table table = new Sqlite3Table(_reader, rootBtree, schemaRow);
        return new Sqlite3MasterTable(this, table);
    }

    /// <summary>
    /// Finds a table by name. Like SQLite, the name is matched case-insensitively against <c>sqlite_master.name</c>,
    /// and <c>sqlite_master</c> / <c>sqlite_schema</c> resolve to the schema table itself.
    /// Virtual tables cannot be read and are not returned; see <see cref="Sqlite3SchemaRow.IsVirtual"/>.
    /// </summary>
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
    /// <exception cref="KeyNotFoundException">No table with that name exists</exception>
    /// <exception cref="NotSupportedException">The table is a virtual table</exception>
    public Sqlite3Table GetTable(string name)
    {
        Sqlite3SchemaRow? tbl = FindTable(name);

        if (tbl == null)
            throw new KeyNotFoundException("Unable to find table named " + name);

        if (tbl.IsVirtual)
            throw new NotSupportedException($"Table {tbl.Name} is a virtual table ({tbl.Sql}). Virtual tables have no data in the database file and cannot be read, though their shadow tables may be.");

        return OpenTable(tbl);
    }

    private Sqlite3SchemaRow? FindTable(string name)
    {
        // Names match as in SQLite, ignoring the case of ASCII letters only (sqlite3StrICmp in util.c), so tables named
        // "é" and "É" are two tables
        AsciiCaseInsensitiveComparer names = AsciiCaseInsensitiveComparer.Instance;

        if (names.Equals(name, "sqlite_master") || names.Equals(name, "sqlite_schema"))
            return _masterSchemaRow;

        foreach (Sqlite3SchemaRow tbl in GetTables())
        {
            if (tbl.Type == "table" && names.Equals(tbl.Name, name))
                return tbl;
        }

        return null;
    }

    private Sqlite3Table OpenTable(Sqlite3SchemaRow tbl)
    {
        BTreePage root = BTreePage.Parse(_reader, tbl.RootPage);
        return new Sqlite3Table(_reader, root, tbl);
    }

    public IEnumerable<Sqlite3SchemaRow> GetTables() => _masterTable.Tables;

    internal T GetProperty<T>(string key, Func<T> creator) where T : class
    {
        if (!_propertyStore.TryGetValue(key, out var val))
            _propertyStore[key] = val = creator();

        return (T)val;
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}