using System;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

public class SqlColumnTypes_Tests
{
    [Theory]
    [InlineData("CREATE TABLE t (c INT)", "c", "INT", typeof(long), false)]
    [InlineData("CREATE TABLE t (c INTEGER)", "c", "INTEGER", typeof(long), false)]
    [InlineData("CREATE TABLE t (c BIGINT PRIMARY KEY)", "c", "BIGINT", typeof(long), true)]
    [InlineData("CREATE TABLE t (c integer)", "c", "INTEGER", typeof(long), false)]
    [InlineData("CREATE TABLE t (c VARCHAR(10))", "c", "VARCHAR", typeof(string), false)]
    [InlineData("CREATE TABLE t (c TEXT)", "c", "TEXT", typeof(string), false)]
    [InlineData("CREATE TABLE t (c CLOB)", "c", "CLOB", typeof(string), false)]
    [InlineData("CREATE TABLE t (c varchar(10))", "c", "VARCHAR", typeof(string), false)]
    [InlineData("CREATE TABLE t (c BLOB)", "c", "BLOB", typeof(byte[]), false)]
    [InlineData("CREATE TABLE t (c blob)", "c", "BLOB", typeof(byte[]), false)]
    [InlineData("CREATE TABLE t (c)", "c", "", typeof(byte[]), false)]
    [InlineData("CREATE TABLE t (c REAL)", "c", "REAL", typeof(double), false)]
    [InlineData("CREATE TABLE t (c FLOAT)", "c", "FLOAT", typeof(double), false)]
    [InlineData("CREATE TABLE t (c DOUBLE PRECISION)", "c", "DOUBLE", typeof(double), false)]
    [InlineData("CREATE TABLE t (c double precision)", "c", "DOUBLE", typeof(double), false)]
    // NUMERIC affinity types are all mapped to long, although SQLite may store a real, text or blob there
    [InlineData("CREATE TABLE t (c DECIMAL(10,5))", "c", "DECIMAL", typeof(long), false)]
    [InlineData("CREATE TABLE t (c BOOLEAN)", "c", "BOOLEAN", typeof(long), false)]
    [InlineData("CREATE TABLE t (c NUMERIC)", "c", "NUMERIC", typeof(long), false)]
    // Unrecognized names are byte[] and keep their name. SQLite: NUMERIC affinity, or INTEGER for a name containing "INT".
    // The library matches whole words only, SQLite matches substrings (CHARINT, FLOATING POINT, STRINGY would be INTEGER, REAL, NUMERIC)
    [InlineData("CREATE TABLE t (c STRING)", "c", "STRING", typeof(byte[]), false)]
    [InlineData("CREATE TABLE t (c POINT)", "c", "POINT", typeof(byte[]), false)]
    [InlineData("CREATE TABLE t (c NVARCHAR(5) NOT NULL)", "c", "NVARCHAR", typeof(string), false)]
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
