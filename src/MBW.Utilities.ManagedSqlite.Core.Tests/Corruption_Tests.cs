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
/// Named, structure-aware corruption cases. Each one patches a few bytes of Corruption.db (see Corruption_Create.sql)
/// in memory and checks the contract: reading terminates, and malformed input surfaces as <see cref="SqliteCorruptException"/>.
/// Every case runs with a timeout, so a regression (such as a page cycle) fails the test instead of hanging the run.
/// </summary>
public class Corruption_Tests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly byte[] Fixture = LoadFixture();

    private const uint MasterRoot = 1;

    /// <summary>
    /// Root of the "Deep" table: an interior page whose children are the interior pages <see cref="DeepLeft"/>
    /// and <see cref="DeepRight"/>, which point to leaves holding one row each
    /// </summary>
    private const uint DeepRoot = 2;

    private static readonly uint DeepLeft = new DbPatcher(Fixture).GetChildPointer(DeepRoot, 0);
    private static readonly uint DeepRight = new DbPatcher(Fixture).GetRightMostPointer(DeepRoot);
    private static readonly uint DeepFirstLeaf = new DbPatcher(Fixture).GetChildPointer(DeepLeft, 0);

    /// <summary>
    /// Root of the "Small" table, a single leaf with 3 rows. Row 2 (cell 1) overflows.
    /// </summary>
    private static readonly uint SmallRoot = GetRootPage("Small");

    private static byte[] LoadFixture()
    {
        using Stream stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.Corruption.db");
        using MemoryStream ms = new MemoryStream();
        stream.CopyTo(ms);

        return ms.ToArray();
    }

    private static uint GetRootPage(string table)
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(Fixture));
        return db.GetTables().Single(s => s.Name == table).RootPage;
    }

    private static DbPatcher Patch() => new DbPatcher(Fixture);

    /// <summary>
    /// Opens the database, lists its tables and reads every row of every table
    /// </summary>
    private static Dictionary<string, int> ReadEverything(byte[] data)
    {
        Dictionary<string, int> rowCounts = new Dictionary<string, int>();

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));

        foreach (Sqlite3SchemaRow schema in db.GetTables().ToList())
        {
            if (schema.Type != "table")
                continue;

            rowCounts[schema.Name] = ReadTable(db, schema.Name);
        }

        return rowCounts;
    }

    private static int ReadTable(Sqlite3Database db, string table)
    {
        int count = 0;
        foreach (Sqlite3Row row in db.GetTable(table).EnumerateRows())
        {
            Assert.NotNull(row.ColumnData);
            count++;
        }

        return count;
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

    private static SqliteCorruptException AssertCorrupt(Action action)
    {
        Exception ex = RunBounded(action);

        Assert.NotNull(ex);
        return Assert.IsType<SqliteCorruptException>(ex);
    }

    private static SqliteCorruptException AssertCorrupt(DbPatcher patched) => AssertCorrupt(() => ReadEverything(patched.Data));

    /// <summary>
    /// Asserts that the action terminates, and that if it fails, it fails with a <see cref="SqliteException"/>
    /// </summary>
    private static void AssertTerminatesGracefully(Action action)
    {
        Exception ex = RunBounded(action);

        if (ex != null)
            Assert.IsAssignableFrom<SqliteException>(ex);
    }

    [Fact]
    public void Fixture_IsReadable()
    {
        Assert.Equal(0x05, Patch().GetPageType(DeepLeft));
        Assert.Equal(0x05, Patch().GetPageType(DeepRight));
        Assert.Equal(0x0D, Patch().GetPageType(DeepFirstLeaf));
        Assert.Equal(0x0D, Patch().GetPageType(SmallRoot));

        Exception ex = RunBounded(() =>
        {
            Dictionary<string, int> counts = ReadEverything(Fixture);
            Assert.Equal(120, counts["Deep"]);
            Assert.Equal(3, counts["Small"]);
        });

        Assert.Null(ex);
    }

    // Database header

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)3)]
    [InlineData((ushort)100)]
    [InlineData((ushort)256)]
    [InlineData((ushort)1000)]
    [InlineData((ushort)(100000 & 0xFFFF))]
    public void Header_InvalidPageSize(ushort pageSize)
    {
        SqliteCorruptException ex = AssertCorrupt(Patch().SetPageSizeField(pageSize));
        Assert.Equal("database header", ex.Structure);
    }

    [Fact]
    public void Header_PageSizeOne_IsAccepted()
    {
        // 1 is how a 65536 byte page size is stored. It is valid, so it must not be rejected as an invalid page
        // size. This file's pages are 512 bytes though, so reading may still fail, but only in a defined way.
        AssertTerminatesGracefully(() => ReadEverything(Patch().SetPageSizeField(1).Data));
    }

    [Fact]
    public void Header_UsableSizeBelow480()
    {
        // 512 - 255 = 257 usable bytes per page, below SQLite's minimum of 480
        SqliteCorruptException ex = AssertCorrupt(Patch().SetReservedSpace(255));
        Assert.Equal("database header", ex.Structure);
    }

    [Fact]
    public void Header_BadMagic()
    {
        AssertCorrupt(Patch().SetMagicByte(0, (byte)'X'));
    }

    // Truncation

    public static IEnumerable<object[]> TruncationPoints()
    {
        DbPatcher patcher = new DbPatcher(Fixture);

        // Every page of the fixture is in use, so cutting the file at any page boundary loses data
        for (int pages = 0; pages < patcher.PageCount; pages++)
            yield return new object[] { pages };
    }

    [Theory]
    [MemberData(nameof(TruncationPoints))]
    public void Truncated_AtPageBoundary(int pages)
    {
        AssertCorrupt(Patch().TruncateToPages(pages));
    }

    [Fact]
    public void Truncated_InsideHeader()
    {
        DbPatcher patcher = Patch();
        byte[] data = patcher.Data.Take(50).ToArray();

        AssertCorrupt(() => ReadEverything(data));
    }

    // B-tree page headers

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x01)]
    [InlineData((byte)0xFF)]
    public void PageType_UnknownOnRoot(byte type)
    {
        SqliteCorruptException ex = AssertCorrupt(Patch().SetPageType(DeepRoot, type));
        Assert.Equal(DeepRoot, ex.Page);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x01)]
    [InlineData((byte)0xFF)]
    public void PageType_UnknownOnLeaf(byte type)
    {
        SqliteCorruptException ex = AssertCorrupt(Patch().SetPageType(DeepFirstLeaf, type));
        Assert.Equal(DeepFirstLeaf, ex.Page);
    }

    [Theory]
    [InlineData((byte)0x02)]
    [InlineData((byte)0x0A)]
    public void PageType_IndexPageInsideTableTree(byte type)
    {
        AssertCorrupt(Patch().SetPageType(DeepFirstLeaf, type));
    }

    [Fact]
    public void CellCount_HugeOnRoot()
    {
        AssertCorrupt(Patch().SetCellCount(DeepRoot, 0xFFFF));
    }

    [Fact]
    public void CellCount_HugeOnLeaf()
    {
        AssertCorrupt(Patch().SetCellCount(DeepFirstLeaf, 0xFFFF));
    }

    [Fact]
    public void CellCount_HugeOnSchemaPage()
    {
        AssertCorrupt(Patch().SetCellCount(MasterRoot, 0xFFFF));
    }

    [Fact]
    public void CellCount_JustTooManyForPage()
    {
        // 249 pointers end at byte 8 + 2 * 249 = 506, past where the cells start, so they overlap the cells
        AssertCorrupt(Patch().SetCellCount(SmallRoot, 249));
    }

    // Cycles and bad child pointers

    [Fact]
    public void Cycle_RootRightMostPointerToItself()
    {
        SqliteCorruptException ex = AssertCorrupt(Patch().SetRightMostPointer(DeepRoot, DeepRoot));
        Assert.Equal("right-most pointer", ex.Structure);
    }

    [Fact]
    public void Cycle_ChildPointerToItself()
    {
        SqliteCorruptException ex = AssertCorrupt(Patch().SetChildPointer(DeepLeft, 0, DeepLeft));
        Assert.Equal(DeepLeft, ex.Page);
        Assert.Equal("interior cell child pointer", ex.Structure);
    }

    [Fact]
    public void Cycle_RightMostPointerToRoot()
    {
        SqliteCorruptException ex = AssertCorrupt(Patch().SetRightMostPointer(DeepLeft, DeepRoot));
        Assert.Equal(DeepLeft, ex.Page);
    }

    [Fact]
    public void Cycle_ChildPointerToRoot()
    {
        SqliteCorruptException ex = AssertCorrupt(Patch().SetChildPointer(DeepRight, 0, DeepRoot));
        Assert.Equal(DeepRight, ex.Page);
    }

    [Fact]
    public void Cycle_OtherTablesStillReadable()
    {
        byte[] data = Patch().SetRightMostPointer(DeepLeft, DeepRoot).Data;

        Exception ex = RunBounded(() =>
        {
            using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data));

            Assert.Throws<SqliteCorruptException>(() => ReadTable(db, "Deep"));

            // The failure is not sticky: the same database still reads its other table, and fails the same way again
            Assert.Equal(3, ReadTable(db, "Small"));
            Assert.Throws<SqliteCorruptException>(() => ReadTable(db, "Deep"));
        });

        Assert.Null(ex);
    }

    [Fact]
    public void SharedPage_ChildVisitedTwice()
    {
        // The first leaf is also listed as a child of the right subtree, which would read its rows twice
        AssertCorrupt(Patch().SetChildPointer(DeepRight, 0, DeepFirstLeaf));
    }

    [Fact]
    public void DepthLimit_LongChainOfInteriorPages()
    {
        // Turn leaves into a chain of interior pages with only a right-most child each: no cycle, but far deeper
        // than any real b-tree. The left interior page is cut down to only the chain, so no page is reached twice.
        DbPatcher patcher = Patch().MakeEmptyInterior(DeepLeft, DeepFirstLeaf);
        for (uint page = DeepFirstLeaf; page < DeepFirstLeaf + 30; page++)
            patcher.MakeEmptyInterior(page, page + 1);

        SqliteCorruptException ex = AssertCorrupt(patcher);
        Assert.Contains("deeper", ex.Message);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(129u)]
    [InlineData(100000u)]
    [InlineData(uint.MaxValue)]
    public void ChildPointer_OutOfRange(uint child)
    {
        SqliteCorruptException ex = AssertCorrupt(Patch().SetChildPointer(DeepLeft, 0, child));
        Assert.Equal(DeepLeft, ex.Page);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(129u)]
    [InlineData(uint.MaxValue)]
    public void RightMostPointer_OutOfRange(uint child)
    {
        SqliteCorruptException ex = AssertCorrupt(Patch().SetRightMostPointer(DeepRoot, child));
        Assert.Equal(DeepRoot, ex.Page);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(129u)]
    public void RootPage_OutOfRange(uint rootPage)
    {
        // Point the "Deep" schema row at a page that does not exist: the rootpage column is a 1-byte integer
        DbPatcher patcher = Patch();
        int record = patcher.GetRecordOffset(MasterRoot, 0, out _);

        // Serial types: type, name, tbl_name, rootpage. The rootpage value follows "table", "Deep", "Deep".
        Assert.Equal(1, patcher.Data[record + 4]);
        int rootPageValue = record + patcher.Data[record] + 5 + 4 + 4;
        Assert.Equal(DeepRoot, patcher.Data[rootPageValue]);
        patcher.WriteByte(rootPageValue, (byte)rootPage);

        AssertCorrupt(patcher);
    }

    // Records

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Record_ReservedSerialType(byte serialType)
    {
        DbPatcher patcher = Patch();
        int record = patcher.GetRecordOffset(SmallRoot, 0, out _);
        patcher.WriteByte(record + 1, serialType);

        SqliteCorruptException ex = AssertCorrupt(patcher);
        Assert.Equal(SmallRoot, ex.Page);
        Assert.Equal("record header", ex.Structure);
    }

    [Fact]
    public void Record_HeaderLargerThanPayload()
    {
        DbPatcher patcher = Patch();
        int record = patcher.GetRecordOffset(SmallRoot, 0, out _);
        patcher.WriteByte(record, 0x7F);

        AssertCorrupt(patcher);
    }

    [Fact]
    public void Record_ValueLongerThanPayload()
    {
        // Row 1's name is 'one' (serial type 19). 127 claims a 57 byte text, which would read into the next cell.
        DbPatcher patcher = Patch();
        int record = patcher.GetRecordOffset(SmallRoot, 0, out _);
        Assert.Equal(19, patcher.Data[record + 2]);
        patcher.WriteByte(record + 2, 127);

        AssertCorrupt(patcher);
    }

    [Fact]
    public void Record_SchemaTypeStoredAsBlob()
    {
        // The "type" column of the first schema row holds 'table' (text, serial type 23). 22 is a 5 byte blob.
        DbPatcher patcher = Patch();
        int record = patcher.GetRecordOffset(MasterRoot, 0, out _);
        Assert.Equal(23, patcher.Data[record + 1]);
        patcher.WriteByte(record + 1, 22);

        AssertCorrupt(patcher);
    }

    // Overflow chains

    [Fact]
    public void Overflow_ChainEndsEarly()
    {
        // Without its overflow pages the blob would come back zero-filled
        AssertCorrupt(Patch().SetOverflowPointer(SmallRoot, 1, 0));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(129u)]
    [InlineData(uint.MaxValue)]
    public void Overflow_PointerOutOfRange(uint page)
    {
        AssertCorrupt(Patch().SetOverflowPointer(SmallRoot, 1, page));
    }

    [Fact]
    public void Overflow_ChainCycle_Terminates()
    {
        // The second overflow page points back to the first. The chain length is bounded by the payload size, so
        // this must terminate. Detecting the cycle itself is a follow-up.
        DbPatcher patcher = Patch();
        uint first = patcher.GetOverflowPointer(SmallRoot, 1);
        uint second = (uint)((patcher.Data[patcher.PageOffset(first)] << 24) | (patcher.Data[patcher.PageOffset(first) + 1] << 16) |
                             (patcher.Data[patcher.PageOffset(first) + 2] << 8) | patcher.Data[patcher.PageOffset(first) + 3]);
        patcher.WriteUInt32(patcher.PageOffset(second), first);

        AssertTerminatesGracefully(() => ReadEverything(patcher.Data));
    }
}
