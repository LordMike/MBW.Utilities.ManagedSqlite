using System;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

/// <summary>
/// The table definition's API, and what the parser does with SQL that SQLite would not store. Syntax SQLite does
/// store is compared with SQLite in <see cref="SqlSchemaParity_Tests"/>.
/// </summary>
public class SqlTableDefinition_Tests
{
    /// <summary>
    /// Names match as in SQLite, ignoring the case of ASCII letters only (sqlite3StrICmp in util.c)
    /// </summary>
    [Fact]
    public void TryGetColumnIgnoresAsciiCaseOnly()
    {
        Assert.True(SqlParser.TryParse("CREATE TABLE t (id INTEGER, é TEXT)", out SqlTableDefinition definition));

        Assert.True(definition.TryGetColumn("ID", out SqlTableColumn c));
        Assert.Equal("id", c.Name);
        Assert.False(definition.TryGetColumn("É", out _));
    }

    [Fact]
    public void VirtualGeneratedColumnHasNoValueByName()
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(SqliteParity.Build(
            "CREATE TABLE t(a TEXT, b AS (a || '!'), c TEXT); INSERT INTO t (a, c) VALUES ('x', 'third');")));

        Sqlite3Row row = db.GetTable("t").EnumerateRows().Single();
        Assert.False(row.TryGetValueByName("b", out object value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CREATE TABLE t(")]
    [InlineData("CREATE TABLE t)")]
    [InlineData("CREATE TABLE t(a), b")]
    [InlineData("CREATE TABLE t(a INTEGER,\u000Bb TEXT)")]
    [InlineData("CREATE TABLE t(a\u007Fb INTEGER)")]
    [InlineData("CREATE TABLE t(a TEXT DEFAULT 'unterminated)")]
    [InlineData("CREATE TABLE t(\"unterminated TEXT)")]
    [InlineData("CREATE TABLE t([unterminated TEXT)")]
    [InlineData("CREATE TABLE t(a TEXT /* unterminated comment")]
    [InlineData("CREATE TABLE t(a TEXT, a INTEGER)")]
    [InlineData("CREATE TABLE t(a, PRIMARY KEY ((a)))")]
    [InlineData("CREATE VIRTUAL TABLE v USING fts5(a, b)")]
    public void UnparseableReturnsFalse(string sql)
    {
        Assert.False(SqlParser.TryParse(sql, out SqlTableDefinition definition));
        Assert.Null(definition);
    }

    [Fact]
    public void GetTableDefinitionOnUnparseableSchemaThrows()
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(SqliteParity.Build("CREATE TABLE t(a); CREATE VIEW v AS SELECT a FROM t;")));

        Sqlite3SchemaRow view = db.GetTables().Single(s => s.Name == "v");

        Assert.Throws<InvalidOperationException>(() => view.GetTableDefinition());

        // A second call (cached failure) throws the same way
        Assert.Throws<InvalidOperationException>(() => view.GetTableDefinition());
    }
}
