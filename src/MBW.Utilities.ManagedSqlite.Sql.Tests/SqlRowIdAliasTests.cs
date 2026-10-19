using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

/// <summary>
/// SQLite only makes a column an alias for the rowid when its declared type is exactly "INTEGER" (any case),
/// it is the sole PRIMARY KEY column, and the table is a rowid table.
/// See https://www.sqlite.org/lang_createtable.html#rowid
/// </summary>
public class SqlRowIdAliasTests
{
    [Theory]
    [InlineData("CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)")]
    [InlineData("CREATE TABLE t (id integer primary key, v text)")]
    [InlineData("CREATE TABLE t (id InTeGeR PRIMARY KEY AUTOINCREMENT, v TEXT)")]
    [InlineData("CREATE TABLE t (id INTEGER PRIMARY KEY ASC, v TEXT)")]
    [InlineData("CREATE TABLE t (id INTEGER NOT NULL PRIMARY KEY, v TEXT)")]
    [InlineData("CREATE TABLE t (id INTEGER CONSTRAINT pk PRIMARY KEY, v TEXT)")]
    [InlineData("CREATE TABLE t (id INTEGER, v TEXT, PRIMARY KEY (id))")]
    [InlineData("CREATE TABLE t (id integer, v TEXT, primary key (id))")]
    [InlineData("CREATE TABLE t (id INTEGER, v TEXT, PRIMARY KEY (id DESC))")]
    public void RowIdAlias(string sql)
    {
        Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));

        Assert.True(definition.TryGetColumn("id", out SqlTableColumn id));
        Assert.True(id.IsPartOfPrimaryKey);
        Assert.Equal(typeof(long), id.DetectedType);
        Assert.Same(id, definition.RowIdColumn);
    }

    [Theory]
    [InlineData("CREATE TABLE t (id INT PRIMARY KEY, v TEXT)")]
    [InlineData("CREATE TABLE t (id BIGINT PRIMARY KEY, v TEXT)")]
    [InlineData("CREATE TABLE t (id int primary key, v text)")]
    [InlineData("CREATE TABLE t (id UNSIGNED INTEGER PRIMARY KEY, v TEXT)")]
    [InlineData("CREATE TABLE t (id INTEGER UNSIGNED PRIMARY KEY, v TEXT)")]
    [InlineData("CREATE TABLE t (id INTEGER(10) PRIMARY KEY, v TEXT)")]
    [InlineData("CREATE TABLE t (id INTEGER PRIMARY KEY DESC, v TEXT)")]
    [InlineData("CREATE TABLE t (id INT, v TEXT, PRIMARY KEY (id))")]
    [InlineData("CREATE TABLE t (id INTEGER, v TEXT, PRIMARY KEY (id, v))")]
    [InlineData("CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID")]
    [InlineData("CREATE TABLE t (id INTEGER, v TEXT, PRIMARY KEY (id)) without rowid")]
    public void NotRowIdAlias(string sql)
    {
        Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));

        Assert.True(definition.TryGetColumn("id", out SqlTableColumn id));
        Assert.True(id.IsPartOfPrimaryKey);
        Assert.Equal(typeof(long), id.DetectedType);
        Assert.Null(definition.RowIdColumn);
    }

    [Fact]
    public void LowercaseTypesAreDetected()
    {
        Assert.True(SqlParser.TryParse("create table t (a integer, b text, c real, d blob, e varchar(10))", out SqlTableDefinition definition));

        SqlTableColumn[] columns = definition.Columns.ToArray();
        Assert.Equal(typeof(long), columns[0].DetectedType);
        Assert.Equal(typeof(string), columns[1].DetectedType);
        Assert.Equal(typeof(double), columns[2].DetectedType);
        Assert.Equal(typeof(byte[]), columns[3].DetectedType);
        Assert.Equal(typeof(string), columns[4].DetectedType);
        Assert.Equal("VARCHAR", columns[4].TypeName);
    }

    [Theory]
    // INT PRIMARY KEY is not an alias, the stored value must be returned, not the rowid (1)
    [InlineData("IntPk", 100L)]
    [InlineData("BigIntPk", 200L)]
    // These alias the rowid, which SQLite stores as NULL in the record
    [InlineData("LowerIntegerPk", 42L)]
    [InlineData("TableLevelPk", 77L)]
    public void TryGetValueByNameFromDatabase(string tableName, long expectedId)
    {
        using Stream stream = typeof(SqlRowIdAliasTests).Assembly.GetManifestResourceStream("MBW.Utilities.ManagedSqlite.Sql.Tests.Data.RowIdAlias.db");
        using Sqlite3Database db = new Sqlite3Database(stream);

        Sqlite3Table table = db.GetTable(tableName);
        Sqlite3Row row = Assert.Single(table.EnumerateRows());

        Assert.True(row.TryGetValueByName("id", out object id));
        Assert.Equal(expectedId, id);

        Assert.True(row.TryGetValueByName("v", out object v));
        Assert.Equal("value", v);
    }
}
