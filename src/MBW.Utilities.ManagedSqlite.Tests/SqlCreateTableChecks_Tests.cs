using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Sql;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

/// <summary>
/// The checks SQLite makes when it parses a table's CREATE TABLE while loading the schema (parse.y and build.c at
/// init). Each case stores its SQL for table t with writable_schema, which SQLite does not check, and compares what
/// SQLite then does with the library.
/// </summary>
public class SqlCreateTableChecks_Tests
{
    // A WITHOUT ROWID table's SQL is stored over an index b-tree, so its rows read as SQLite's would
    private static byte[] Build(string sql) => SqliteParity.Build(
        $"CREATE TABLE t (a{(sql.Contains("WITHOUT ROWID") ? " PRIMARY KEY) WITHOUT ROWID" : ")")}; INSERT INTO t VALUES (1); CREATE TABLE u (b);",
        $"UPDATE sqlite_schema SET sql = '{sql.Replace("'", "''")}' WHERE name = 't'");

    // Strict fails the open
    private static SqliteCorruptException Open(byte[] data) =>
        Assert.Throws<SqliteCorruptException>(() => new Sqlite3Database(new MemoryStream(data)));

    // Lenient opens, reports the error, and fails the table
    private static SqliteCorruptException OpenLenient(byte[] data)
    {
        List<string> issues = new List<string>();
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data), new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add(i.Check) });
        SqliteCorruptException e = Assert.Throws<SqliteCorruptException>(() => db.GetTable("t"));
        Assert.Equal(new[] { e.Check }, issues);
        return e;
    }

    /// <summary>
    /// SQL SQLite finds malformed, which makes the whole schema corrupt. These decide how the table's rows read, so
    /// Lenient fails on them too.
    /// </summary>
    [Theory]
    // Syntax, and the tokenizer
    [InlineData("SCHEMA-22", "CREATE TABLE t (select)")]
    [InlineData("SCHEMA-22", "CREATE TABLE t ()")]
    [InlineData("SCHEMA-22", "CREATE TABLE t (a,)")]
    [InlineData("SCHEMA-22", "CREATE TABLE 1 (a)")]
    [InlineData("SCHEMA-22", "CREATE TABLE t (table, b)")]
    [InlineData("SCHEMA-22", "CREATE TABLE t (PRIMARY KEY (a), a)")]
    [InlineData("SCHEMA-22", "CREATE TABLE t (a) 1")]
    [InlineData("SCHEMA-22", "CREATE TABLE t (a INT PRIMARY KEY) STRICT WITHOUT ROWID")]
    [InlineData("SCHEMA-22", "CREATE TABLE t (a DEFAULT -(1)")]
    [InlineData("DDL-20", "CREATE TABLE t (a DEFAULT 1abc)")]
    [InlineData("DDL-20", "CREATE TABLE t (a DEFAULT 1__0)")]
    [InlineData("DDL-21", "CREATE TABLE t (a DEFAULT x'0')")]
    [InlineData("DDL-24", "CREATE TABLE t (a!)")]
    [InlineData("DDL-22", "CREATE TABLE t (a DEFAULT (#1))")]
    [InlineData("DDL-22", "CREATE TABLE t (a DEFAULT (?0))")]
    [InlineData("DDL-22", "CREATE TABLE t (a DEFAULT (?32767))")]
    // The table
    [InlineData("SCHEMA-28", "CREATE TABLE main.t (a)")]
    [InlineData("DDL-29", "CREATE TABLE t (a) WITHOUT \"rowid\"")]
    [InlineData("DDL-29", "CREATE TABLE t (a) STRICT, FAST")]
    [InlineData("DDL-37", "CREATE TABLE t (a, A)")]
    [InlineData("DDL-61", "CREATE TABLE t (a) WITHOUT ROWID")]
    // Keys
    [InlineData("DDL-48", "CREATE TABLE t (a PRIMARY KEY, PRIMARY KEY (a))")]
    [InlineData("DDL-49", "CREATE TABLE t (a, UNIQUE (b))")]
    [InlineData("DDL-49", "CREATE TABLE t (a, PRIMARY KEY (rowid))")]
    [InlineData("DDL-50", "CREATE TABLE t (a, UNIQUE (a + 1))")]
    [InlineData("DDL-50", "CREATE TABLE t (a, PRIMARY KEY (\"x\"))")]
    [InlineData("DDL-50", "CREATE TABLE t (a, UNIQUE (random()))")]
    [InlineData("DDL-54", "CREATE TABLE t (a, b AS (a) PRIMARY KEY)")]
    [InlineData("DDL-54", "CREATE TABLE t (a, b PRIMARY KEY AS (a))")]
    // Generated columns
    [InlineData("DDL-34", "CREATE TABLE t (a, b AS (a) PERSISTED)")]
    // Expressions
    [InlineData("SCHEMA-22", "CREATE TABLE t (a DEFAULT (count(DISTINCT a) OVER ()))")]
    [InlineData("SCHEMA-22", "CREATE TABLE t (a DEFAULT (sum(a) OVER (ROWS BETWEEN CURRENT ROW AND 1 PRECEDING)))")]
    [InlineData("SCHEMA-22", "CREATE TABLE t (a DEFAULT ((1, 2) IN ((1, 2), (3))))")]
    public void RejectedLikeSqlite(string check, string sql)
    {
        byte[] data = Build(sql);
        SqliteParity.AssertSqliteReportsCorrupt(data, "u");

        Assert.Equal(check, Open(data).Check);
        Assert.Equal(check, OpenLenient(data).Check);
    }

    /// <summary>
    /// SQL SQLite finds malformed, where the cause does not change how the table's rows read: a key's ON CONFLICT or
    /// NULLS order, AUTOINCREMENT, a foreign key, a DEFAULT that is never used, a STRICT type, a generated column's
    /// expression. Lenient reports it and reads on.
    /// </summary>
    [Theory]
    [InlineData("DDL-42", "CREATE TABLE t (a DEFAULT (b))")]
    [InlineData("DDL-42", "CREATE TABLE t (a DEFAULT ((SELECT 1)))")]
    [InlineData("DDL-43", "CREATE TABLE t (a, b AS (1) DEFAULT 2)")]
    [InlineData("DDL-43", "CREATE TABLE t (a, b DEFAULT 2 AS (1))")]
    [InlineData("DDL-47", "CREATE TABLE t (a REFERENCES p (x, y))")]
    [InlineData("DDL-47", "CREATE TABLE t (a, FOREIGN KEY (b) REFERENCES p)")]
    [InlineData("DDL-47", "CREATE TABLE t (a, FOREIGN KEY (a) REFERENCES p (x, y))")]
    [InlineData("DDL-51", "CREATE TABLE t (a, UNIQUE (a NULLS FIRST))")]
    [InlineData("DDL-53", "CREATE TABLE t (a TEXT PRIMARY KEY AUTOINCREMENT)")]
    [InlineData("DDL-55", "CREATE TABLE t (a UNIQUE ON CONFLICT IGNORE, UNIQUE (a) ON CONFLICT REPLACE)")]
    [InlineData("DDL-59", "CREATE TABLE t (a VARCHAR) STRICT")]
    [InlineData("DDL-59", "CREATE TABLE t (a) STRICT")]
    [InlineData("DDL-60", "CREATE TABLE t (a INTEGER PRIMARY KEY AUTOINCREMENT) WITHOUT ROWID")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS (c))")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS (t.a))")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS (random()))")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS ((SELECT 1)))")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS (count(a)))")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS (abs(a) OVER ()))")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS (nosuch(a) FILTER (WHERE a)))")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS (substr()))")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS (likelihood(a, 2.0)))")]
    [InlineData("DDL-62", "CREATE TABLE t (a, b AS ((a, 1) = (1, 2, 3)))")]
    [InlineData("DDL-62", "CREATE TABLE t (a AS (1) STORED)")]
    public void RelaxableLikeSqlite(string check, string sql)
    {
        byte[] data = Build(sql);
        SqliteParity.AssertSqliteReportsCorrupt(data, "u");

        Assert.Equal(check, Open(data).Check);

        List<string> issues = new List<string>();
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data), new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add(i.Check) });
        Assert.Equal("a", db.GetTable("t").GetTableDefinition().Columns.First().Name);
        Assert.Equal(new[] { check }, issues);
    }

    /// <summary>
    /// SQL SQLite would refuse in a CREATE TABLE statement, but accepts when it loads the schema
    /// </summary>
    [Theory]
    // Only the first statement is parsed (DDL-11), TEMP and IF NOT EXISTS are ignored (SCHEMA-29, SCHEMA-30)
    [InlineData("CREATE TABLE t (a); garbage ((")]
    [InlineData("CREATE TEMP TABLE t (a)")]
    [InlineData("CREATE TABLE IF NOT EXISTS t (a)")]
    // A parameter in a DEFAULT is NULL (DDL-22), an unknown collation is a placeholder (DDL-45), an unknown function
    // is allowed (DDL-62)
    [InlineData("CREATE TABLE t (a DEFAULT (?1 + :x))")]
    [InlineData("CREATE TABLE t (a COLLATE nosuch)")]
    [InlineData("CREATE TABLE t (a, b AS (nosuch(a)))")]
    // COLLATE and DESC in a referenced column list are ignored in a legacy schema
    [InlineData("CREATE TABLE t (a REFERENCES p (x COLLATE nocase DESC))")]
    // Keywords that may be names (%fallback ID, JOIN_KW)
    [InlineData("CREATE TABLE t (left, key, without)")]
    // A key term in parentheses is still a column
    [InlineData("CREATE TABLE t (a, PRIMARY KEY ((a)))")]
    public void AcceptedLikeSqlite(string sql) => SqliteParity.AssertSameAsSqlite(
        "CREATE TABLE t (a); INSERT INTO t VALUES (1);", $"UPDATE sqlite_schema SET sql = '{sql.Replace("'", "''")}' WHERE name = 't'");

    /// <summary>
    /// SQLite's limits: the parser stack (SQLITE_MAX_PARSER_DEPTH, 2500), the expression depth (SQLITE_MAX_EXPR_DEPTH,
    /// 1000) and the number of columns (SQLITE_MAX_COLUMN, 2000), just below and above each
    /// </summary>
    [Theory]
    [InlineData(null, "CREATE TABLE t (a DEFAULT (", "(", 1000, "1", ")", "))")]
    [InlineData("SCHEMA-22", "CREATE TABLE t (a DEFAULT (", "(", 3000, "1", ")", "))")]
    [InlineData(null, "CREATE TABLE t (a DEFAULT (", "- ", 999, "1", "", "))")]
    [InlineData("DDL-85", "CREATE TABLE t (a DEFAULT (", "- ", 1000, "1", "", "))")]
    [InlineData(null, "CREATE TABLE t (a", ", c{0}", 1999, "", "", ")")]
    [InlineData("DDL-36", "CREATE TABLE t (a", ", c{0}", 2000, "", "", ")")]
    public void Limits(string check, string prefix, string open, int count, string middle, string close, string suffix)
    {
        string sql = prefix + string.Concat(Enumerable.Range(0, count).Select(i => string.Format(open, i))) + middle +
                     string.Concat(Enumerable.Repeat(close, count)) + suffix;
        byte[] data = Build(sql);

        // The widest table has more columns than SQLite returns in a result, so only the definition is compared
        if (check == null)
        {
            SqliteParity.AssertSqliteReads(data, "u");
            using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
            Assert.Equal(sql.Count(c => c == ',') + 1, db.GetTable("t").GetTableDefinition().Columns.Count());
            return;
        }

        SqliteParity.AssertSqliteReportsCorrupt(data, "u");
        Assert.Equal(check, Open(data).Check);
    }
}
