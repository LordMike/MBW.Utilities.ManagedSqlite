using System;
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
    private static readonly uint DeepFirstLeaf = new DbPatcher(Base).GetChildPointer(DeepLeft, 0);
    private static readonly uint SmallRoot = RootPage("Small");
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

    /// <summary>
    /// Damages a copy of the database with <paramref name="patch"/>, reads everything, and asserts it fails with a
    /// <see cref="SqliteCorruptException"/> for <paramref name="structure"/>. Checking the structure makes a case fail
    /// if a later, unrelated check is what catches it.
    /// </summary>
    private static void AssertCorrupt(string structure, Action<DbPatcher> patch)
    {
        DbPatcher patcher = new DbPatcher(Base);
        patch(patcher);

        Exception ex = RunBounded(() => ReadEverything(patcher.Data));

        Assert.Equal(structure, Assert.IsType<SqliteCorruptException>(ex).Structure);
    }

    // Database header

    [Fact]
    public void Header_BadMagic() => AssertCorrupt("magic bytes", p => p.WriteByte(0, (byte)'X'));

    // A page size is a power of two from 512 to 65536. The stored value 1 means 65536, read by Structure_Tests.
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void Header_InvalidPageSize(ushort pageSize) => AssertCorrupt("database header", p => p.SetPageSizeField(pageSize));

    [Fact]
    public void Header_UsableSizeBelow480() => AssertCorrupt("database header", p => p.SetReservedSpace(255));

    // The in-header database size is valid (SQLite wrote the change counter and version-valid-for fields), so it decides
    // how many pages the database has: more than the file holds is corrupt, and pages after it are not read
    [Fact]
    public void Header_DatabaseSizeLargerThanFile() => AssertCorrupt("file length", p => p.SetDatabaseSizeInPages((uint)p.PageCount + 1));

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

    [Fact]
    public void Truncated_EmptyFile() => AssertCorrupt("file length", p => p.Truncate(0));

    [Fact]
    public void Truncated_InsideTheHeader() => AssertCorrupt("file length", p => p.Truncate(50));

    [Fact]
    public void Truncated_AfterTheSchemaPage() => AssertCorrupt("file length", p => p.Truncate(p.PageSize));

    [Fact]
    public void Truncated_LastPageMissing() => AssertCorrupt("file length", p => p.Truncate((p.PageCount - 1) * p.PageSize));

    // A partial last page still counts as a page, so the file opens and a table that does not touch that page reads as
    // before. (The last page holds Small's overflow; SQLite reads the missing bytes as zeros, which the reader does not yet.)
    [Fact]
    public void Truncated_InsideTheLastPage()
    {
        byte[] data = new DbPatcher(Base).Truncate(Base.Length - 1).Data;

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));
        SqliteParity.AssertTablesSameAsSqlite(Script, db, "Deep");
    }

    // B-tree page headers

    [Fact]
    public void PageType_Unknown() => AssertCorrupt("b-tree page header", p => p.SetPageType(DeepFirstLeaf, 0x01));

    [Fact]
    public void PageType_IndexPageInsideTableBTree() => AssertCorrupt("b-tree page header", p => p.SetPageType(DeepFirstLeaf, 0x0A));

    [Fact]
    public void CellCount_PointerArrayPastEndOfPage() => AssertCorrupt("b-tree page header", p => p.SetCellCount(DeepFirstLeaf, 0xFFFF));

    // Page 1's b-tree header follows the 100 byte database header: 100 + 8 + 2 * 250 = 608 is past the page
    [Fact]
    public void CellCount_PointerArrayPastEndOfSchemaPage() => AssertCorrupt("b-tree page header", p => p.SetCellCount(MasterRoot, 250));

    // 249 pointers end at byte 8 + 2 * 249 = 506, past where the cells start, so they overlap the cells
    [Fact]
    public void CellCount_PointersOverlapCells() => AssertCorrupt("cell pointer array", p => p.SetCellCount(SmallRoot, 249));

    // Child pointers

    // As in SQLite, the depth limit is what stops a cycle. Root and left page alternate, so the walk gives up at the
    // root's first child pointer.
    [Fact]
    public void Cycle_RightMostPointerBackToRoot() => AssertCorrupt("interior cell child pointer", p => p.SetRightMostPointer(DeepLeft, DeepRoot));

    [Fact]
    public void ChildPointer_PastTheEnd() => AssertCorrupt("interior cell child pointer", p => p.SetChildPointer(DeepLeft, 0, PastTheEnd));

    // Every interior page has a right-most child
    [Fact]
    public void RightMostPointer_Zero() => AssertCorrupt("right-most pointer", p => p.SetRightMostPointer(DeepRoot, 0));

    // 0xFF is the 1-byte integer -1
    [Theory]
    [InlineData(0)]
    [InlineData(0xFF)]
    public void SchemaRootPage_Invalid(byte rootPage) => AssertCorrupt("page number", p => SetDeepRootPageInSchema(p, rootPage));

    // Row 1 is the last cell on Small's page. Its payload size (the 1-byte varint 7, before the rowid varint and the
    // record) becomes 127, which no longer fits in the page.
    [Fact]
    public void LeafCell_PayloadPastEndOfPage() => AssertCorrupt("b-tree leaf cell", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _) - 2, 0x7F));

    // Records. Small's row 1 is (NULL rowid alias, 'one', NULL), its record header bytes are: size, 0, 19, 0.

    [Fact]
    public void Record_HeaderLargerThanPayload() => AssertCorrupt("record header", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _), 0x7F));

    // The payload is 7 bytes: the 4 byte header and 'one'. Serial type 127 is a 57 byte text, past any value that
    // fits, and 21 is a 4 byte text, one byte more than the payload holds.
    [Theory]
    [InlineData(127)]
    [InlineData(21)]
    public void Record_SerialTypeTooLargeForPayload(byte serialType) => AssertCorrupt("record header", p => p.WriteByte(p.GetRecordOffset(SmallRoot, 0, out _) + 2, serialType));

    // Overflow chains. Without its overflow pages, row 2's blob would come back zero-filled.

    [Fact]
    public void Overflow_ChainEndsEarly() => AssertCorrupt("overflow chain", p => p.SetOverflowPointer(SmallRoot, 1, 0));

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

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void ReservedSerialType_ReadsAsNull(byte serialType)
    {
        // SQLite reads the reserved serial types 10 and 11 as NULL. Small's row 1 stores NULL in its first column.
        DbPatcher patcher = new DbPatcher(Base);
        patcher.WriteByte(patcher.GetRecordOffset(SmallRoot, 0, out _) + 1, serialType);

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(patcher.Data));
        Sqlite3Row row = db.GetTable("Small").EnumerateRows().First();

        Assert.Null(row.ColumnData[0]);
        Assert.Equal("one", row.ColumnData[1]);
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
