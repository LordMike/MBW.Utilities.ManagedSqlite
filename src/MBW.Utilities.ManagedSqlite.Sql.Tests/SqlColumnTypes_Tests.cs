using System;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

public class SqlColumnTypes_Tests
{
    /// <summary>
    /// The declared type name and primary key flag. The declared type ends at the first constraint, and DetectedType is
    /// checked against SQLite's affinity for the same statement.
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE t (c integer)", "c", "INTEGER", false)]
    [InlineData("CREATE TABLE t (c BIGINT PRIMARY KEY)", "c", "BIGINT", true)]
    [InlineData("CREATE TABLE t (c VARCHAR(10))", "c", "VARCHAR", false)]
    [InlineData("CREATE TABLE t (c NVARCHAR(5) NOT NULL)", "c", "NVARCHAR", false)]
    [InlineData("CREATE TABLE t (c DOUBLE TEXT)", "c", "DOUBLE", false)]
    [InlineData("CREATE TABLE t (c)", "c", "", false)]
    [InlineData("CREATE TABLE t (c BLOB CONSTRAINT nn NOT NULL)", "c", "BLOB", false)]
    [InlineData("CREATE TABLE t (c double precision)", "c", "DOUBLE", false)]
    [InlineData("CREATE TABLE t (c DECIMAL(10,5))", "c", "DECIMAL", false)]
    [InlineData("CREATE TABLE t (c STRING)", "c", "STRING", false)]
    public void ColumnType(string sql, string column, string typeName, bool isPrimaryKey)
    {
        Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));

        Assert.True(definition.TryGetColumn(column, out SqlTableColumn c));
        Assert.Equal(typeName, c.TypeName);
        Assert.Equal(isPrimaryKey, c.IsPartOfPrimaryKey);
        Assert.Equal(SqliteAffinityType(sql, column), c.DetectedType);
    }

    /// <summary>
    /// SQLite picks a column's affinity from substrings of its declared type, in this order: "INT" is INTEGER; "CHAR",
    /// "CLOB" or "TEXT" is TEXT; "BLOB", or no type, is BLOB; "REAL", "FLOA" or "DOUB" is REAL; anything else is NUMERIC
    /// (https://www.sqlite.org/datatype3.html#determination_of_column_affinity). DetectedType must agree with SQLite.
    /// </summary>
    [Theory]
    [InlineData("INT")]
    [InlineData("INTEGER")]
    [InlineData("TINYINT")]
    [InlineData("BIGINT")]
    [InlineData("INT8")]
    [InlineData("CHARINT")]
    [InlineData("FLOATING POINT")]
    [InlineData("DOUBLE INT")]
    [InlineData("POINT")]
    [InlineData("TEXT")]
    [InlineData("VARCHAR(255)")]
    [InlineData("NCHAR(10)")]
    [InlineData("CLOB")]
    [InlineData("DOUBLE TEXT")]
    [InlineData("BLOB")]
    [InlineData("FLOAT BLOB")]
    [InlineData("")]
    [InlineData("REAL")]
    [InlineData("FLOAT")]
    [InlineData("DOUBLE")]
    [InlineData("DOUBLE PRECISION")]
    [InlineData("FLOAT8")]
    [InlineData("REALVAL")]
    [InlineData("DOUBLEX")]
    [InlineData("LONG DOUBLE")]
    [InlineData("FLOAT(10)")]
    [InlineData("floatval")]
    [InlineData("NUMERIC")]
    [InlineData("DECIMAL(10,5)")]
    [InlineData("BOOLEAN")]
    [InlineData("DATE")]
    [InlineData("DATETIME")]
    [InlineData("GUID")]
    [InlineData("STRING")]
    [InlineData("STRINGY")]
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

    [Theory]
    [InlineData("Id")]
    [InlineData("ID")]
    public void TryGetColumnIsCaseInsensitive(string name)
    {
        Assert.True(SqlParser.TryParse("CREATE TABLE t (id INTEGER, v TEXT)", out SqlTableDefinition definition));

        Assert.True(definition.TryGetColumn(name, out SqlTableColumn c));
        Assert.Equal("id", c.Name);
    }
}
