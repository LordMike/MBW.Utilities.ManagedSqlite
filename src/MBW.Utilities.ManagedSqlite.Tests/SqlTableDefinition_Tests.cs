using System;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Sql;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

/// <summary>
/// The table definition's API, and what the parser does with SQL that SQLite would not store. Syntax SQLite does
/// store is compared with SQLite in <see cref="SqlCreateTable_Tests"/>.
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
    [InlineData("CREATE TABLE t(a INTEGER,\uFEFF\u000Bb TEXT)")]
    [InlineData("CREATE TABLE t(a\u007Fb INTEGER)")]
    [InlineData("CREATE TABLE t(a TEXT DEFAULT 'unterminated)")]
    [InlineData("CREATE TABLE t(\"unterminated TEXT)")]
    [InlineData("CREATE TABLE t([unterminated TEXT)")]
    [InlineData("CREATE TABLE t(a TEXT /* unterminated comment")]
    [InlineData("CREATE VIRTUAL TABLE v USING fts5(a, b)")]
    [InlineData("ALTER TABLE t ADD COLUMN x INT DEFAULT 1")]
    public void UnparseableReturnsFalse(string sql)
    {
        Assert.False(SqlParser.TryParse(sql, out SqlTableDefinition definition));
        Assert.Null(definition);
    }

    // A view or a virtual table has no table definition, which is the caller's error, not damage
    [Fact]
    public void GetTableDefinitionOfNonTableThrows()
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(SqliteParity.Build(
            "CREATE TABLE t(a); CREATE VIEW v AS SELECT a FROM t; CREATE VIRTUAL TABLE f USING fts5(a);")));

        Assert.Throws<InvalidOperationException>(() => db.GetTables().Single(s => s.Name == "v").GetTableDefinition());
        Assert.Throws<NotSupportedException>(() => db.GetTables().Single(s => s.Name == "f").GetTableDefinition());
    }

    // A CREATE TABLE IF NOT EXISTS of a name a row before it took makes no table, in SQLite too, so the name reads the
    // first table
    [Fact]
    public void GetTableDefinitionOfIfNotExistsDuplicateThrows()
    {
        byte[] data = SqliteParity.Build("CREATE TABLE t(a);", "INSERT INTO sqlite_schema VALUES ('table', 't', 't', 0, 'CREATE TABLE IF NOT EXISTS t (b)')", 1);
        SqliteParity.AssertSqliteReads(data, "t");

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));

        Assert.Throws<InvalidOperationException>(() => db.GetTables().Last().GetTableDefinition());
        Assert.Equal("a", db.GetTable("t").GetTableDefinition().Columns.Single().Name);
    }
}
