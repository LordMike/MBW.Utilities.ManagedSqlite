using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using MBW.Utilities.ManagedSqlite.Sql;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

/// <summary>
/// Named, structure-aware corruption cases. Each one patches a few bytes of a database SQLite builds from
/// <see cref="Script"/> and checks the contract: reading terminates, and malformed input surfaces as
/// <see cref="SqliteCorruptException"/>. Every case runs with a timeout, so a regression (such as a page cycle) fails
/// the test instead of hanging the run.
/// </summary>
public class Corruption_Tests
{
    /// <summary>
    /// 512 byte pages and 300 byte rows give one row per leaf page, so Deep is a 3-level b-tree (root interior ->
    /// interior -> leaf). Small fits on one leaf page, and its row 2 overflows into a chain of overflow pages. Keyed is
    /// a WITHOUT ROWID table, an index b-tree whose root is an interior page over several leaves.
    /// </summary>
    private const string Script = @"
PRAGMA page_size = 512;
CREATE TABLE Deep (Id INTEGER PRIMARY KEY, Payload TEXT);
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 120)
INSERT INTO Deep SELECT i, printf('%300d', i) FROM n;
CREATE TABLE Small (Id INTEGER PRIMARY KEY, Name TEXT, Data BLOB);
INSERT INTO Small VALUES (1, 'one', NULL), (2, 'two', zeroblob(1500)), (3, 'three', x'0102');
CREATE TABLE Keyed (Name TEXT PRIMARY KEY, Payload TEXT) WITHOUT ROWID;
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40)
INSERT INTO Keyed SELECT printf('key%03d', i), printf('%50d', i) FROM n;";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly byte[] Base = SqliteParity.Build(Script);

    private const uint MasterRoot = 1;
    private static readonly uint DeepRoot = RootPage("Deep");
    private static readonly uint DeepLeft = new DbPatcher(Base).GetChildPointer(DeepRoot, 0);
    private static readonly uint DeepFirstLeaf = new DbPatcher(Base).GetChildPointer(DeepLeft, 0);
    private static readonly uint SmallRoot = RootPage("Small");
    private static readonly uint KeyedRoot = RootPage("Keyed");
    private static readonly uint KeyedFirstLeaf = new DbPatcher(Base).GetChildPointer(KeyedRoot, 0);
    private static readonly uint PastTheEnd = (uint)new DbPatcher(Base).PageCount + 1;

    private static uint RootPage(string table)
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(Base));
        return db.GetTable(table).SchemaDefinition.RootPage;
    }

    /// <summary>
    /// The first schema row is Deep's. Its rootpage column is a 1-byte integer that follows "table", "Deep", "Deep".
    /// </summary>
    private static void SetDeepRootPageInSchema(DbPatcher patcher, byte value)
    {
        int record = patcher.GetRecordOffset(MasterRoot, 0, out _);
        Assert.Equal(1, patcher.Data[record + 4]);

        int rootPageValue = record + patcher.Data[record] + 5 + 4 + 4;
        patcher.WriteByte(rootPageValue, (byte)DeepRoot, value);
    }

    /// <summary>
    /// Opens the database, lists its tables and reads every row of every table
    /// </summary>
    private static void ReadEverything(byte[] data)
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));

        foreach (Sqlite3SchemaRow schema in db.GetTables().Where(s => s.Type == "table").ToList())
            ReadTable(db, schema.Name);
    }

    private static void ReadTable(Sqlite3Database db, string table)
    {
        foreach (Sqlite3Row row in db.GetTable(table).EnumerateRows())
            Assert.NotNull(row.ColumnData);
    }

    /// <summary>
    /// Runs the action with a timeout, and returns the exception it threw, if any
    /// </summary>
    private static Exception RunBounded(Action action)
    {
        Task task = Task.Run(action);

        try
        {
            if (!task.Wait(Timeout))
                Assert.Fail($"Did not finish within {Timeout}, the reader is likely stuck in a loop");
        }
        catch (AggregateException e)
        {
            return e.InnerException;
        }

        return null;
    }

    /// <summary>
    /// Asserts that the action terminates, and that if it fails, it fails with a <see cref="SqliteException"/>
    /// </summary>
    private static void AssertTerminatesGracefully(Action action)
    {
        Exception ex = RunBounded(action);

        if (ex != null)
            Assert.IsAssignableFrom<SqliteException>(ex);
    }

    /// <summary>
    /// Damages a copy of the database with <paramref name="patch"/>, reads everything, and asserts it fails with a
    /// <see cref="SqliteCorruptException"/> for <paramref name="structure"/>. Checking the structure makes a case fail
    /// if a later, unrelated check is what catches it. <paramref name="check"/>, when given, is the check's ID.
    /// </summary>
    private static void AssertCorrupt(string structure, Action<DbPatcher> patch, string check = null)
    {
        DbPatcher patcher = new DbPatcher(Base);
        patch(patcher);

        Exception ex = RunBounded(() => ReadEverything(patcher.Data));

        SqliteCorruptException corrupt = Assert.IsType<SqliteCorruptException>(ex);
        Assert.Equal(structure, corrupt.Structure);
        if (check != null)
            Assert.Equal(check, corrupt.Check);
    }

    // Database header

    [Fact]
    public void Header_BadMagic() => AssertCorrupt("magic bytes", p => p.WriteByte(0, (byte)'S', (byte)'X'));

    // A page size is a power of two from 512 to 65536. The stored value 1 means 65536, read by Structure_Tests.
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void Header_InvalidPageSize(ushort pageSize) => AssertCorrupt("database header", p => p.SetPageSizeField(pageSize));

    [Fact]
    public void Header_UsableSizeBelow480() => AssertCorrupt("database header", p => p.SetReservedSpace(255));

    /// <summary>
    /// Header checks SQLite makes although the data is intact: a read version above 2 (a format newer than it knows),
    /// payload fractions other than 64, 32, 32, a valid in-header database size larger than the file, and a schema format
    /// above 4. Strict fails like SQLite, with the check's ID; Lenient reports the check once and reads every table as
    /// SQLite reads the undamaged database.
    /// </summary>
    [Theory]
    [InlineData("HDR-05")]
    [InlineData("HDR-08")]
    [InlineData("HDR-14")]
    [InlineData("HDR-24")]
    public void Header_Relaxable(string check)
    {
        DbPatcher patcher = new DbPatcher(Base);
        byte[] data = (check switch
        {
            "HDR-05" => patcher.SetReadVersion(3),
            "HDR-08" => patcher.SetMaxEmbeddedPayloadFraction(65),
            "HDR-14" => patcher.SetDatabaseSizeInPages((uint)patcher.PageCount + 1),
            _ => patcher.WriteByte(47, 4, 5)
        }).Data;

        SqliteParity.AssertSqliteFails(data, "Small");
        Assert.Equal(check, Assert.Throws<SqliteCorruptException>(() => new Sqlite3Database(new MemoryStream(data))).Check);

        List<string> issues = new List<string>();
        Sqlite3Settings lenient = new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add(i.Check) };

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data), lenient);
        SqliteParity.AssertTablesSameAsSqlite(Script, db, "Deep", "Small", "Keyed");
        Assert.Equal(new[] { check }, issues);
    }

    /// <summary>
    /// Strict checks hold in Lenient too: the open fails with the check's ID, and nothing is reported.
    /// </summary>
    [Theory]
    [InlineData("HDR-03")]
    [InlineData("HDR-09")]
    public void Header_StrictInLenient(string check)
    {
        DbPatcher patcher = new DbPatcher(Base);
        byte[] data = (check == "HDR-03" ? patcher.WriteByte(0, (byte)'S', (byte)'X') : patcher.SetPageSizeField(1000)).Data;

        List<string> issues = new List<string>();
        Sqlite3Settings lenient = new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add(i.Check) };

        Assert.Equal(check, Assert.Throws<SqliteCorruptException>(() => new Sqlite3Database(new MemoryStream(data), lenient)).Check);
        Assert.Empty(issues);
    }

    // HDR-24: SQLite reads only the schema format's low byte, so 0x104 is format 4
    [Fact]
    public void Header_SchemaFormatLowByteOnly() => SqliteParity.AssertSameAsSqlite(Script, data =>
    {
        Assert.Equal(new byte[] { 0, 0, 0, 4 }, data.AsSpan(44, 4).ToArray());
        data[46] = 1;
        return data;
    });

    [Fact]
    public void Header_PagesAfterDatabaseSize_AreNotRead() => AssertCorrupt("page number", p => p.AppendPages(1).SetOverflowPointer(SmallRoot, 1, PastTheEnd));

    // When the version-valid-for number differs from the change counter, the in-header size is stale and the file length decides
    [Fact]
    public void Header_DatabaseSizeIgnoredWhenNotValidFor()
    {
        DbPatcher patcher = new DbPatcher(Base);
        byte[] data = patcher.SetVersionValidFor(patcher.ReadUInt32(24) + 1).SetDatabaseSizeInPages(PastTheEnd).Data;

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
        SqliteParity.AssertTablesSameAsSqlite(Script, db, "Deep", "Small");
    }

    // File length

    // HDR-01: SQLite opens a 0-byte file as an empty database
    [Fact]
    public void Truncated_EmptyFile() => SqliteParity.AssertSameAsSqlite("", _ => new byte[0]);

    // A stream positioned at its end is not an empty file
    [Fact]
    public void Truncated_StreamAtItsEnd()
    {
        MemoryStream stream = new MemoryStream(Base);
        stream.Seek(0, SeekOrigin.End);
        Assert.Equal("file length", Assert.Throws<SqliteCorruptException>(() => new Sqlite3Database(stream)).Structure);
    }

    [Fact]
    public void Truncated_InsideTheHeader() => AssertCorrupt("file length", p => p.Truncate(50));

    [Fact]
    public void Truncated_AfterTheSchemaPage() => AssertCorrupt("file length", p => p.Truncate(p.PageSize));

    [Fact]
    public void Truncated_LastPageMissing() => AssertCorrupt("file length", p => p.Truncate((p.PageCount - 1) * p.PageSize));

    // HDR-17: a partial last page still counts as a page, so the file opens and a table that does not touch that page
    // reads as before. The last page is a leaf of Keyed: Strict fails reading it, Lenient reads the missing bytes as
    // zeros, as SQLite does, and reports it once.
    [Fact]
    public void Truncated_InsideTheLastPage()
    {
        byte[] data = new DbPatcher(Base).Truncate(Base.Length - 100).Data;

        using (Sqlite3Database db = new Sqlite3Database(new MemoryStream(data)))
        {
            SqliteParity.AssertTablesSameAsSqlite(Script, db, "Deep", "Small");
            SqliteCorruptException ex = Assert.Throws<SqliteCorruptException>(() => ReadTable(db, "Keyed"));
            Assert.Equal("HDR-17", ex.Check);
            Assert.Equal((uint)new DbPatcher(Base).PageCount, ex.Page);
        }

        List<string> issues = new List<string>();
        Sqlite3Settings lenient = new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add($"{i.Check} page {i.Page}") };
        SqliteParity.AssertSameAsSqlite(Script, _ => data, lenient);
        Assert.Equal(new[] { $"HDR-17 page {new DbPatcher(Base).PageCount}" }, issues);
    }

    // B-tree page headers

    /// <summary>
    /// PAGE-21: a child page with no cells (a leaf, or an interior page), and PAGE-19: an interior root other than page
    /// 1 with no cells, in a table and in a WITHOUT ROWID table. SQLite reports them corrupt, and so does Strict.
    /// Lenient reports the check once and walks on: the empty leaf has no rows, and an empty interior page still leads
    /// to its right-most child. The rows it does return are in order and each is intact (its payload holds its key), and
    /// the other tables read as SQLite reads them.
    /// </summary>
    [Theory]
    [InlineData("PAGE-21", "Deep", "first leaf")]
    [InlineData("PAGE-21", "Deep", "interior child")]
    [InlineData("PAGE-19", "Deep", "root")]
    [InlineData("PAGE-21", "Keyed", "first leaf")]
    [InlineData("PAGE-19", "Keyed", "root")]
    public void EmptyPage_Relaxable(string check, string table, string page)
    {
        uint emptied = (table, page) switch
        {
            ("Deep", "first leaf") => DeepFirstLeaf,
            ("Deep", "interior child") => DeepLeft,
            ("Deep", _) => DeepRoot,
            (_, "first leaf") => KeyedFirstLeaf,
            _ => KeyedRoot
        };
        byte[] data = new DbPatcher(Base).SetCellCount(emptied, 0).Data;

        SqliteParity.AssertSqliteReportsCorrupt(data, table);
        using (Sqlite3Database db = new Sqlite3Database(new MemoryStream(data)))
            Assert.Equal(check, Assert.Throws<SqliteCorruptException>(() => ReadTable(db, table)).Check);

        List<string> issues = new List<string>();
        Sqlite3Settings lenient = new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add(i.Check) };
        using (Sqlite3Database db = new Sqlite3Database(new MemoryStream(data), lenient))
        {
            List<long> keys = db.GetTable(table).EnumerateRows().Select(row =>
            {
                Assert.True(row.TryGetValueByName("Payload", out string payload));
                long key = table == "Deep" ? row.RowId : long.Parse(Assert.IsType<string>(row.ColumnData[0]).Substring(3));
                Assert.Equal(key.ToString(), payload.Trim());
                return key;
            }).ToList();

            Assert.NotEmpty(keys);
            Assert.True(keys.Count < (table == "Deep" ? 120 : 40));
            Assert.Equal(keys.OrderBy(k => k), keys);
            if (table == "Deep" && page == "first leaf")
                Assert.Equal(Enumerable.Range(2, 119).Select(i => (long)i), keys);

            SqliteParity.AssertTablesSameAsSqlite(Script, db, new[] { "Deep", "Small", "Keyed" }.Where(t => t != table).ToArray());
        }

        Assert.Equal(new[] { check }, issues);
    }

    // PAGE-18: page 1 holds the schema, a rowid table, so it must be a table b-tree page in either mode
    [Theory]
    [InlineData(SqliteValidation.Strict)]
    [InlineData(SqliteValidation.Lenient)]
    public void PageType_SchemaPageIsIndexPage(SqliteValidation validation)
    {
        byte[] data = new DbPatcher(Base).SetPageType(MasterRoot, 0x0A).Data;

        SqliteParity.AssertSqliteReportsCorrupt(data, "Small");
        Assert.Equal("PAGE-18", Assert.Throws<SqliteCorruptException>(() => new Sqlite3Database(new MemoryStream(data), new Sqlite3Settings { Validation = validation })).Check);
    }

    [Fact]
    public void PageType_Unknown() => AssertCorrupt("b-tree page header", p => p.SetPageType(DeepFirstLeaf, 0x01), "PAGE-01");

    [Fact]
    public void PageType_IndexPageInsideTableBTree() => AssertCorrupt("b-tree page header", p => p.SetPageType(DeepFirstLeaf, 0x0A), "PAGE-22");

    /// <summary>
    /// Small is a rowid table whose root becomes a leaf index page, and Keyed a WITHOUT ROWID table whose root becomes
    /// an interior table page. The root page alone can't tell, so GetTableDefinition, which knows the SQL, checks it.
    /// </summary>
    [Theory]
    [InlineData("Small", 0x0A)]
    [InlineData("Keyed", 0x05)]
    public void PageType_RootDisagreesWithSchemaSql(string table, byte rootType)
    {
        byte[] data = new DbPatcher(Base).SetPageType(RootPage(table), rootType).Data;
        SqliteParity.AssertSqliteReportsCorrupt(data, table);

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
        Assert.Equal("PAGE-18", Assert.Throws<SqliteCorruptException>(() => db.GetTable(table).GetTableDefinition()).Check);
    }

    [Fact]
    public void CellCount_PointerArrayPastEndOfPage() => AssertCorrupt("b-tree page header", p => p.SetCellCount(DeepFirstLeaf, 0xFFFF));

    // Page 1's b-tree header follows the 100 byte database header: 100 + 8 + 2 * 250 = 608 is past the page
    [Fact]
    public void CellCount_PointerArrayPastEndOfSchemaPage() => AssertCorrupt("b-tree page header", p => p.SetCellCount(MasterRoot, 250));

    // PAGE-03: 249 cells is more than the (512 - 8) / 6 = 84 a page can hold, in either mode. Their pointers would
    // overlap the cells, which would then read as garbage.
    [Theory]
    [InlineData(SqliteValidation.Strict)]
    [InlineData(SqliteValidation.Lenient)]
    public void CellCount_PointersOverlapCells(SqliteValidation validation)
    {
        byte[] data = new DbPatcher(Base).SetCellCount(SmallRoot, 249).Data;
        SqliteParity.AssertSqliteReportsCorrupt(data, "Small");

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data), new Sqlite3Settings { Validation = validation });
        Assert.Equal("PAGE-03", Assert.IsType<SqliteCorruptException>(RunBounded(() => ReadTable(db, "Small"))).Check);
    }

    // Child pointers

    // As in SQLite, the depth limit is what stops a cycle. Root and left page alternate, so the walk gives up at the
    // root's first child pointer.
    [Fact]
    public void Cycle_RightMostPointerBackToRoot() => AssertCorrupt("interior cell child pointer", p => p.SetRightMostPointer(DeepLeft, DeepRoot), "PAGE-24");

    [Fact]
    public void ChildPointer_PastTheEnd() => AssertCorrupt("interior cell child pointer", p => p.SetChildPointer(DeepLeft, 0, PastTheEnd), "PAGE-14");

    [Fact]
    public void WithoutRowId_ChildPointerPastTheEnd() => AssertCorrupt("interior cell child pointer", p => p.SetChildPointer(KeyedRoot, 0, PastTheEnd));

    // Every interior page has a right-most child
    [Fact]
    public void RightMostPointer_Zero() => AssertCorrupt("right-most pointer", p => p.SetRightMostPointer(DeepRoot, 0), "PAGE-15");

    // SCHEMA-18: SQLite loads a table with rootpage 0, and fails when it is read
    [Fact]
    public void SchemaRootPage_Zero() => AssertCorrupt("page number", p => SetDeepRootPageInSchema(p, 0));

    // Schema rows. Each case edits one sqlite_schema row with writable_schema, after a schema of tables, a view, an index, a
    // trigger, a table with an automatic index and a virtual table.
    private const string SchemaScript = @"
CREATE TABLE t (a); INSERT INTO t VALUES (1);
CREATE TABLE u (b); INSERT INTO u VALUES (2);
CREATE VIEW v AS SELECT a FROM t;
CREATE INDEX i ON t (a);
CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;
CREATE TABLE k (x PRIMARY KEY);
CREATE VIRTUAL TABLE f USING fts5(c);
";

    /// <summary>
    /// The checks SQLite makes on each sqlite_schema row while it loads the schema: the open fails in either mode
    /// </summary>
    [Theory]
    [InlineData("SCHEMA-11", "rootpage = NULL WHERE name = 'u'")]
    [InlineData("SCHEMA-11", "rootpage = NULL WHERE name = 'v'")]
    [InlineData("SCHEMA-13", "sql = ' ' || sql WHERE name = 'u'")]
    [InlineData("SCHEMA-13", "sql = '/*x*/' || sql WHERE name = 'u'")]
    [InlineData("SCHEMA-13", "sql = 'SELECT 1' WHERE name = 'u'")]
    [InlineData("SCHEMA-13", "name = NULL WHERE name = 'sqlite_autoindex_k_1'")]
    [InlineData("SCHEMA-15", "rootpage = 1 WHERE name = 'sqlite_autoindex_k_1'")]
    [InlineData("SCHEMA-15", "rootpage = '3x' WHERE name = 'sqlite_autoindex_k_1'")]
    [InlineData("SCHEMA-17", "rootpage = -2 WHERE name = 'u'")]
    [InlineData("SCHEMA-17", "rootpage = 2.5 WHERE name = 'u'")]
    [InlineData("SCHEMA-17", "rootpage = 'abc' WHERE name = 'u'")]
    [InlineData("SCHEMA-17", "rootpage = rootpage + 4294967296 WHERE name = 'u'")]
    [InlineData("SCHEMA-17", "rootpage = 999 WHERE name = 'u'")]
    [InlineData("SCHEMA-17", "rootpage = 999 WHERE name = 'v'")]
    [InlineData("SCHEMA-17", "rootpage = 999 WHERE name = 'i'")]
    [InlineData("SCHEMA-17", "rootpage = 99999 WHERE name = 'tr'")]
    [InlineData("SCHEMA-26", "name = NULL WHERE name = 'u'")]
    public void SchemaRow_CorruptLikeSqlite(string check, string update)
    {
        byte[] data = SqliteParity.Build(SchemaScript, $"UPDATE sqlite_schema SET {update}");
        SqliteParity.AssertSqliteReportsCorrupt(data, "t");

        foreach (SqliteValidation validation in new[] { SqliteValidation.Strict, SqliteValidation.Lenient })
        {
            Sqlite3Settings settings = new Sqlite3Settings { Validation = validation };
            Assert.Equal(check, Assert.Throws<SqliteCorruptException>(() => new Sqlite3Database(new MemoryStream(data), settings)).Check);
        }
    }

    /// <summary>
    /// The checks SQLite makes on a row's statement as it parses it: Strict fails the open, and Lenient keeps the error
    /// on the row, so only that row cannot be opened
    /// </summary>
    [Theory]
    [InlineData("SCHEMA-19", "u", "rootpage = 1 WHERE name = 'u'")]
    [InlineData("SCHEMA-19", "f", "rootpage = 1 WHERE name = 'f'")]
    [InlineData("SCHEMA-22", "u", "sql = 'CRxATE TABLE u (b)' WHERE name = 'u'")]
    [InlineData("DDL-58", "v", "rootpage = 2 WHERE name = 'v'")]
    [InlineData("DDL-58", "u", "sql = 'CREATE TABLE u AS SELECT 1' WHERE name = 'u'")]
    [InlineData("DDL-58", "u", "sql = 'CREATE /* c */ TEMPORARY TABLE IF NOT EXISTS \"u\" -- c' || char(10) || 'AS SELECT 1' WHERE name = 'u'")]
    [InlineData("DDL-58", "indexed", "sql = 'CREATE TABLE indexed AS SELECT 1', name = 'indexed', tbl_name = 'indexed' WHERE name = 'u'")]
    public void SchemaRow_StatementCorruptLikeSqlite(string check, string name, string update)
    {
        byte[] data = SqliteParity.Build(SchemaScript, $"UPDATE sqlite_schema SET {update}");
        SqliteParity.AssertSqliteReportsCorrupt(data, "t");

        Assert.Equal(check, Assert.Throws<SqliteCorruptException>(() => new Sqlite3Database(new MemoryStream(data))).Check);

        using Sqlite3Database lenient = new Sqlite3Database(new MemoryStream(data), new Sqlite3Settings { Validation = SqliteValidation.Lenient });
        Assert.Equal(check, Assert.Throws<SqliteCorruptException>(() => lenient.GetTable(name)).Check);
        SqliteParity.AssertTablesSameAsSqlite(SchemaScript, lenient, "t");
    }

    // SCHEMA-08: SQLite reads a rootpage stored as a BLOB of digits as text, so as that page. (Text digits would be
    // stored as an integer, the column's affinity.)
    [Fact]
    public void SchemaRow_RootPageBlob() =>
        SqliteParity.AssertSameAsSqlite(SchemaScript, "UPDATE sqlite_schema SET rootpage = CAST(CAST(rootpage AS TEXT) AS BLOB) WHERE name = 'u'");

    /// <summary>
    /// SCHEMA-26, DDL-03: a CREATE row's type, name and tbl_name must agree with its SQL. SQLite skips this with
    /// writable_schema on, so Lenient reports it and reads the table by its statement.
    /// </summary>
    [Theory]
    [InlineData("SCHEMA-26", "type = 'index' WHERE name = 'u'")]
    [InlineData("SCHEMA-26", "type = NULL WHERE name = 'u'")]
    [InlineData("DDL-03", "tbl_name = 't' WHERE name = 'u'")]
    [InlineData("DDL-03", "tbl_name = 'u' WHERE name = 'v'")]
    [InlineData("SCHEMA-26", "type = 'table' WHERE name = 'v'")]
    [InlineData("DDL-03", "sql = 'CREATE TABLE w (b)' WHERE name = 'u'")]
    public void SchemaRow_NamesDisagreeWithSql(string check, string update)
    {
        byte[] data = SqliteParity.Build(SchemaScript, $"UPDATE sqlite_schema SET {update}");
        SqliteParity.AssertSqliteReportsCorrupt(data, "t");

        SqliteCorruptException ex = Assert.Throws<SqliteCorruptException>(() =>
        {
            using Sqlite3Database strict = new Sqlite3Database(new MemoryStream(data));
            strict.GetTable("u").GetTableDefinition();
        });
        Assert.Equal(check, ex.Check);

        List<string> issues = new List<string>();
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data), new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add(i.Check) });
        SqliteParity.AssertTablesSameAsSqlite(SchemaScript, db, "t", "u");
        Assert.Equal(new[] { check }, issues);
    }

    /// <summary>
    /// SCHEMA-08, SCHEMA-09, SCHEMA-26: what SQLite reads on from. Every value reads as text, a BLOB too, and ends at a
    /// NUL; the type compares ignoring ASCII case.
    /// </summary>
    [Theory]
    [InlineData("type = 'TABLE' WHERE name = 'u'")]
    [InlineData("type = CAST(type AS BLOB), name = CAST(name AS BLOB), tbl_name = CAST(tbl_name AS BLOB), sql = CAST(sql AS BLOB) WHERE name = 'u'")]
    [InlineData("sql = sql || char(0) || 'garbage ((' WHERE name = 'u'")]
    [InlineData("rootpage = 1 WHERE name = 'tr'")]
    [InlineData("sql = 'CREATE  /* x */ VIRTUAL TABLE f USING fts5(c)' WHERE name = 'f'")]
    public void SchemaRow_ValuesReadLikeSqlite(string update) => SqliteParity.AssertSameAsSqlite(SchemaScript, $"UPDATE sqlite_schema SET {update}");

    // SCHEMA-58: SQLite tolerates a rootpage on a virtual table, and never reads it as a b-tree
    [Fact]
    public void SchemaRow_VirtualTableWithRootPage()
    {
        byte[] data = SqliteParity.Build(SchemaScript, "UPDATE sqlite_schema SET rootpage = (SELECT rootpage FROM sqlite_schema WHERE name = 't') WHERE name = 'f'");

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
        Assert.Throws<NotSupportedException>(() => db.GetTable("f"));
        SqliteParity.AssertTablesSameAsSqlite(SchemaScript, db, "t", "u");
    }

    // Row 1 is the last cell on Small's page. Its payload size (the 1-byte varint 7, before the rowid varint and the
    // record) becomes 127, which no longer fits in the page.
    [Fact]
    public void LeafCell_PayloadPastEndOfPage() => AssertCorrupt("b-tree leaf cell", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _) - 2, 7, 0x7F));

    // The first key is the last cell on Keyed's first leaf, a 60 byte cell at offset 452. Its payload size (the 1-byte
    // varint 59, before the record) becomes 100, which is still below the index page overflow threshold of 102 and no
    // longer fits in the page.
    // CELL-11: a payload that is all on its page is read straight from the page, even where it runs into the reserved
    // bytes. Small's row 1 is the cell at the end of the usable area; its payload grows by 2 bytes into the 8 reserved
    // bytes (zeros), and its text value from 3 to 5 bytes to match.
    [Fact]
    public void LeafCell_PayloadOverReservedBytes() =>
        SqliteParity.AssertSameAsSqlite(Script, _ =>
        {
            byte[] data = SqliteParity.Build(Script, 8);
            uint root;
            using (Sqlite3Database db = new Sqlite3Database(new MemoryStream(data)))
                root = db.GetTable("Small").SchemaDefinition.RootPage;

            DbPatcher p = new DbPatcher(data);
            int record = p.GetRecordOffset(root, 0, out long payloadSize);
            Assert.Equal(7, payloadSize);
            Assert.Equal(p.PageOffset(root) + p.PageSize - 8, record + 7);
            return p.WriteByte(record - 2, 7, 9).WriteByte(record + 2, 19, 23).Data;
        });

    /// <summary>
    /// Cells are read where SQLite reads them. PAGE-11, CELL-08: a cell pointer is masked to the page size, not checked,
    /// so Small's first pointer plus the page size points at the same cell. CELL-01: the payload size varint keeps its
    /// low 32 bits, so row 1 rewritten in free space with its size 7 encoded as the 5-byte varint of 2^32 + 7 reads as
    /// before.
    /// </summary>
    [Theory]
    [InlineData("pointer past the page")]
    [InlineData("payload size over 32 bits")]
    public void LeafCell_ReadLikeSqlite(string variant) =>
        SqliteParity.AssertSameAsSqlite(Script, data =>
        {
            DbPatcher p = new DbPatcher(data);
            int record = p.GetRecordOffset(SmallRoot, 0, out _);
            int cell = record - 2 - p.PageOffset(SmallRoot);
            if (variant == "pointer past the page")
                return p.SetCellOffset(SmallRoot, 0, (ushort)(cell + p.PageSize)).Data;

            const int at = 100;
            byte[] rewritten = new byte[] { 0x90, 0x80, 0x80, 0x80, 0x07, data[record - 1] }.Concat(data.Skip(record).Take(7)).ToArray();
            for (int i = 0; i < rewritten.Length; i++)
                p.WriteByte(p.PageOffset(SmallRoot) + at + i, 0, rewritten[i]);

            return p.SetCellOffset(SmallRoot, 0, at).Data;
        });

    [Fact]
    public void WithoutRowId_LeafCellPayloadPastEndOfPage() => AssertCorrupt("b-tree leaf cell", p => p.WriteByte(p.GetRecordOffset(KeyedFirstLeaf, 0, out _) - 1, 59, 100));

    // Records. Small's row 1 is (NULL rowid alias, 'one', NULL), its record header bytes are: size, 0, 19, 0.

    [Fact]
    public void Record_HeaderLargerThanPayload() => AssertCorrupt("record header", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _), 4, 0x7F));

    // The payload is 7 bytes: the 4 byte header and 'one'. Serial type 21 is a 4 byte text, one byte more than the payload holds.
    [Fact]
    public void Record_SerialTypeTooLargeForPayload() => AssertCorrupt("record header", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _) + 2, 19, 21));

    /// <summary>
    /// Record headers SQLite reads although it never writes them. REC-04: a header size of 0 is a record of no fields,
    /// so every column reads as NULL. REC-10: fields after the last column read are never parsed; Small's schema is
    /// patched to drop its Data column, and the now surplus third field gets a serial type larger than the payload.
    /// REC-04 again: a 0-byte payload takes its header size from the byte after the cell, here a 0.
    /// </summary>
    [Theory]
    [InlineData("header size 0")]
    [InlineData("surplus field")]
    [InlineData("0-byte payload")]
    [InlineData("WITHOUT ROWID header size 0")]
    public void Record_ReadLikeSqlite(string variant) =>
        SqliteParity.AssertSameAsSqlite(Script, data =>
        {
            DbPatcher p = new DbPatcher(data);
            int record = p.GetRecordOffset(SmallRoot, 0, out _);
            switch (variant)
            {
                case "header size 0":
                    p.WriteByte(record, 4, 0);
                    break;
                case "surplus field":
                    DropSmallDataColumn(p);
                    p.WriteByte(record + 3, 0, 0x7F);
                    break;
                case "WITHOUT ROWID header size 0":
                    p.WriteByte(p.GetRecordOffset(KeyedFirstLeaf, 0, out _), 3, 0);
                    break;
                default:
                    p.WriteByte(record - 2, 7, 0).WriteByte(record, 4, 0);
                    break;
            }

            return p.Data;
        });

    /// <summary>
    /// REC-05: a header size of 1 leaves no room for a serial type, and SQLite reports the record corrupt. REC-03: a
    /// 0-byte payload whose next byte is not 0 has a header larger than the payload.
    /// </summary>
    [Theory]
    [InlineData("CELL-23", "header size 1")]
    [InlineData("REC-03", "0-byte payload")]
    public void Record_CorruptLikeSqlite(string check, string variant)
    {
        DbPatcher p = new DbPatcher(Base);
        int record = p.GetRecordOffset(SmallRoot, 0, out _);
        byte[] data = (variant == "header size 1" ? p.WriteByte(record, 4, 1) : p.WriteByte(record - 2, 7, 0)).Data;

        SqliteParity.AssertSqliteReportsCorrupt(data, "Small");
        SqliteCorruptException ex = Assert.IsType<SqliteCorruptException>(RunBounded(() => ReadEverything(data)));
        Assert.Equal(check, ex.Check);
    }

    // A record that failed to read fails again on the next read, as in SQLite, instead of reading as fewer fields
    [Fact]
    public void Record_FailsOnEveryRead()
    {
        DbPatcher p = new DbPatcher(Base);
        byte[] data = p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _), 4, 1).Data;

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
        Sqlite3Row row = db.GetTable("Small").EnumerateRows().First();
        Assert.Equal("CELL-23", Assert.Throws<SqliteCorruptException>(() => row.TryGetOrdinal(1, out _)).Check);
        Assert.Equal("CELL-23", Assert.Throws<SqliteCorruptException>(() => row.TryGetOrdinal(1, out _)).Check);
    }

    /// <summary>
    /// Blanks ", Data BLOB" out of Small's CREATE TABLE in the schema, so the table has two columns while its records
    /// keep three fields
    /// </summary>
    private static void DropSmallDataColumn(DbPatcher p)
    {
        byte[] column = System.Text.Encoding.ASCII.GetBytes(", Data BLOB");
        int at = p.Data.AsSpan(0, p.PageSize).IndexOf(column);
        Assert.True(at > 0);
        for (int i = 0; i < column.Length; i++)
            p.WriteByte(at + i, column[i], (byte)' ');
    }

    // Overflow chains. Without its overflow pages, row 2's blob would come back zero-filled.

    [Fact]
    public void Overflow_ChainEndsEarly() => AssertCorrupt("overflow chain", p => p.SetOverflowPointer(SmallRoot, 1, 0));

    // CELL-16: SQLite never follows the last overflow page's next pointer, so a bad one reads on
    [Fact]
    public void Overflow_LastNextPointerIgnored()
    {
        DbPatcher patcher = new DbPatcher(Base);
        byte[] data = patcher.SetOverflowNextPointer(patcher.GetOverflowChain(SmallRoot, 1).Last(), PastTheEnd).Data;

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
        SqliteParity.AssertTablesSameAsSqlite(Script, db, "Small");
    }

    // CELL-13: a chain that ends on an intermediate overflow page, while payload remains
    [Fact]
    public void Overflow_ChainEndsOnOverflowPage()
    {
        DbPatcher patcher = new DbPatcher(Base);
        List<uint> chain = patcher.GetOverflowChain(SmallRoot, 1);
        Assert.True(chain.Count >= 2);

        Exception ex = RunBounded(() => ReadEverything(patcher.SetOverflowNextPointer(chain[0], 0).Data));
        SqliteCorruptException corrupt = Assert.IsType<SqliteCorruptException>(ex);
        Assert.Equal("overflow chain", corrupt.Structure);
        Assert.Equal("CELL-13", corrupt.Check);
    }

    // HDR-20: the lock-byte page is never data, in either mode. The database is padded with zero pages past 1 GiB, and
    // Small's overflow chain points at the page holding byte 0x40000000.
    [Theory]
    [InlineData(SqliteValidation.Strict)]
    [InlineData(SqliteValidation.Lenient)]
    public void Overflow_PointerToLockBytePage(SqliteValidation validation)
    {
        DbPatcher patcher = new DbPatcher(Base);
        uint lockBytePage = (uint)(0x40000000 / patcher.PageSize + 1);
        byte[] data = patcher.SetDatabaseSizeInPages(lockBytePage + 1).SetOverflowPointer(SmallRoot, 1, lockBytePage).Data;

        Dictionary<long, byte[]> pages = new Dictionary<long, byte[]>();
        for (int page = 1; page <= patcher.PageCount; page++)
            pages[page] = data.AsSpan(patcher.PageOffset((uint)page), patcher.PageSize).ToArray();

        List<string> issues = new List<string>();
        Sqlite3Settings settings = new Sqlite3Settings { Validation = validation, OnValidationIssue = i => issues.Add(i.Check) };
        using Sqlite3Database db = new Sqlite3Database(new PageMappedStream(patcher.PageSize, (lockBytePage + 1L) * patcher.PageSize, pages), settings);
        SqliteCorruptException ex = Assert.Throws<SqliteCorruptException>(() => ReadTable(db, "Small"));
        Assert.Equal("HDR-20", ex.Check);
        Assert.Empty(issues);
    }

    [Fact]
    public void Overflow_PointerPastTheEnd() => AssertCorrupt("page number", p => p.SetOverflowPointer(SmallRoot, 1, PastTheEnd));

    [Fact]
    public void Corruption_OtherTablesStillReadLikeSqlite()
    {
        byte[] data = new DbPatcher(Base).SetRightMostPointer(DeepLeft, DeepRoot).Data;

        Exception ex = RunBounded(() =>
        {
            using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));

            Assert.Equal(DeepLeft, Assert.Throws<SqliteCorruptException>(() => ReadTable(db, "Deep")).Page);

            // The failure is not sticky: the same database still reads its other table, and fails the same way again
            SqliteParity.AssertTablesSameAsSqlite(Script, db, "Small");
            Assert.Throws<SqliteCorruptException>(() => ReadTable(db, "Deep"));
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Corruption_OverflowChainCycle_Terminates()
    {
        // The second overflow page points back to the first. The chain length is bounded by the payload size, so
        // this must terminate. Detecting the cycle itself is a follow-up.
        DbPatcher patcher = new DbPatcher(Base);
        uint first = patcher.GetOverflowPointer(SmallRoot, 1);
        uint second = patcher.ReadUInt32(patcher.PageOffset(first));
        patcher.SetOverflowNextPointer(second, first);

        AssertTerminatesGracefully(() => ReadEverything(patcher.Data));
    }

    [Fact]
    public void Corruption_RandomByteDamage_FailsGracefully()
    {
        // A smoke test for what the named cases miss: overwrite a few random bytes, with a fixed seed so a failure
        // reproduces
        Random random = new Random(20261019);

        for (int i = 0; i < 300; i++)
        {
            DbPatcher patcher = new DbPatcher(Base);
            for (int j = random.Next(1, 5); j > 0; j--)
                patcher.Data[random.Next(patcher.Data.Length)] = (byte)random.Next(256);

            AssertTerminatesGracefully(() => ReadEverything(patcher.Data));
        }
    }
}
