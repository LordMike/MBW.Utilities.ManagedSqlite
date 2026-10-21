using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;

/// <summary>
/// Builds an in-memory database with real SQLite from a SQL script, reads it both with SQLite and with this library,
/// and asserts the two dumps are equal.
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

    private static void AssertSameAsSqlite(byte[] data)
    {
        string expected;
        using (SqliteConnection connection = Open(data))
        {
            List<object[]> schema = Query(connection, "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY rowid");
            expected = Dump(Query(connection, "PRAGMA encoding")[0][0].ToString().Replace("-", "").ToUpperInvariant(),
                Query(connection, "PRAGMA page_size")[0][0].ToString(),
                schema.Select(s => ((string)s[0], (string)s[1], (string)s[2], s[3] as string)),
                schema.Where(s => IsReadableTable((string)s[0], s[3] as string)).Select(s => ReadWithSqlite(connection, (string)s[1])));
        }

        string actual;
        using (Sqlite3Database db = new Sqlite3Database(new MemoryStream(data)))
        {
            List<Sqlite3SchemaRow> schema = db.GetTables().ToList();
            actual = Dump(db.Header.TextEncoding.ToString(), db.Header.PageSize.ToString(CultureInfo.InvariantCulture),
                schema.Select(s => (s.Type, s.Name, s.TableName, s.Sql)),
                schema.Where(s => IsReadableTable(s.Type, s.Sql)).Select(s => ReadWithLibrary(db, s.Name)));
        }

        Assert.Equal(expected, actual);
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
    /// The bytes of the database file real SQLite builds from <paramref name="script"/>, exactly as SQLite would write
    /// them to disk (https://www.sqlite.org/c3ref/serialize.html).
    /// </summary>
    public static byte[] Build(string script, int reservedBytes = 0)
    {
        using SqliteConnection connection = Run(script, reservedBytes);

        IntPtr data = raw.sqlite3_serialize(connection.Handle, "main", out long size, 0);
        if (data == IntPtr.Zero)
            throw new InvalidOperationException("sqlite3_serialize failed");

        byte[] bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, (int)size);
        raw.sqlite3_free(data);
        return bytes;
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
    /// deserialize itself fails (end_deserialize in https://sqlite.org/src/file?name=src/memdb.c).
    /// </summary>
    private static SqliteConnection Open(byte[] data)
    {
        SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        IntPtr copy = raw.sqlite3_malloc(data.Length);
        Marshal.Copy(data, 0, copy, data.Length);
        int rc = raw.sqlite3_deserialize(connection.Handle, "main", copy, data.Length, data.Length, raw.SQLITE_DESERIALIZE_FREEONCLOSE | raw.SQLITE_DESERIALIZE_READONLY);
        if (rc != raw.SQLITE_OK)
        {
            connection.Dispose();
            throw new InvalidOperationException($"sqlite3_deserialize failed: {rc}");
        }

        return connection;
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
            .Select(row => (table.IsWithoutRowId ? null : (long?)row.RowId, stored.Select(c => row.TryGetValueByName(c, out object value) ? value : null).ToArray()))
            .ToList();

        return new Table(name, definition.RowIdColumn?.Name, columns, rows);
    }

    private static bool IsReadableTable(string type, string sql)
    {
        return type == "table" && (sql == null || !sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase));
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
