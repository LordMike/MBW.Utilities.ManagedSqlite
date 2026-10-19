using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

public class SqlParserSyntaxTests
{
    private static SqlTableDefinition Parse(string sql)
    {
        Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));
        Assert.NotNull(definition);
        return definition;
    }

    private static void AssertColumn(SqlTableDefinition definition, int ordinal, string name, string typeName, Type type)
    {
        SqlTableColumn column = definition.Columns.ElementAt(ordinal);
        Assert.Equal(ordinal, column.Ordinal);
        Assert.Equal(name, column.Name);
        Assert.Equal(typeName, column.TypeName);
        Assert.Equal(type, column.DetectedType);
    }

    [Fact]
    public void CheckAndDefaultConstraints()
    {
        SqlTableDefinition def = Parse("CREATE TABLE t(a INTEGER CHECK (a > 0), b TEXT DEFAULT 'x')");

        Assert.Equal("t", def.TableName);
        Assert.Equal(2, def.Columns.Count);
        AssertColumn(def, 0, "a", "INTEGER", typeof(long));
        AssertColumn(def, 1, "b", "TEXT", typeof(string));
    }

    [Fact]
    public void Comments()
    {
        SqlTableDefinition def = Parse("CREATE TABLE t( -- the id\n a INTEGER, /* note */ b TEXT)");

        Assert.Equal(2, def.Columns.Count);
        AssertColumn(def, 0, "a", "INTEGER", typeof(long));
        AssertColumn(def, 1, "b", "TEXT", typeof(string));
    }

    [Fact]
    public void CommentsContainingSyntax()
    {
        // Comments may contain anything, including parentheses, commas and quotes
        SqlTableDefinition def = Parse("CREATE TABLE t(\r\n  a INTEGER, -- don't (count, this)\r\n  /* b TEXT, ( ' \" */ c TEXT -- ) closing paren is in a comment\r\n) -- trailing comment without newline");

        Assert.Equal(2, def.Columns.Count);
        AssertColumn(def, 0, "a", "INTEGER", typeof(long));
        AssertColumn(def, 1, "c", "TEXT", typeof(string));
    }

    [Fact]
    public void GeneratedColumn()
    {
        SqlTableDefinition def = Parse("CREATE TABLE t(a INTEGER, b INTEGER GENERATED ALWAYS AS (a*2) VIRTUAL)");

        Assert.Equal(2, def.Columns.Count);
        AssertColumn(def, 0, "a", "INTEGER", typeof(long));
        AssertColumn(def, 1, "b", "INTEGER", typeof(long));
    }

    [Theory]
    [InlineData("a = 1")]
    [InlineData("a == 1")]
    [InlineData("a != 1")]
    [InlineData("a <> 1")]
    [InlineData("a < 1")]
    [InlineData("a <= 1")]
    [InlineData("a > 1")]
    [InlineData("a >= 1")]
    [InlineData("a * 2 / 3 % 4 - 5 + 6 > 0")]
    [InlineData("(a & 1) | (a << 2) | (a >> 3) | ~a")]
    [InlineData("a || 'x' = 'yx'")]
    [InlineData("a -> '$.x' IS NOT NULL AND a ->> '$.y' IS NULL")]
    [InlineData("a > -1.5e-3 AND a < .5 AND a <> 0x1F AND a <> 1E6 AND a <> 2.")]
    public void CheckExpressionOperators(string expression)
    {
        SqlTableDefinition def = Parse($"CREATE TABLE t(a INTEGER CHECK ({expression}), b TEXT, CHECK ({expression}))");

        Assert.Equal(2, def.Columns.Count);
        AssertColumn(def, 0, "a", "INTEGER", typeof(long));
        AssertColumn(def, 1, "b", "TEXT", typeof(string));
    }

    [Theory]
    [InlineData("CREATE TABLE t(\"col\"\"two\" TEXT)", "col\"two")]
    [InlineData("CREATE TABLE t('col''two' TEXT)", "col'two")]
    [InlineData("CREATE TABLE t(`col``two` TEXT)", "col`two")]
    [InlineData("CREATE TABLE t([col two] TEXT)", "col two")]
    [InlineData("CREATE TABLE t(\"\"\"\" TEXT)", "\"")]
    [InlineData("CREATE TABLE t(\"a'b`c\" TEXT)", "a'b`c")]
    public void EscapedQuotesInColumnNames(string sql, string expectedName)
    {
        SqlTableDefinition def = Parse(sql);

        Assert.Single(def.Columns);
        AssertColumn(def, 0, expectedName, "TEXT", typeof(string));
        Assert.True(def.TryGetColumn(expectedName, out _));
    }

    [Fact]
    public void EscapedQuotesInTableName()
    {
        SqlTableDefinition def = Parse("CREATE TABLE \"my\"\"table\"(a TEXT)");

        Assert.Equal("my\"table", def.TableName);
    }

    [Fact]
    public void QuotedPrimaryKeyColumnInTableConstraint()
    {
        SqlTableDefinition def = Parse("CREATE TABLE t(\"my\"\"id\" INTEGER, b TEXT, PRIMARY KEY (\"my\"\"id\"))");

        Assert.True(def.TryGetColumn("my\"id", out SqlTableColumn column));
        Assert.True(column.IsPartOfPrimaryKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CREATE TABLE t(")]
    [InlineData("CREATE TABLE t)")]
    [InlineData("CREATE TABLE t(a))")]
    [InlineData("a, b")]
    [InlineData("CREATE TABLE t(a), b")]
    [InlineData("CREATE TABLE t(a TEXT DEFAULT 'unterminated)")]
    [InlineData("CREATE TABLE t(\"unterminated TEXT)")]
    [InlineData("CREATE TABLE t([unterminated TEXT)")]
    [InlineData("CREATE TABLE t(a TEXT /* unterminated comment")]
    [InlineData("CREATE TABLE t(a TEXT, a INTEGER)")]
    [InlineData("CREATE TABLE t(a #)")]
    [InlineData("CREATE TABLE t(a \u0001)")]
    [InlineData("CREATE VIEW v AS SELECT * FROM t")]
    [InlineData("CREATE VIRTUAL TABLE v USING fts5(a, b)")]
    [InlineData("CREATE INDEX i ON t(a)")]
    public void UnparseableReturnsFalse(string sql)
    {
        Assert.False(SqlParser.TryParse(sql, out SqlTableDefinition definition));
        Assert.Null(definition);
    }

    [Theory]
    [InlineData("CREATE TABLE t()")]
    [InlineData("CREATE TABLE t(,)")]
    [InlineData("CREATE TABLE t(a,)")]
    [InlineData("CREATE TABLE t((a))")]
    [InlineData("CREATE TABLE t(PRIMARY KEY)")]
    [InlineData("CREATE TABLE t(PRIMARY KEY ())")]
    [InlineData("CREATE TABLE t(PRIMARY KEY ((a)))")]
    [InlineData("CREATE TABLE t(a ?, b :x, c @y, d $z)")]
    [InlineData("CREATE TABLE t(a TEXT ! b)")]
    public void MalformedDoesNotThrow(string sql)
    {
        // These are not valid SQLite, the parser may accept or reject them, but it must not throw
        Exception ex = Record.Exception(() => SqlParser.TryParse(sql, out _));
        Assert.Null(ex);
    }

    private static Sqlite3Database OpenSchemaFeaturesDb()
    {
        Assembly assembly = typeof(SqlParserSyntaxTests).Assembly;
        Stream stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.Data.SchemaFeatures.db");
        Assert.NotNull(stream);

        return new Sqlite3Database(stream);
    }

    [Fact]
    public void ReadTablesWithSchemaFeaturesFromDatabase()
    {
        using Sqlite3Database db = OpenSchemaFeaturesDb();

        Sqlite3Row checksRow = db.GetTable("checks").EnumerateRows().Single();
        Assert.True(checksRow.TryGetValueByName("id", out long id));
        Assert.True(checksRow.TryGetValueByName("a", out long a));
        Assert.True(checksRow.TryGetValueByName("b", out string b));
        Assert.Equal(1, id);
        Assert.Equal(5, a);
        Assert.Equal("hello", b);

        Sqlite3Row commentsRow = db.GetTable("comments").EnumerateRows().Single();
        Assert.True(commentsRow.TryGetValueByName("a", out a));
        Assert.True(commentsRow.TryGetValueByName("b", out b));
        Assert.Equal(7, a);
        Assert.Equal("seven", b);

        Sqlite3Row quotedRow = db.GetTable("quo\"ted").EnumerateRows().Single();
        Assert.True(quotedRow.TryGetValueByName("col\"two", out string colTwo));
        Assert.True(quotedRow.TryGetValueByName("sq'col", out long sqCol));
        Assert.True(quotedRow.TryGetValueByName("bt`col", out double btCol));
        Assert.Equal("two", colTwo);
        Assert.Equal(2, sqCol);
        Assert.Equal(2.5, btCol);

        // Generated columns parse; value ordinals of virtual columns are not handled here
        SqlTableDefinition generated = db.GetTable("generated").GetTableDefinition();
        Assert.Equal(new[] { "a", "b" }, generated.Columns.Select(s => s.Name));
    }

    [Fact]
    public void GetTableDefinitionOnUnparseableSchemaThrowsDescriptiveException()
    {
        using Sqlite3Database db = OpenSchemaFeaturesDb();

        Sqlite3SchemaRow view = db.GetTables().Single(s => s.Name == "checks_view");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => view.GetTableDefinition());
        Assert.Contains("checks_view", ex.Message);

        // A second call (cached failure) throws the same way
        ex = Assert.Throws<InvalidOperationException>(() => view.GetTableDefinition());
        Assert.Contains("checks_view", ex.Message);
    }
}
