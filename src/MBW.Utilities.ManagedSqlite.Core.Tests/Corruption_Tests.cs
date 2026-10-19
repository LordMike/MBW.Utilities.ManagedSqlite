using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

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
    /// interior -> leaf). Small fits on one leaf page, and its row 2 overflows into a chain of overflow pages.
    /// </summary>
    private const string Script = @"
PRAGMA page_size = 512;
CREATE TABLE Deep (Id INTEGER PRIMARY KEY, Payload TEXT);
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 120)
INSERT INTO Deep SELECT i, printf('%300d', i) FROM n;
CREATE TABLE Small (Id INTEGER PRIMARY KEY, Name TEXT, Data BLOB);
INSERT INTO Small VALUES (1, 'one', NULL), (2, 'two', zeroblob(1500)), (3, 'three', x'0102');";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly byte[] Base = SqliteParity.Build(Script);

    private const uint MasterRoot = 1;
    private static readonly uint DeepRoot = RootPage("Deep");
    private static readonly uint DeepLeft = new DbPatcher(Base).GetChildPointer(DeepRoot, 0);
    private static readonly uint DeepRight = new DbPatcher(Base).GetRightMostPointer(DeepRoot);
    private static readonly uint DeepFirstLeaf = new DbPatcher(Base).GetChildPointer(DeepLeft, 0);
    private static readonly uint SmallRoot = RootPage("Small");
    private static readonly uint PastTheEnd = (uint)new DbPatcher(Base).PageCount + 1;

    private static uint RootPage(string table)
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(Base));
        return db.GetTable(table).SchemaDefinition.RootPage;
    }

    /// <summary>
    /// Each case: the <see cref="SqliteCorruptException.Structure"/> it must report, so the case fails if a later,
    /// unrelated check is what catches it, and the patch that damages the database
    /// </summary>
    private static readonly Dictionary<string, (string Structure, Action<DbPatcher> Patch)> Cases = new()
    {
        // Database header
        ["header: bad magic"] = ("magic bytes", p => p.WriteByte(0, (byte)'X')),
        // A page size is a power of two from 512 to 65536. The stored value 1 means 65536, read by Structure_Tests.
        ["header: page size 0, below 512"] = ("database header", p => p.SetPageSizeField(0)),
        ["header: page size 1000, not a power of two"] = ("database header", p => p.SetPageSizeField(1000)),
        ["header: usable size below 480"] = ("database header", p => p.SetReservedSpace(255)),

        // File length
        ["truncated: empty file"] = ("file length", p => p.Truncate(0)),
        ["truncated: inside the header"] = ("file length", p => p.Truncate(50)),
        ["truncated: after the schema page"] = ("file length", p => p.Truncate(p.PageSize)),
        ["truncated: last page missing"] = ("file length", p => p.Truncate((p.PageCount - 1) * p.PageSize)),

        // B-tree page headers
        ["page type: unknown"] = ("b-tree page header", p => p.SetPageType(DeepFirstLeaf, 0x01)),
        ["page type: index page inside a table b-tree"] = ("b-tree page header", p => p.SetPageType(DeepFirstLeaf, 0x0A)),
        ["cell count: pointer array past the end of the page"] = ("b-tree page header", p => p.SetCellCount(DeepFirstLeaf, 0xFFFF)),
        // Page 1's b-tree header follows the 100 byte database header: 100 + 8 + 2 * 250 = 608 is past the page
        ["cell count: pointer array past the end of the schema page"] = ("b-tree page header", p => p.SetCellCount(MasterRoot, 250)),
        // 249 pointers end at byte 8 + 2 * 249 = 506, past where the cells start, so they overlap the cells
        ["cell count: pointers overlap the cells"] = ("cell pointer array", p => p.SetCellCount(SmallRoot, 249)),

        // Child pointers
        ["cycle: right-most pointer back to the root"] = ("right-most pointer", p => p.SetRightMostPointer(DeepLeft, DeepRoot)),
        // The first leaf is also listed as a child of the right subtree, which would read its rows twice
        ["shared page: child of two parents"] = ("interior cell child pointer", p => p.SetChildPointer(DeepRight, 0, DeepFirstLeaf)),
        // Leaves become a chain of interior pages with only a right-most child each: no cycle, but far deeper than any
        // real b-tree. The left interior page is cut down to only the chain, so no page is reached twice.
        ["depth: chain of 31 interior pages"] = ("right-most pointer", p =>
        {
            p.MakeEmptyInterior(DeepLeft, DeepFirstLeaf);
            for (uint page = DeepFirstLeaf; page < DeepFirstLeaf + 30; page++)
                p.MakeEmptyInterior(page, page + 1);
        }),
        ["child pointer: page 1, the schema root"] = ("interior cell child pointer", p => p.SetChildPointer(DeepLeft, 0, 1)),
        ["child pointer: past the end"] = ("interior cell child pointer", p => p.SetChildPointer(DeepLeft, 0, PastTheEnd)),
        // Every interior page has a right-most child
        ["right-most pointer: 0"] = ("right-most pointer", p => p.SetRightMostPointer(DeepRoot, 0)),
        ["schema root page: 0"] = ("page number", p => SetDeepRootPageInSchema(p, 0)),
        // The 1-byte integer 0xFF is -1
        ["schema root page: -1"] = ("page number", p => SetDeepRootPageInSchema(p, 0xFF)),

        // Row 1 is the last cell on Small's page. Its payload size (the 1-byte varint 7, before the rowid varint and
        // the record) becomes 127, which no longer fits in the page.
        ["leaf cell: payload past the end of the page"] = ("b-tree leaf cell", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _) - 2, 0x7F)),

        // Records. Small's row 1 is (NULL rowid alias, 'one', NULL), its record header bytes are: size, 0, 19, 0.
        ["record: reserved serial type 10"] = ("record header", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _) + 1, 10)),
        ["record: reserved serial type 11"] = ("record header", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _) + 1, 11)),
        ["record: header larger than the payload"] = ("record header", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _), 0x7F)),
        // The payload is 7 bytes: the 4 byte header and 'one'. Serial type 127 is a 57 byte text, past any value that fits.
        ["record: serial type larger than the payload"] = ("record header", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _) + 2, 127)),
        // Serial type 21 is a 4 byte text, one byte more than the payload holds
        ["record: values longer than the payload"] = ("record header", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _) + 2, 21)),
        // The "type" column of the first schema row holds 'table' (serial type 23, text). 22 is a 5 byte blob.
        ["record: schema type stored as a blob"] = (null, p => p.WriteByte(p.GetRecordOffset(MasterRoot, 0, out _) + 1, 22)),

        // Overflow chains. Without its overflow pages, row 2's blob would come back zero-filled.
        ["overflow: chain ends early"] = ("overflow chain", p => p.SetOverflowPointer(SmallRoot, 1, 0)),
        ["overflow: pointer past the end"] = ("page number", p => p.SetOverflowPointer(SmallRoot, 1, PastTheEnd)),
    };

    public static TheoryData<string> CaseNames()
    {
        TheoryData<string> res = new TheoryData<string>();
        foreach (string name in Cases.Keys)
            res.Add(name);

        return res;
    }

    /// <summary>
    /// The first schema row is Deep's. Its rootpage column is a 1-byte integer that follows "table", "Deep", "Deep".
    /// </summary>
    private static void SetDeepRootPageInSchema(DbPatcher patcher, byte value)
    {
        int record = patcher.GetRecordOffset(MasterRoot, 0, out _);
        Assert.Equal(1, patcher.Data[record + 4]);

        int rootPageValue = record + patcher.Data[record] + 5 + 4 + 4;
        Assert.Equal(DeepRoot, patcher.Data[rootPageValue]);
        patcher.WriteByte(rootPageValue, value);
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

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Corruption_ThrowsSqliteCorruptException(string name)
    {
        (string structure, Action<DbPatcher> patch) = Cases[name];
        DbPatcher patcher = new DbPatcher(Base);
        patch(patcher);

        Exception ex = RunBounded(() => ReadEverything(patcher.Data));

        Assert.Equal(structure, Assert.IsType<SqliteCorruptException>(ex).Structure);
    }

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
        patcher.WriteUInt32(patcher.PageOffset(second), first);

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
                patcher.WriteByte(random.Next(patcher.Data.Length), (byte)random.Next(256));

            AssertTerminatesGracefully(() => ReadEverything(patcher.Data));
        }
    }
}
