using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Sql;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

/// <summary>
/// The checks SQLite makes on the whole schema while it loads it: each row's CREATE statement against the rows before
/// it. Each case adds a row to sqlite_schema with writable_schema, which SQLite does not check, after a schema with a
/// table of each kind. A malformed schema fails the open, and in Lenient the case reads table u, which is fine.
/// </summary>
public class SqlSchemaChecks_Tests
{
    private const string Schema =
        "CREATE TABLE u (b); INSERT INTO u VALUES (1); CREATE TABLE t (a PRIMARY KEY, c UNIQUE); CREATE INDEX i0 ON t (c); " +
        "CREATE TABLE w (p PRIMARY KEY, q UNIQUE) WITHOUT ROWID; CREATE VIEW v AS SELECT 1 AS a; CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END";

    // A virtual table of a module the reader cannot load, which every edit adds before its own rows
    private const string VirtualTable = "INSERT INTO sqlite_schema VALUES ('table', 'vt', 'vt', 0, 'CREATE VIRTUAL TABLE vt USING nosuch (x)'); ";

    /// <summary>
    /// The schema, then <paramref name="edit"/>, which must change <paramref name="rows"/> sqlite_schema rows
    /// </summary>
    private static byte[] Build(string edit, int rows) => SqliteParity.Build(Schema, VirtualTable + edit, rows + 1);

    // A row's rootpage is u's page, which no index uses, or 0 for a view, a trigger or a virtual table
    private static byte[] Build(string type, string name, string tableName, string sql)
    {
        string rootPage = type is "index" || (type == "table" && !sql.Contains("VIRTUAL")) ? "(SELECT rootpage FROM sqlite_schema WHERE name = 'u')" : "0";
        return Build($"INSERT INTO sqlite_schema VALUES ('{type}', '{name}', '{tableName}', {rootPage}, '{sql.Replace("'", "''")}')", 1);
    }

    private static void AssertRejected(string check, byte[] data)
    {
        SqliteParity.AssertSqliteReportsCorrupt(data, "u");

        Assert.Equal(check, Assert.Throws<SqliteCorruptException>(() => Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(data))).Check);

        // Lenient reports the error, keeps it on the row that failed, and loads on
        List<string> issues = new List<string>();
        using Sqlite3Database lenient = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(data), new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add(i.Check) });
        Assert.Equal("b", lenient.GetTable("u").GetTableDefinition().Columns.Single().Name);
        Assert.Equal(new[] { check }, issues);
    }

    private static void AssertAccepted(byte[] data)
    {
        SqliteParity.AssertSqliteReads(data, "u");

        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(data));
        Assert.Equal("b", db.GetTable("u").GetTableDefinition().Columns.Single().Name);
    }

    [Theory]
    // Names: no two tables, views or virtual tables, no two indexes and no two triggers, and no table named as an index
    [InlineData("DDL-08", "table", "T", "T", "CREATE TABLE T (x)")]
    [InlineData("DDL-08", "view", "v", "v", "CREATE VIEW v AS SELECT 2")]
    [InlineData("DDL-08", "table", "vt", "vt", "CREATE VIRTUAL TABLE vt USING nosuch")]
    [InlineData("DDL-08", "table", "i0", "i0", "CREATE TABLE i0 (x)")]
    [InlineData("DDL-08", "view", "sqlite_autoindex_t_1", "sqlite_autoindex_t_1", "CREATE VIEW sqlite_autoindex_t_1 AS SELECT 1")]
    [InlineData("DDL-10", "index", "i0", "t", "CREATE INDEX i0 ON t (a)")]
    [InlineData("DDL-10", "trigger", "tr", "t", "CREATE TRIGGER tr AFTER DELETE ON t BEGIN SELECT 1; END")]
    [InlineData("SCHEMA-28", "index", "i1", "t", "CREATE INDEX main.i1 ON t (a)")]
    [InlineData("SCHEMA-28", "view", "v1", "v1", "CREATE VIEW main.v1 AS SELECT 1")]
    [InlineData("SCHEMA-28", "trigger", "tr1", "t", "CREATE TRIGGER main.tr1 AFTER INSERT ON t BEGIN SELECT 1; END")]
    // The row's name and tbl_name are the statement's
    [InlineData("DDL-03", "index", "i2", "t", "CREATE INDEX i1 ON t (a)")]
    [InlineData("DDL-03", "index", "i1", "u", "CREATE INDEX i1 ON t (a)")]
    [InlineData("DDL-03", "view", "v2", "v2", "CREATE VIEW v1 AS SELECT 1")]
    [InlineData("DDL-03", "table", "vt2", "vt2", "CREATE VIRTUAL TABLE vt9 USING nosuch")]
    [InlineData("DDL-03", "trigger", "tr1", "u", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN SELECT 1; END")]
    // CREATE INDEX: an ordinary table loaded before it, and terms and a WHERE that resolve against it
    [InlineData("DDL-65", "index", "i1", "nosuch", "CREATE INDEX i1 ON nosuch (a)")]
    [InlineData("DDL-66", "index", "i1", "v", "CREATE INDEX i1 ON v (a)")]
    [InlineData("DDL-66", "index", "i1", "vt", "CREATE INDEX i1 ON vt (x)")]
    [InlineData("DDL-68", "index", "i1", "t", "CREATE INDEX i1 ON t (nosuch)")]
    [InlineData("DDL-68", "index", "i1", "t", "CREATE INDEX i1 ON t (rowid)")]
    [InlineData("DDL-68", "index", "i1", "t", "CREATE INDEX i1 ON t (t.a)")]
    [InlineData("DDL-68", "index", "i1", "t", "CREATE INDEX i1 ON t (random())")]
    [InlineData("DDL-68", "index", "i1", "t", "CREATE INDEX i1 ON t ((SELECT 1))")]
    [InlineData("DDL-69", "index", "i1", "t", "CREATE INDEX i1 ON t (a) WHERE nosuch")]
    [InlineData("DDL-69", "index", "i1", "t", "CREATE INDEX i1 ON t (a) WHERE u.b")]
    [InlineData("DDL-69", "index", "i1", "t", "CREATE INDEX i1 ON t (a) WHERE main.u.b")]
    [InlineData("DDL-69", "index", "i1", "t", "CREATE INDEX i1 ON t (a) WHERE a = ?")]
    [InlineData("DDL-69", "index", "i1", "t", "CREATE INDEX i1 ON t (a) WHERE count(a)")]
    [InlineData("DDL-69", "index", "i1", "w", "CREATE INDEX i1 ON w (p) WHERE rowid")]
    [InlineData("DDL-51", "index", "i1", "t", "CREATE INDEX i1 ON t (a NULLS FIRST)")]
    // Views
    [InlineData("DDL-74", "view", "v1", "v1", "CREATE VIEW v1 AS SELECT ?")]
    [InlineData("DDL-75", "view", "v1", "v1", "CREATE VIEW v1 AS SELECT * FROM temp.t")]
    [InlineData("DDL-75", "view", "v1", "v1", "CREATE VIEW v1 AS SELECT (SELECT 1 FROM main.t, aux.u)")]
    // Triggers: on a table or view loaded before it, of the right kind, with steps SQLite allows in a trigger
    [InlineData("DDL-79", "trigger", "tr1", "nosuch", "CREATE TRIGGER tr1 AFTER INSERT ON nosuch BEGIN SELECT 1; END")]
    [InlineData("DDL-80", "trigger", "tr1", "vt", "CREATE TRIGGER tr1 AFTER INSERT ON vt BEGIN SELECT 1; END")]
    [InlineData("DDL-80", "trigger", "tr1", "sqlite_master", "CREATE TRIGGER tr1 AFTER INSERT ON sqlite_master BEGIN SELECT 1; END")]
    [InlineData("DDL-81", "trigger", "tr1", "v", "CREATE TRIGGER tr1 INSERT ON v BEGIN SELECT 1; END")]
    [InlineData("DDL-81", "trigger", "tr1", "t", "CREATE TRIGGER tr1 INSTEAD OF INSERT ON t BEGIN SELECT 1; END")]
    [InlineData("DDL-82", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN UPDATE t INDEXED BY i0 SET a = 1; END")]
    [InlineData("DDL-82", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN DELETE FROM t NOT INDEXED; END")]
    [InlineData("DDL-82", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN INSERT INTO main.u VALUES (1); END")]
    [InlineData("DDL-82", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN INSERT INTO u VALUES (1) RETURNING *; END")]
    [InlineData("DDL-82", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t WHEN EXISTS (SELECT 1 FROM temp.u) BEGIN SELECT 1; END")]
    [InlineData("DDL-82", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN UPDATE u SET b = 1 FROM aux.t; END")]
    [InlineData("DDL-51", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN INSERT INTO u VALUES (1) ON CONFLICT (b NULLS FIRST) DO NOTHING; END")]
    [InlineData("DDL-51", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN INSERT INTO u VALUES (1) ON CONFLICT (b NULLS LAST) WHERE b DO UPDATE SET b = 2; END")]
    [InlineData("DDL-51", "trigger", "tr", "t", "CREATE TRIGGER IF NOT EXISTS tr AFTER INSERT ON t BEGIN INSERT INTO u VALUES (1) ON CONFLICT (b NULLS FIRST) DO NOTHING; END")]
    [InlineData("DDL-82", "trigger", "tr", "t", "CREATE TRIGGER IF NOT EXISTS tr AFTER INSERT ON t BEGIN DELETE FROM t NOT INDEXED; END")]
    [InlineData("DDL-82", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN UPDATE u SET (b, b) = (1, 2, 3); END")]
    [InlineData("DDL-82", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN UPDATE u SET b = 1, (b, b) = 1; END")]
    [InlineData("DDL-82", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN INSERT INTO u VALUES (1) ON CONFLICT DO UPDATE SET (b, b) = (1); END")]
    [InlineData("SCHEMA-22", "trigger", "tr1", "t", "CREATE TRIGGER tr1 AFTER INSERT ON t BEGIN SELECT; END")]
    public void RejectedLikeSqlite(string check, string type, string name, string tableName, string sql) =>
        AssertRejected(check, Build(type, name, tableName, sql));

    [Theory]
    // IF NOT EXISTS makes nothing when the name exists
    [InlineData("table", "t", "t", "CREATE TABLE IF NOT EXISTS t (x)")]
    [InlineData("view", "v", "v", "CREATE VIEW IF NOT EXISTS v AS SELECT 2")]
    [InlineData("index", "i0", "t", "CREATE INDEX IF NOT EXISTS i0 ON t (a)")]
    [InlineData("trigger", "tr", "t", "CREATE TRIGGER IF NOT EXISTS tr AFTER DELETE ON t BEGIN SELECT 1; END")]
    // An index may be named as a table, and its names compare ignoring ASCII case. A partial index WHERE may name its
    // table and the rowid, and any database, which SQLite ignores.
    [InlineData("index", "u", "t", "CREATE INDEX u ON t (a)")]
    [InlineData("index", "I1", "T", "CREATE INDEX i1 ON t (A)")]
    [InlineData("index", "i1", "t", "CREATE UNIQUE INDEX i1 ON t (a + c COLLATE nocase DESC, lower(c), 'a')")]
    [InlineData("index", "i1", "t", "CREATE INDEX i1 ON t (a) WHERE t.a > 0 AND nosuch.t.c IS NOT NULL AND rowid > 0 AND _rowid_ AND oid")]
    // The table of a trigger may name the main database; its steps and a view's SELECT are not resolved
    [InlineData("trigger", "tr1", "t", "CREATE TRIGGER tr1 BEFORE UPDATE OF nosuch ON main.t WHEN new.nosuch BEGIN UPDATE nosuch SET x = 1; END")]
    [InlineData("trigger", "tr1", "v", "CREATE TRIGGER tr1 INSTEAD OF INSERT ON v BEGIN INSERT INTO u SELECT * FROM main.t; DELETE FROM u WHERE b = new.a; END")]
    [InlineData("trigger", "tr1", "u", "CREATE TRIGGER tr1 AFTER INSERT ON u BEGIN INSERT INTO t VALUES (1, 2) ON CONFLICT DO NOTHING; END")]
    [InlineData("trigger", "tr1", "u", "CREATE TRIGGER tr1 AFTER INSERT ON nosuch.u BEGIN INSERT INTO u VALUES (1) ON CONFLICT (b) DO NOTHING ON CONFLICT (b NULLS FIRST) DO NOTHING; UPDATE u SET (b, b) = (SELECT 1, 2, 3); END")]
    [InlineData("index", "i1", "sqlite_master", "CREATE INDEX i1 ON sqlite_schema (name) WHERE rowid > 0")]
    [InlineData("view", "v1", "v1", "CREATE VIEW v1 (x, y) AS SELECT nosuch FROM nosuch")]
    [InlineData("table", "vt1", "vt1", "CREATE VIRTUAL TABLE vt1 USING nosuch (x, (y, z), ''a'')")]
    public void AcceptedLikeSqlite(string type, string name, string tableName, string sql) =>
        AssertAccepted(Build(type, name, tableName, sql));

    /// <summary>
    /// The index rows of PRIMARY KEY and UNIQUE constraints have no SQL. Each must be an index a table before it made,
    /// with a root page none of its table's other indexes have.
    /// </summary>
    [Theory]
    [InlineData("DDL-02", 1, "INSERT INTO sqlite_schema VALUES ('index', 'sqlite_autoindex_u_1', 'u', 3, NULL)")]
    [InlineData("DDL-02", 1, "INSERT INTO sqlite_schema VALUES ('index', 'i9', 't', 3, '')")]
    [InlineData("SCHEMA-15", 1, "UPDATE sqlite_schema SET rootpage = (SELECT rootpage FROM sqlite_schema WHERE name = 'sqlite_autoindex_t_2') WHERE name = 'sqlite_autoindex_t_1'")]
    [InlineData("SCHEMA-15", 1, "UPDATE sqlite_schema SET rootpage = (SELECT rootpage FROM sqlite_schema WHERE name = 'w') WHERE name = 'sqlite_autoindex_w_2'")]
    [InlineData("DDL-72", 2, "INSERT INTO sqlite_schema VALUES ('index', 'i1', 't', 0, 'CREATE INDEX i1 ON t (a)'); INSERT INTO sqlite_schema VALUES ('index', 'i2', 't', 0, 'CREATE INDEX i2 ON t (a)')")]
    [InlineData("DDL-72", 1, "INSERT INTO sqlite_schema VALUES ('index', 'i1', 't', (SELECT rootpage FROM sqlite_schema WHERE name = 'i0'), 'CREATE INDEX i1 ON t (a)')")]
    public void IndexRowsLikeSqlite(string check, int rows, string edit) => AssertRejected(check, Build(edit, rows));

    // A WITHOUT ROWID table's PRIMARY KEY index is the table's b-tree; a CREATE INDEX with rootpage 0 before the
    // automatic index rows is no duplicate, as their root pages are not set yet
    [Theory]
    [InlineData(1, "INSERT INTO sqlite_schema VALUES ('index', 'sqlite_autoindex_w_1', 'w', (SELECT rootpage FROM sqlite_schema WHERE name = 'w'), NULL)")]
    [InlineData(3, "UPDATE sqlite_schema SET rowid = rowid + 100 WHERE name LIKE 'sqlite_autoindex_t_%'; INSERT INTO sqlite_schema VALUES ('index', 'i1', 't', 0, 'CREATE INDEX i1 ON t (a)')")]
    public void IndexRowsAcceptedLikeSqlite(int rows, string edit) => AssertAccepted(Build(edit, rows));

    /// <summary>
    /// A CREATE INDEX before a table can take the name of the table's automatic index, as sqlite_ names are only
    /// reserved outside the schema load. SQLite then fails the load as out of memory, and this reader as corrupt.
    /// </summary>
    [Fact]
    public void IndexTakingAnAutomaticIndexName()
    {
        byte[] data = Build("INSERT INTO sqlite_schema VALUES ('index', 'sqlite_autoindex_x_1', 'u', 0, 'CREATE INDEX sqlite_autoindex_x_1 ON u (b)'); " +
                            "INSERT INTO sqlite_schema VALUES ('table', 'x', 'x', (SELECT rootpage FROM sqlite_schema WHERE name = 'u'), 'CREATE TABLE x (q UNIQUE)')", 2);
        SqliteParity.AssertSqliteFails(data, "u");

        Assert.Equal("DDL-10", Assert.Throws<SqliteCorruptException>(() => Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(data))).Check);
    }

    /// <summary>
    /// A virtual table's module arguments count toward the column limit (SQLITE_MAX_COLUMN, 2000), with the module, the
    /// database and the table names before them
    /// </summary>
    [Theory]
    [InlineData(null, 1994)]
    [InlineData("DDL-36", 1995)]
    public void VirtualTableArguments(string check, int count)
    {
        byte[] data = Build("table", "vt1", "vt1", $"CREATE VIRTUAL TABLE vt1 USING nosuch ({string.Join(", ", Enumerable.Range(0, count).Select(i => $"c{i}"))})");
        if (check == null)
            AssertAccepted(data);
        else
            AssertRejected(check, data);
    }
}
