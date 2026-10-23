using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests.Helpers;

/// <summary>
/// Builds an in-memory database with real SQLite from a SQL script, reads it both with SQLite and with this library,
/// and asserts the two dumps are equal. <see cref="Snapshot"/> does the same for a database file and its <c>-wal</c>
/// or <c>-journal</c> file, as a crash would leave them.
/// <para>
/// The dump starts with the schema: the text encoding, the page size, every sqlite_master entry in file order, and per
/// table its rowid alias and per column its name, declared type (without its size, in upper case), primary key
/// membership and whether it is a VIRTUAL generated column. Then the data: every table's rows in enumeration order,
/// each with its rowid (none in a WITHOUT ROWID table) and every stored value with its storage class. VIRTUAL generated
/// columns have no value, as SQLite computes them when read. Virtual tables, views, indexes and triggers are only
/// listed; the shadow tables of a virtual table are ordinary tables and are read.
/// </para>
/// </summary>
internal static class SqliteParity
{
    private sealed record Column(string Name, string Type, bool Pk, bool Virtual);

    private sealed record Table(string Name, string RowIdAlias, List<Column> Columns, List<(long? RowId, object[] Values)> Rows);

    public static void AssertSameAsSqlite(string script) => AssertSameAsSqlite(Build(script));

    /// <summary>
    /// <see cref="AssertSameAsSqlite(string)"/> for the database <see cref="Build(string, string, int)"/> makes, with
    /// <paramref name="schemaEdit"/> changing one sqlite_schema row
    /// </summary>
    public static void AssertSameAsSqlite(string script, string schemaEdit) => AssertSameAsSqlite(Build(script, schemaEdit));

    /// <summary>
    /// For a database with <paramref name="reservedBytes"/> unused bytes at the end of every page (the header's
    /// "reserved space", offset 20), which no SQL statement can set.
    /// </summary>
    public static void AssertSameAsSqlite(int reservedBytes, string script)
    {
        byte[] data = Build(script, reservedBytes);
        Assert.Equal(reservedBytes, data[20]);
        AssertSameAsSqlite(data);
    }

    /// <summary>
    /// After <paramref name="patch"/> has changed the file's bytes, for cases SQLite itself never writes.
    /// </summary>
    public static void AssertSameAsSqlite(string script, Func<byte[], byte[]> patch) => AssertSameAsSqlite(patch(Build(script)));

    /// <summary>
    /// After <paramref name="patch"/>, with the library opening the file with <paramref name="settings"/>
    /// </summary>
    public static void AssertSameAsSqlite(string script, Func<byte[], byte[]> patch, Sqlite3Settings settings) => AssertSameAsSqlite(patch(Build(script)), settings);

    private static void AssertSameAsSqlite(byte[] data, Sqlite3Settings settings = null)
    {
        string expected;
        using (SqliteConnection connection = Open(data))
            expected = DumpWithSqlite(connection);

        string actual;
        using (Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(data), settings))
            actual = DumpWithLibrary(db);

        Assert.Equal(expected, actual);
    }

    private static string DumpWithSqlite(SqliteConnection connection)
    {
        // The schema as SQLite's schema loader sees it: every value as text, ending at a NUL (SCHEMA-08, SCHEMA-09)
        List<object[]> schema = Query(connection, "SELECT CAST(type AS TEXT), CAST(name AS TEXT), CAST(tbl_name AS TEXT), CAST(sql AS TEXT) FROM sqlite_master ORDER BY rowid")
            .Select(r => r.Select(v => v is string text && text.IndexOf('\0') >= 0 ? text.Substring(0, text.IndexOf('\0')) : v).ToArray()).ToList();
        return Dump(Query(connection, "PRAGMA encoding")[0][0].ToString().Replace("-", "").ToUpperInvariant(),
            Query(connection, "PRAGMA page_size")[0][0].ToString(),
            schema.Select(s => ((string)s[0], (string)s[1], (string)s[2], s[3] as string)),
            schema.Where(s => IsReadableTable((string)s[0], s[3] as string)).Select(s => ReadWithSqlite(connection, (string)s[1])));
    }

    private static string DumpWithLibrary(Sqlite3Database db)
    {
        List<Sqlite3SchemaRow> schema = db.GetTables().ToList();
        return Dump(db.TextEncoding.ToString(), db.Header.PageSize.ToString(CultureInfo.InvariantCulture),
            schema.Select(s => (s.Type, s.Name, s.TableName, s.Sql)),
            schema.Where(s => IsReadableTable(s.Type, s.Sql)).Select(s => ReadWithLibrary(db, s.Name)));
    }

    /// <summary>
    /// Runs <paramref name="script"/> against a database file, "db", in a new directory, and copies the database with
    /// its <c>-wal</c> and <c>-journal</c> files to another new directory while the connection is still open, as a crash
    /// at that point would leave them. A script that ends inside a transaction leaves it uncommitted, and a WAL is not
    /// checkpointed (wal_autocheckpoint is 0). The <c>-shm</c> file is not copied, as the library never reads it.
    /// Returns the copy's directory; dispose it to delete both.
    /// </summary>
    public static SnapshotDirectory Snapshot(string script)
    {
        using SnapshotDirectory source = new SnapshotDirectory();
        using SqliteConnection connection = new SqliteConnection($"Data Source={source.Database};Pooling=False");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_autocheckpoint = 0;" + script;
        command.ExecuteNonQuery();

        return source.Copy();
    }

    /// <summary>
    /// Asserts that <see cref="Sqlite3Database.FromPath"/> reads the database in <paramref name="snapshot"/> as SQLite
    /// reads it, opened read-write so that it plays a hot journal back and recovers a WAL without its -shm file.
    /// SQLite reads a copy, as it changes the files. With <paramref name="sideFilesMatter"/>, also asserts that
    /// the main file alone reads differently, so the case does exercise the side files.
    /// </summary>
    public static void AssertSameAsSqlite(SnapshotDirectory snapshot, bool sideFilesMatter)
    {
        string actual;
        using (Sqlite3Database db = Sqlite3Database.FromPath(snapshot.Database))
            actual = DumpWithLibrary(db);

        if (sideFilesMatter)
        {
            string mainOnly;
            try
            {
                using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(File.OpenRead(snapshot.Database));
                mainOnly = DumpWithLibrary(db);
            }
            catch (Exception e)
            {
                mainOnly = e.ToString();
            }

            Assert.NotEqual(actual, mainOnly);
        }

        using SnapshotDirectory copy = snapshot.Copy();

        string expected;
        using (SqliteConnection connection = new SqliteConnection($"Data Source={copy.Database};Pooling=False"))
        {
            connection.Open();
            expected = DumpWithSqlite(connection);
        }

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Asserts that SQLite fails with <paramref name="errorCode"/> when it reads the database in
    /// <paramref name="snapshot"/>
    /// </summary>
    public static void AssertSqliteFails(SnapshotDirectory snapshot, int errorCode)
    {
        using SnapshotDirectory copy = snapshot.Copy();

        using SqliteConnection connection = new SqliteConnection($"Data Source={copy.Database};Pooling=False");
        connection.Open();
        SqliteException ex = Assert.Throws<SqliteException>(() => Query(connection, "SELECT * FROM sqlite_master"));
        Assert.Equal(errorCode, ex.SqliteErrorCode);
    }

    /// <summary>
    /// A new temporary directory, deleted on dispose. <see cref="Database"/> is the path of the database file in it.
    /// </summary>
    public sealed class SnapshotDirectory : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "managedsqlite-" + Guid.NewGuid().ToString("N"));

        public string Database { get; }

        public SnapshotDirectory()
        {
            Directory.CreateDirectory(_directory);
            Database = Path.Combine(_directory, "db");
        }

        /// <summary>
        /// Copies the database and its -wal and -journal files, not -shm, to a new directory
        /// </summary>
        public SnapshotDirectory Copy()
        {
            SnapshotDirectory copy = new SnapshotDirectory();
            try
            {
                foreach (string suffix in new[] { "", "-wal", "-journal" })
                    if (File.Exists(Database + suffix))
                        File.Copy(Database + suffix, copy.Database + suffix);

                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Directory.Delete(_directory, true);
        }
    }

    /// <summary>
    /// Asserts that this library reads <paramref name="tables"/> from <paramref name="db"/> as SQLite reads them from
    /// the database <paramref name="script"/> builds. For a damaged copy of that database, whose other tables must
    /// still read.
    /// </summary>
    public static void AssertTablesSameAsSqlite(string script, Sqlite3Database db, params string[] tables)
    {
        using SqliteConnection connection = Run(script);

        StringBuilder expected = new StringBuilder();
        StringBuilder actual = new StringBuilder();
        foreach (string table in tables)
        {
            AppendTable(expected, ReadWithSqlite(connection, table));
            AppendTable(actual, ReadWithLibrary(db, table));
        }

        Assert.Equal(expected.ToString(), actual.ToString());
    }

    /// <summary>
    /// Asserts that real SQLite reports SQLITE_CORRUPT when it reads the rows of <paramref name="table"/> from the
    /// database file <paramref name="data"/>, for a damaged database the library must also reject.
    /// </summary>
    public static void AssertSqliteReportsCorrupt(byte[] data, string table)
    {
        using SqliteConnection connection = Open(data);
        SqliteException ex = Assert.Throws<SqliteException>(() => Query(connection, $"SELECT * FROM {Name(table)}"));
        Assert.Equal(raw.SQLITE_CORRUPT, ex.SqliteErrorCode);
    }

    /// <summary>
    /// Asserts that real SQLite loads the schema of the database file <paramref name="data"/> and reads the rows of
    /// <paramref name="table"/>
    /// </summary>
    public static void AssertSqliteReads(byte[] data, string table)
    {
        using SqliteConnection connection = Open(data);
        Query(connection, $"SELECT * FROM {Name(table)}");
    }

    /// <summary>
    /// Asserts that real SQLite fails with an error, any error, when it reads the rows of <paramref name="table"/> from
    /// the database file <paramref name="data"/>
    /// </summary>
    public static void AssertSqliteFails(byte[] data, string table)
    {
        using SqliteConnection connection = Open(data);
        Assert.Throws<SqliteException>(() => Query(connection, $"SELECT * FROM {Name(table)}"));
    }

    /// <summary>
    /// The bytes of the database file real SQLite builds from <paramref name="script"/>, exactly as SQLite would write
    /// them to disk (https://www.sqlite.org/c3ref/serialize.html).
    /// </summary>
    public static byte[] Build(string script, int reservedBytes = 0)
    {
        using SqliteConnection connection = Run(script, reservedBytes);
        return Serialize(connection);
    }

    /// <summary>
    /// <see cref="Build(string, int)"/>, then <paramref name="schemaEdit"/> with writable_schema on, which must change
    /// exactly <paramref name="rows"/> rows of sqlite_schema, so an edit that matches nothing fails instead of testing
    /// the undamaged schema
    /// </summary>
    public static byte[] Build(string script, string schemaEdit, int rows = 1)
    {
        using SqliteConnection connection = Run(script);
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA writable_schema = ON";
            pragma.ExecuteNonQuery();
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = schemaEdit;
            Assert.Equal(rows, command.ExecuteNonQuery());
        }

        return Serialize(connection);
    }

    private static byte[] Serialize(SqliteConnection connection)
    {
        IntPtr data = raw.sqlite3_serialize(connection.Handle, "main", out long size, 0);
        if (data == IntPtr.Zero)
            throw new InvalidOperationException("sqlite3_serialize failed");

        try
        {
            byte[] bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            raw.sqlite3_free(data);
        }
    }

    /// <summary>
    /// Runs <paramref name="script"/> against a new in-memory database. A database with <paramref name="reservedBytes"/>
    /// gets them before the script runs, through SQLITE_FCNTL_RESERVE_BYTES
    /// (https://www.sqlite.org/c3ref/c_fcntl_begin_atomic_write.html#sqlitefcntlreservebytes).
    /// </summary>
    private static SqliteConnection Run(string script, int reservedBytes = 0)
    {
        SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        if (reservedBytes != 0 && sqlite3_file_control(connection.Handle.DangerousGetHandle(), "main", SqliteFcntlReserveBytes, ref reservedBytes) != raw.SQLITE_OK)
            throw new InvalidOperationException("sqlite3_file_control failed");

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();

        return connection;
    }

    private const int SqliteFcntlReserveBytes = 38;

    // SQLitePCLRaw does not wrap sqlite3_file_control, so it is called in the SQLite library it loaded, e_sqlite3
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_file_control(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string dbName, int op, ref int arg);

    /// <summary>
    /// Opens a database file's bytes with SQLite, read-only and in memory, through sqlite3_deserialize
    /// (https://www.sqlite.org/c3ref/deserialize.html), so SQLite reads exactly the bytes the library is given. SQLite
    /// owns the copy (SQLITE_DESERIALIZE_FREEONCLOSE) and frees it when the connection closes, and also when
    /// deserialize itself fails (end_deserialize in https://github.com/sqlite/sqlite/blob/version-3.53.4/src/memdb.c#L904).
    /// </summary>
    private static SqliteConnection Open(byte[] data)
    {
        SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        try
        {
            IntPtr copy = raw.sqlite3_malloc(Math.Max(1, data.Length));
            if (copy == IntPtr.Zero)
                throw new OutOfMemoryException();

            Marshal.Copy(data, 0, copy, data.Length);
            int rc = raw.sqlite3_deserialize(connection.Handle, "main", copy, data.Length, data.Length, raw.SQLITE_DESERIALIZE_FREEONCLOSE | raw.SQLITE_DESERIALIZE_READONLY);
            if (rc != raw.SQLITE_OK)
                throw new InvalidOperationException($"sqlite3_deserialize failed: {rc}");

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// SQLite's view of a table: pragma_table_list (wr), pragma_table_xinfo (name, type, pk, hidden = 2 for VIRTUAL
    /// generated) and pragma_index_list. A rowid table (wr = 0) whose primary key has no automatic index (origin 'pk')
    /// has a rowid alias, its one pk column. <see cref="SqlTableColumn.TypeName"/> leaves out the size, "(10, 5)", so
    /// it is cut here too. The definition exposes neither STRICT nor the primary key's column order, so those are not
    /// compared; WITHOUT ROWID shows through the rowid alias.
    /// </summary>
    private static Table ReadWithSqlite(SqliteConnection connection, string name)
    {
        bool withoutRowId = (long)Query(connection, $"SELECT wr FROM pragma_table_list WHERE schema = 'main' AND name = {Literal(name)}")[0][0] != 0;
        List<Column> columns = Query(connection, $"SELECT name, type, pk, hidden FROM pragma_table_xinfo({Literal(name)}) ORDER BY cid")
            .Select(c => new Column((string)c[0], Regex.Replace(Regex.Replace((string)c[1], @"\(.*\)$", ""), @"\s+", " ").Trim().ToUpperInvariant(), (long)c[2] > 0, (long)c[3] == 2))
            .ToList();

        List<Column> pk = columns.Where(c => c.Pk).ToList();
        bool pkHasIndex = Query(connection, $"SELECT 1 FROM pragma_index_list({Literal(name)}) WHERE origin = 'pk'").Any();
        string rowIdAlias = !withoutRowId && pk.Count == 1 && !pkHasIndex ? pk[0].Name : null;

        string values = string.Join(", ", columns.Where(c => !c.Virtual).Select(c => Name(c.Name)));
        string sql = withoutRowId ? $"SELECT NULL, {values} FROM {Name(name)}" : $"SELECT rowid, {values} FROM {Name(name)} ORDER BY rowid";
        return new Table(name, rowIdAlias, columns, Query(connection, sql).Select(r => (r[0] as long?, r.Skip(1).ToArray())).ToList());
    }

    private static Table ReadWithLibrary(Sqlite3Database db, string name)
    {
        Sqlite3Table table = db.GetTable(name);
        SqlTableDefinition definition = table.GetTableDefinition();
        List<Column> columns = definition.Columns.OrderBy(c => c.Ordinal)
            .Select(c => new Column(c.Name, c.TypeName.ToUpperInvariant(), c.IsPartOfPrimaryKey, c.IsVirtualGenerated)).ToList();

        // By name, so a rowid alias column reads the rowid as SQLite does. A column missing from the record (added by
        // ALTER TABLE after the row was written) reads as NULL.
        List<string> stored = columns.Where(c => !c.Virtual).Select(c => c.Name).ToList();
        List<(long?, object[])> rows = table.EnumerateRows()
            .Select(row =>
            {
                object[] values = stored.Select(c => row.TryGetValueByName(c, out object value) ? value : null).ToArray();
                AssertBlobsSameAsValues(row, stored, definition.RowIdColumn?.Name);
                return (table.IsWithoutRowId ? null : (long?)row.RowId, values);
            })
            .ToList();

        return new Table(name, definition.RowIdColumn?.Name, columns, rows);
    }

    /// <summary>
    /// <see cref="Sqlite3Row.TryGetBlob"/> returns the bytes <see cref="Sqlite3Row.TryGetOrdinal(ushort, out object)"/>
    /// returns for each BLOB field of the <paramref name="stored"/> columns, and false for every other field. The rowid
    /// alias is skipped: it reads the rowid, so its field is never decoded, as in SQLite.
    /// </summary>
    private static void AssertBlobsSameAsValues(Sqlite3Row row, List<string> stored, string rowIdAlias)
    {
        for (ushort i = 0; i < stored.Count; i++)
        {
            if (stored[i] == rowIdAlias)
                continue;

            bool exists = row.TryGetOrdinal(i, out object value);
            bool isBlob = row.TryGetBlob(i, out ReadOnlyMemory<byte> blob);
            Assert.Equal(exists && value is byte[], isBlob);
            if (isBlob)
                Assert.Equal((byte[])value, blob.ToArray());
        }
    }

    private static bool IsReadableTable(string type, string sql)
    {
        return string.Equals(type, "table", StringComparison.OrdinalIgnoreCase) && (sql == null || !Regex.IsMatch(sql, @"^\s*CREATE\s+(/\*.*?\*/\s*)*VIRTUAL\s", RegexOptions.IgnoreCase | RegexOptions.Singleline));
    }

    /// <summary>
    /// The schema of every table before any table's rows, so a schema difference is the first difference shown
    /// </summary>
    private static string Dump(string encoding, string pageSize, IEnumerable<(string type, string name, string tableName, string sql)> schema, IEnumerable<Table> tables)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("encoding ").AppendLine(encoding);
        sb.Append("page size ").AppendLine(pageSize);

        foreach ((string type, string name, string tableName, string sql) in schema)
            sb.Append("schema ").Append(type).Append(' ').Append(Quote(name)).Append(" on ").Append(Quote(tableName)).Append(": ").AppendLine(Quote(sql));

        List<Table> list = tables.ToList();
        list.ForEach(t => AppendColumns(sb, t));
        list.ForEach(t => AppendRows(sb, t));
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, Table table)
    {
        AppendColumns(sb, table);
        AppendRows(sb, table);
    }

    private static void AppendColumns(StringBuilder sb, Table table)
    {
        sb.Append("columns of ").Append(Quote(table.Name)).Append(", rowid alias ").AppendLine(Quote(table.RowIdAlias));
        foreach (Column c in table.Columns)
            sb.Append("  ").Append(Quote(c.Name)).Append(' ').Append(Quote(c.Type)).Append(c.Pk ? " pk" : "").AppendLine(c.Virtual ? " virtual" : "");
    }

    private static void AppendRows(StringBuilder sb, Table table)
    {
        sb.Append("rows of ").AppendLine(Quote(table.Name));
        foreach ((long? rowId, object[] values) in table.Rows)
            sb.Append("  ").Append(rowId?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(": ").AppendLine(string.Join(", ", values.Select(FormatValue)));
    }

    private static string FormatValue(object value)
    {
        switch (value)
        {
            case null:
            case DBNull _:
                return "NULL";
            case long l:
                return "integer " + l.ToString(CultureInfo.InvariantCulture);
            case double d:
                return "real " + d.ToString("R", CultureInfo.InvariantCulture);
            case string s:
                return "text " + Quote(s);
            case byte[] b:
                return "blob " + BitConverter.ToString(b).Replace("-", "");
            default:
                // Any other CLR type is a mismatch by itself, SQLite only has the five storage classes above
                return value.GetType().Name + " " + Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A SQL string literal
    /// </summary>
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// A SQL identifier
    /// </summary>
    private static string Name(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// A value in the dump, escaped to stay on one line
    /// </summary>
    private static string Quote(string value)
    {
        if (value == null)
            return "NULL";

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static List<object[]> Query(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        using SqliteDataReader reader = command.ExecuteReader();
        List<object[]> rows = new List<object[]>();

        while (reader.Read())
        {
            object[] row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }
}
