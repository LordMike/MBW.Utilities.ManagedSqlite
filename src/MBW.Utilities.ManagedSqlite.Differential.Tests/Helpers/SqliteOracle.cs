using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace MBW.Utilities.ManagedSqlite.Differential.Tests.Helpers;

internal sealed class ExpectedRow
{
    /// <summary>The rowid, or null for a WITHOUT ROWID table.</summary>
    public long? RowId { get; init; }

    /// <summary>
    /// The values of <c>SELECT *</c>: long, double, string, byte[] or null. The CLR type is the storage class SQLite
    /// reports (sqlite3_column_type, the same thing <c>typeof()</c> returns).
    /// </summary>
    public object[] Values { get; init; }
}

internal sealed class ExpectedTable
{
    /// <summary>The name exactly as stored in sqlite_schema.name.</summary>
    public string Name { get; init; }

    /// <summary>sqlite_schema type as SQLite's pragma_table_list reports it: table, virtual, shadow...</summary>
    public string Kind { get; init; }

    public bool WithoutRowId { get; init; }

    public string[] ColumnNames { get; init; }

    /// <summary>
    /// Ordinal (in <see cref="ColumnNames"/>) of the INTEGER PRIMARY KEY column that aliases the rowid, or -1.
    /// SQLite stores NULL in the record for that column, so the raw ColumnData holds null there.
    /// </summary>
    public int RowIdAliasOrdinal { get; init; }

    public List<ExpectedRow> Rows { get; init; }
}

/// <summary>
/// Reads a table through real SQLite, which is the oracle the library is compared against.
/// </summary>
internal static class SqliteOracle
{
    public static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Reads <c>SELECT rowid, * FROM name ORDER BY rowid</c> (or <c>SELECT *</c> for WITHOUT ROWID tables, which SQLite
    /// returns in primary key order). The name is resolved the way SQLite resolves it, case-insensitively.
    /// </summary>
    public static ExpectedTable ReadTable(SqliteConnection connection, string name)
    {
        string storedName, kind;
        bool withoutRowId;

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, type, wr FROM pragma_table_list WHERE schema = 'main' AND name = $name COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$name", name);

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException($"SQLite has no table named '{name}'");

            storedName = reader.GetString(0);
            kind = reader.GetString(1);
            withoutRowId = reader.GetInt64(2) != 0;
        }

        string quoted = QuoteIdentifier(storedName);
        List<ExpectedRow> rows = new List<ExpectedRow>();
        string[] columnNames;

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = withoutRowId ? $"SELECT * FROM {quoted}" : $"SELECT rowid, * FROM {quoted} ORDER BY rowid";

            using SqliteDataReader reader = cmd.ExecuteReader();
            int offset = withoutRowId ? 0 : 1;
            columnNames = Enumerable.Range(offset, reader.FieldCount - offset).Select(reader.GetName).ToArray();

            while (reader.Read())
            {
                object[] values = new object[columnNames.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    object value = reader.GetValue(i + offset);
                    values[i] = value is DBNull ? null : value;
                }

                rows.Add(new ExpectedRow
                {
                    RowId = withoutRowId ? null : reader.GetInt64(0),
                    Values = values
                });
            }
        }

        int aliasOrdinal = kind == "table" && !withoutRowId ? FindRowIdAlias(connection, storedName, columnNames) : -1;

        return new ExpectedTable
        {
            Name = storedName,
            Kind = kind,
            WithoutRowId = withoutRowId,
            ColumnNames = columnNames,
            RowIdAliasOrdinal = aliasOrdinal,
            Rows = rows
        };
    }

    /// <summary>
    /// SQLite's rule: a rowid table whose primary key is a single column declared with type exactly "INTEGER" (any
    /// case). The one exception (a column constraint "INTEGER PRIMARY KEY DESC" is not an alias) is not visible in
    /// pragma_table_info, so the candidate is confirmed by checking that it equals the rowid on every row.
    /// </summary>
    private static int FindRowIdAlias(SqliteConnection connection, string table, string[] columnNames)
    {
        List<(string name, string type)> pk = new List<(string, string)>();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, type FROM pragma_table_info($table) WHERE pk > 0";
            cmd.Parameters.AddWithValue("$table", table);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                pk.Add((reader.GetString(0), reader.GetString(1)));
        }

        if (pk.Count != 1 || !pk[0].type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase))
            return -1;

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT count(*) FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(pk[0].name)} IS NOT rowid";
            if ((long)cmd.ExecuteScalar() != 0)
                return -1;
        }

        return Array.FindIndex(columnNames, s => s.Equals(pk[0].name, StringComparison.OrdinalIgnoreCase));
    }
}
