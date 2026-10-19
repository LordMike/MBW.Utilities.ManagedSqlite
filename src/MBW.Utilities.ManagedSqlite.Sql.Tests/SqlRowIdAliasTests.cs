using System.Collections.Generic;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

/// <summary>
/// SQLite only makes a column an alias for the rowid when its declared type is exactly "INTEGER" (any case),
/// it is the sole PRIMARY KEY column, and the table is a rowid table.
/// See https://www.sqlite.org/lang_createtable.html#rowid
/// </summary>
public class SqlRowIdAliasTests
{
    private static readonly string[] Alias =
    {
        "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)",
        "CREATE TABLE t (id integer primary key, v text)",
        "CREATE TABLE t (id InTeGeR PRIMARY KEY AUTOINCREMENT, v TEXT)",
        "CREATE TABLE t (id INTEGER PRIMARY KEY ASC, v TEXT)",
        "CREATE TABLE t (id INTEGER NOT NULL PRIMARY KEY, v TEXT)",
        "CREATE TABLE t (id INTEGER CONSTRAINT pk PRIMARY KEY, v TEXT)",
        "CREATE TABLE t (id INTEGER, v TEXT, PRIMARY KEY (id))",
        "CREATE TABLE t (id integer, v TEXT, primary key (id))",
        "CREATE TABLE t (id INTEGER, v TEXT, PRIMARY KEY (id DESC))"
    };

    private static readonly string[] NotAlias =
    {
        "CREATE TABLE t (id INT PRIMARY KEY, v TEXT)",
        "CREATE TABLE t (id BIGINT PRIMARY KEY, v TEXT)",
        "CREATE TABLE t (id int primary key, v text)",
        "CREATE TABLE t (id UNSIGNED INTEGER PRIMARY KEY, v TEXT)",
        "CREATE TABLE t (id INTEGER UNSIGNED PRIMARY KEY, v TEXT)",
        "CREATE TABLE t (id INTEGER(10) PRIMARY KEY, v TEXT)",
        "CREATE TABLE t (id INTEGER PRIMARY KEY DESC, v TEXT)",
        "CREATE TABLE t (id INT, v TEXT, PRIMARY KEY (id))",
        "CREATE TABLE t (id INTEGER, v TEXT, PRIMARY KEY (id, v))",
        "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID",
        "CREATE TABLE t (id INTEGER, v TEXT, PRIMARY KEY (id)) without rowid"
    };

    public static IEnumerable<object[]> AliasSql() => Alias.Select(s => new object[] { s });
    public static IEnumerable<object[]> NotAliasSql() => NotAlias.Select(s => new object[] { s });

    // WITHOUT ROWID tables are not supported by the Core reader
    public static IEnumerable<object[]> ParitySql() =>
        Alias.Concat(NotAlias).Where(s => !s.Contains("without rowid", System.StringComparison.OrdinalIgnoreCase)).Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(AliasSql))]
    public void RowIdAlias(string sql)
    {
        Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));

        Assert.True(definition.TryGetColumn("id", out SqlTableColumn id));
        Assert.True(id.IsPartOfPrimaryKey);
        Assert.Equal(typeof(long), id.DetectedType);
        Assert.Same(id, definition.RowIdColumn);
    }

    [Theory]
    [MemberData(nameof(NotAliasSql))]
    public void NotRowIdAlias(string sql)
    {
        Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));

        Assert.True(definition.TryGetColumn("id", out SqlTableColumn id));
        Assert.True(id.IsPartOfPrimaryKey);
        Assert.Equal(typeof(long), id.DetectedType);
        Assert.Null(definition.RowIdColumn);
    }

    /// <summary>
    /// An alias reads the rowid (42, -7 as written), a non-alias reads the stored value, with rowids 1 and 2 either way.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParitySql))]
    public void ReadsSameAsSqlite(string sql)
    {
        SqliteParity.AssertSameAsSqlite(sql + "; INSERT INTO t (id, v) VALUES (42, 'x'), (-7, 'y');");
    }
}
