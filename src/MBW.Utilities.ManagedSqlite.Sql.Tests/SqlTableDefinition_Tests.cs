using System;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

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

    // SCHEMA-22: SQLite finds the whole schema malformed, so Strict fails every table's definition, every time it is
    // asked for. Lenient fails only the table whose SQL does not parse.
    [Fact]
    public void GetTableDefinitionOfMalformedSqlIsCorrupt()
    {
        byte[] data = SqliteParity.Build("CREATE TABLE t(a); CREATE TABLE u(b); PRAGMA writable_schema = ON; UPDATE sqlite_schema SET sql = 'CREATE TABLE t(a' WHERE name = 't';");
        SqliteParity.AssertSqliteReportsCorrupt(data, "u");

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
        Assert.Equal("SCHEMA-22", Assert.Throws<SqliteCorruptException>(() => db.GetTable("u").GetTableDefinition()).Check);
        Assert.Equal("SCHEMA-22", Assert.Throws<SqliteCorruptException>(() => db.GetTable("t").GetTableDefinition()).Check);

        using Sqlite3Database lenient = new Sqlite3Database(new MemoryStream(data), new Sqlite3Settings { Validation = SqliteValidation.Lenient });
        Assert.Equal("SCHEMA-22", Assert.Throws<SqliteCorruptException>(() => lenient.GetTable("t").GetTableDefinition()).Check);
        Assert.Equal("b", lenient.GetTable("u").GetTableDefinition().Columns.Single().Name);
    }
}
