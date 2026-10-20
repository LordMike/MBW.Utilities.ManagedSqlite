using System;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

public class SqlColumnTypes_Tests
{
    /// <summary>
    /// DetectedType follows SQLite's affinity rules, the first that matches a substring of the declared type:
    /// "INT" is INTEGER, "CHAR", "CLOB" or "TEXT" is TEXT, "BLOB" or no type is BLOB, "REAL", "FLOA" or "DOUB" is REAL,
    /// anything else is NUMERIC. https://www.sqlite.org/datatype3.html#determination_of_column_affinity
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE t (c integer)", "c", "INTEGER", typeof(long), false)]
    [InlineData("CREATE TABLE t (c BIGINT PRIMARY KEY)", "c", "BIGINT", typeof(long), true)]
    [InlineData("CREATE TABLE t (c CHARINT)", "c", "CHARINT", typeof(long), false)]
    [InlineData("CREATE TABLE t (c FLOATING POINT)", "c", "FLOATING POINT", typeof(long), false)]
    [InlineData("CREATE TABLE t (c POINT)", "c", "POINT", typeof(long), false)]
    [InlineData("CREATE TABLE t (c VARCHAR(10))", "c", "VARCHAR", typeof(string), false)]
    [InlineData("CREATE TABLE t (c TEXT)", "c", "TEXT", typeof(string), false)]
    [InlineData("CREATE TABLE t (c CLOB)", "c", "CLOB", typeof(string), false)]
    [InlineData("CREATE TABLE t (c NVARCHAR(5) NOT NULL)", "c", "NVARCHAR", typeof(string), false)]
    [InlineData("CREATE TABLE t (c DOUBLE TEXT)", "c", "DOUBLE", typeof(string), false)]
    [InlineData("CREATE TABLE t (c BLOB)", "c", "BLOB", typeof(byte[]), false)]
    [InlineData("CREATE TABLE t (c)", "c", "", typeof(byte[]), false)]
    // The constraint is not part of the declared type, so the "INT" in CONSTRAINT does not count
    [InlineData("CREATE TABLE t (c BLOB CONSTRAINT nn NOT NULL)", "c", "BLOB", typeof(byte[]), false)]
    [InlineData("CREATE TABLE t (c REAL)", "c", "REAL", typeof(double), false)]
    [InlineData("CREATE TABLE t (c FLOAT8)", "c", "FLOAT8", typeof(double), false)]
    [InlineData("CREATE TABLE t (c double precision)", "c", "DOUBLE", typeof(double), false)]
    // NUMERIC affinity types are all mapped to long, although SQLite may store a real, text or blob there
    [InlineData("CREATE TABLE t (c NUMERIC)", "c", "NUMERIC", typeof(long), false)]
    [InlineData("CREATE TABLE t (c DECIMAL(10,5))", "c", "DECIMAL", typeof(long), false)]
    [InlineData("CREATE TABLE t (c DATETIME)", "c", "DATETIME", typeof(long), false)]
    [InlineData("CREATE TABLE t (c GUID)", "c", "GUID", typeof(long), false)]
    [InlineData("CREATE TABLE t (c STRING)", "c", "STRING", typeof(long), false)]
    [InlineData("CREATE TABLE t (c STRINGY)", "c", "STRINGY", typeof(long), false)]
    public void ColumnType(string sql, string column, string typeName, Type detectedType, bool isPrimaryKey)
    {
        Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));

        Assert.True(definition.TryGetColumn(column, out SqlTableColumn c));
        Assert.Equal(typeName, c.TypeName);
        Assert.Equal(detectedType, c.DetectedType);
        Assert.Equal(isPrimaryKey, c.IsPartOfPrimaryKey);
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
