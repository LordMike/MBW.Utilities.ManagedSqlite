using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

/// <summary>
/// The checks SQLite makes on the whole schema while it loads it: each row's CREATE statement against the rows before
/// it. Each case adds a row to sqlite_schema with writable_schema, which SQLite does not check, after a schema with a
/// table of each kind. A malformed schema makes every table unreadable, so the case reads table u, which is fine.
/// </summary>
public class SqlSchemaChecks_Tests
{
    private const string Schema =
        "CREATE TABLE u (b); INSERT INTO u VALUES (1); CREATE TABLE t (a PRIMARY KEY, c UNIQUE); CREATE INDEX i0 ON t (c); " +
        "CREATE VIEW v AS SELECT 1 AS a; CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END; PRAGMA writable_schema = ON; " +
        "INSERT INTO sqlite_schema VALUES ('table', 'vt', 'vt', 0, 'CREATE VIRTUAL TABLE vt USING nosuch (x)'); ";

    // A row's rootpage is u's page, which no index uses, or 0 for a view, a trigger or a virtual table
    private static byte[] Build(string type, string name, string tableName, string sql)
    {
        string rootPage = type is "index" || (type == "table" && !sql.Contains("VIRTUAL")) ? "(SELECT rootpage FROM sqlite_schema WHERE name = 'u')" : "0";
        return SqliteParity.Build(Schema + $"INSERT INTO sqlite_schema VALUES ('{type}', '{name}', '{tableName}', {rootPage}, '{sql.Replace("'", "''")}');");
    }

    private static void AssertRejected(string check, byte[] data)
    {
        SqliteParity.AssertSqliteReportsCorrupt(data, "u");

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
        Assert.Equal(check, Assert.Throws<SqliteCorruptException>(() => db.GetTable("u").GetTableDefinition()).Check);

        // Lenient checks each table by itself
        using Sqlite3Database lenient = new Sqlite3Database(new MemoryStream(data), new Sqlite3Settings { Validation = SqliteValidation.Lenient });
        Assert.Equal("b", lenient.GetTable("u").GetTableDefinition().Columns.Single().Name);
    }

    private static void AssertAccepted(byte[] data)
    {
        SqliteParity.AssertSqliteReads(data, "u");

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
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
    [InlineData("view", "v1", "v1", "CREATE VIEW v1 (x, y) AS SELECT nosuch FROM nosuch")]
    [InlineData("table", "vt1", "vt1", "CREATE VIRTUAL TABLE vt1 USING nosuch (x, (y, z), ''a'')")]
    public void AcceptedLikeSqlite(string type, string name, string tableName, string sql) =>
        AssertAccepted(Build(type, name, tableName, sql));

    /// <summary>
    /// The index rows of PRIMARY KEY and UNIQUE constraints have no SQL. Each must be an index a table before it made,
    /// with a root page none of its table's other indexes have.
    /// </summary>
    [Theory]
    [InlineData("DDL-02", "INSERT INTO sqlite_schema VALUES ('index', 'sqlite_autoindex_u_1', 'u', 3, NULL)")]
    [InlineData("DDL-02", "INSERT INTO sqlite_schema VALUES ('index', 'i9', 't', 3, '')")]
    [InlineData("SCHEMA-15", "UPDATE sqlite_schema SET rootpage = (SELECT rootpage FROM sqlite_schema WHERE name = 'sqlite_autoindex_t_2') WHERE name = 'sqlite_autoindex_t_1'")]
    [InlineData("DDL-72", "INSERT INTO sqlite_schema VALUES ('index', 'i1', 't', (SELECT rootpage FROM sqlite_schema WHERE name = 'i0'), 'CREATE INDEX i1 ON t (a)')")]
    public void IndexRowsLikeSqlite(string check, string statement) =>
        AssertRejected(check, SqliteParity.Build(Schema + statement + ";"));

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
