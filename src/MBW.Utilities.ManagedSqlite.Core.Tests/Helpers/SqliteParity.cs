using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;

/// <summary>
/// Builds an in-memory database with real SQLite from a SQL script, then dumps it both with SQLite and with this library into
/// the same text format and asserts the two dumps are equal.
/// <para>
/// The dump holds everything the library promises to read: the text encoding, every sqlite_master entry (type, name,
/// table name and SQL, in file order), and for every table its column names and its rows in enumeration order, each
/// with its rowid and every column value with its storage class. Virtual tables are listed but not read, since their
/// contents are not in the file; their shadow tables are ordinary tables and are read. Views, indexes and triggers
/// are listed only, as the library does not run queries.
/// </para>
/// </summary>
internal static class SqliteParity
{
    public static void AssertSameAsSqlite(string script)
    {
        // An in-memory database, nothing touches the disk
        using SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = script;
            command.ExecuteNonQuery();
        }

        string expected = DumpWithSqlite(connection);

        string actual;
        using (Stream stream = new MemoryStream(Serialize(connection)))
            actual = DumpWithLibrary(stream);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The database file's bytes, exactly as SQLite would write them to disk.
    /// </summary>
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

    public static string DumpWithSqlite(SqliteConnection connection)
    {
        StringBuilder sb = new StringBuilder();

        string encoding = Query(connection, "PRAGMA encoding").Single()[0].ToString();
        sb.Append("encoding ").AppendLine(encoding.Replace("-", "").ToUpperInvariant());

        List<object[]> schema = Query(connection, "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY rowid");
        foreach (object[] entry in schema)
            AppendSchemaEntry(sb, (string)entry[0], (string)entry[1], (string)entry[2], entry[3] as string);

        foreach (object[] entry in schema.Where(s => IsReadableTable((string)s[0], s[3] as string)))
        {
            string name = (string)entry[1];
            string quoted = "\"" + name.Replace("\"", "\"\"") + "\"";

            List<string> columns = Query(connection, $"PRAGMA table_info({quoted})").Select(s => (string)s[1]).ToList();
            AppendTableHeader(sb, name, columns);

            foreach (object[] row in Query(connection, $"SELECT rowid, * FROM {quoted} ORDER BY rowid"))
                AppendRow(sb, (long)row[0], row.Skip(1));
        }

        return sb.ToString();
    }

    public static string DumpWithLibrary(Stream stream)
    {
        StringBuilder sb = new StringBuilder();

        using Sqlite3Database db = new Sqlite3Database(stream);

        sb.Append("encoding ").AppendLine(db.Header.TextEncoding.ToString());

        List<Sqlite3SchemaRow> schema = db.GetTables().ToList();
        foreach (Sqlite3SchemaRow entry in schema)
            AppendSchemaEntry(sb, entry.Type, entry.Name, entry.TableName, entry.Sql);

        foreach (Sqlite3SchemaRow entry in schema.Where(s => IsReadableTable(s.Type, s.Sql)))
        {
            Sqlite3Table table = db.GetTable(entry.Name);
            List<string> columns = table.GetTableDefinition().Columns.OrderBy(s => s.Ordinal).Select(s => s.Name).ToList();
            AppendTableHeader(sb, entry.Name, columns);

            foreach (Sqlite3Row row in table.EnumerateRows())
            {
                // By name, so a rowid alias column reads the rowid as SQLite does. A column missing from the record
                // (added by ALTER TABLE after the row was written) reads as NULL.
                IEnumerable<object> values = columns.Select(c => row.TryGetValueByName(c, out object value) ? value : null);
                AppendRow(sb, row.RowId, values);
            }
        }

        return sb.ToString();
    }

    private static bool IsReadableTable(string type, string sql)
    {
        return type == "table" && (sql == null || !sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendSchemaEntry(StringBuilder sb, string type, string name, string tableName, string sql)
    {
        sb.Append("schema ").Append(type).Append(' ').Append(Quote(name)).Append(" on ").Append(Quote(tableName))
            .Append(": ").AppendLine(sql == null ? "NULL" : Quote(sql));
    }

    private static void AppendTableHeader(StringBuilder sb, string name, List<string> columns)
    {
        sb.Append("table ").Append(Quote(name)).Append(" (").Append(string.Join(", ", columns.Select(Quote))).AppendLine(")");
    }

    private static void AppendRow(StringBuilder sb, long rowId, IEnumerable<object> values)
    {
        sb.Append("  row ").Append(rowId).Append(": ").AppendLine(string.Join(", ", values.Select(FormatValue)));
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
