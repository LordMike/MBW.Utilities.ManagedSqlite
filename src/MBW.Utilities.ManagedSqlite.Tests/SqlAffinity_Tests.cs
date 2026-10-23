using System;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Sql;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

public class SqlAffinity_Tests
{
    /// <summary>
    /// SQLite picks a column's affinity from substrings of its declared type, in this order: "INT" is INTEGER; "CHAR",
    /// "CLOB" or "TEXT" is TEXT; "BLOB", or no type, is BLOB; "REAL", "FLOA" or "DOUB" is REAL; anything else is NUMERIC
    /// (https://www.sqlite.org/datatype3.html#determination_of_column_affinity). DetectedType must agree with SQLite.
    /// </summary>
    [Theory]
    [InlineData("CHARINT")]
    [InlineData("FLOATING POINT")]
    [InlineData("VARCHAR(255)")]
    [InlineData("CLOB")]
    [InlineData("DOUBLE TEXT")]
    [InlineData("BLOB")]
    [InlineData("FLOAT BLOB")]
    [InlineData("")]
    [InlineData("REAL")]
    [InlineData("DOUBLE")]
    [InlineData("floatval")]
    [InlineData("STRING")]
    public void DetectedTypeMatchesSqliteAffinity(string declaredType)
    {
        string sql = $"CREATE TABLE t (c {declaredType})";
        Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));
        Assert.True(definition.TryGetColumn("c", out SqlTableColumn c));

        Assert.Equal(SqliteAffinityType(sql, "c"), c.DetectedType);
    }

    /// <summary>
    /// Asks SQLite which affinity it gives <paramref name="column"/>, without inserting anything. SQLite extracts the
    /// declared type from the CREATE statement, and a CAST to that type name uses the same affinity rules as a column.
    /// Casting '1' and '1.5' tells the five affinities apart: INTEGER gives integer/integer, TEXT text/text, BLOB
    /// blob/blob, REAL real/real and NUMERIC integer/real.
    /// </summary>
    private static Type SqliteAffinityType(string createTable, string column)
    {
        using SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (SqliteCommand create = new SqliteCommand(createTable, connection))
            create.ExecuteNonQuery();

        string declaredType;
        using (SqliteCommand info = new SqliteCommand("SELECT type FROM pragma_table_xinfo('t') WHERE name = $name", connection))
        {
            info.Parameters.AddWithValue("$name", column);
            declaredType = (string)info.ExecuteScalar();
        }

        // A column with no declared type has BLOB affinity, and CAST needs a type name
        if (declaredType.Length == 0)
            return typeof(byte[]);

        using SqliteCommand probe = new SqliteCommand($"SELECT typeof(CAST('1' AS {declaredType})), typeof(CAST('1.5' AS {declaredType}))", connection);
        using SqliteDataReader reader = probe.ExecuteReader();
        Assert.True(reader.Read());

        switch ((reader.GetString(0), reader.GetString(1)))
        {
            case ("integer", "integer"):
            case ("integer", "real"):
                // INTEGER and NUMERIC affinity are both read as long
                return typeof(long);
            case ("text", "text"):
                return typeof(string);
            case ("blob", "blob"):
                return typeof(byte[]);
            case ("real", "real"):
                return typeof(double);
            default:
                throw new InvalidOperationException($"Unexpected CAST results {reader.GetString(0)}/{reader.GetString(1)} for type {declaredType}");
        }
    }
}
