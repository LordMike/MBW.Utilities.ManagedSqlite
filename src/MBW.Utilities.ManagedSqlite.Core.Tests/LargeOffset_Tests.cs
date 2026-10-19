using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

/// <summary>
/// Reads a table whose pages lie past the 4 GiB mark, without a 4 GiB file.
/// LargeOffset.db has the table's root (leaf) page at page 2 and its overflow page at page 3. The test moves
/// both into a virtual file just over 4 GiB long and patches the page numbers that point at them.
/// </summary>
public class LargeOffset_Tests
{
    private const int PageSize = 4096;
    private const string TableName = "LargeOffsetTable";
    private const string StoredSql = "CREATE TABLE LargeOffsetTable (Id INTEGER PRIMARY KEY, Name TEXT   )";

    // (FarPage - 1) * 4096 = 4,294,971,392, just past 4 GiB. Computed in 32 bits it wraps to 4096 (page 2)
    private const uint FarPage = 1_048_578;

    [Fact]
    public void TestTableBeyond4GiB()
    {
        using Sqlite3Database db = new Sqlite3Database(CreateLargeStream());

        Sqlite3SchemaRow schema = db.GetTables().Single(s => s.TableName == TableName);
        Assert.Equal(FarPage, schema.RootPage);

        Sqlite3Table tbl = db.GetTable(TableName);
        List<Sqlite3Row> rows = tbl.EnumerateRows().OrderBy(s => s.RowId).ToList();

        Assert.Equal(new long[] { 1, 2, 3 }, rows.Select(s => s.RowId));

        Assert.True(rows[0].TryGetOrdinal(1, out string one));
        Assert.Equal("one", one);

        Assert.True(rows[1].TryGetOrdinal(1, out string two));
        Assert.Equal("two", two);

        // Partly stored on the overflow page at FarPage + 1
        Assert.True(rows[2].TryGetOrdinal(1, out string large));
        Assert.Equal(new string('x', 5000), large);
    }

    private static Stream CreateLargeStream()
    {
        byte[] file;
        using (Stream fs = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.LargeOffset.db"))
        using (MemoryStream ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            file = ms.ToArray();
        }

        Assert.Equal(3 * PageSize, file.Length);

        byte[] page1 = file.AsSpan(0, PageSize).ToArray();
        byte[] rootPage = file.AsSpan(PageSize, PageSize).ToArray();
        byte[] overflowPage = file.AsSpan(2 * PageSize, PageSize).ToArray();

        // sqlite_master: rootpage 2 (serial type 1, a 1-byte integer) becomes FarPage (serial type 4, a 4-byte
        // integer). Three trailing spaces are taken out of the stored SQL so the record keeps its length.
        ReplaceOnce(page1,
            MasterRecord(1, new byte[] { 2 }, StoredSql),
            MasterRecord(4, BigEndian(FarPage), StoredSql.Remove(StoredSql.Length - 4, 3)));

        // The last cell's overflow pointer comes right after its in-page 'x' characters
        ReplaceOnce(rootPage,
            Concat(Encoding.ASCII.GetBytes("xxxx"), BigEndian(3)),
            Concat(Encoding.ASCII.GetBytes("xxxx"), BigEndian(FarPage + 1)));

        // Pages 2 and 3 stay unmapped (zeros), so a read that wraps around to them cannot succeed by accident
        Dictionary<long, byte[]> pages = new Dictionary<long, byte[]>
        {
            { 1, page1 },
            { FarPage, rootPage },
            { FarPage + 1, overflowPage }
        };

        long length = (FarPage + 1L) * PageSize;
        Assert.True(length > 4L * 1024 * 1024 * 1024);

        return new PageMappedStream(PageSize, length, pages);
    }

    /// <summary>
    /// The sqlite_master record (type, name, tbl_name, rootpage, sql) for <see cref="TableName"/>
    /// </summary>
    private static byte[] MasterRecord(byte rootPageSerialType, byte[] rootPage, string sql)
    {
        byte[] type = Encoding.ASCII.GetBytes("table");
        byte[] name = Encoding.ASCII.GetBytes(TableName);
        byte[] sqlBytes = Encoding.ASCII.GetBytes(sql);

        // Text serial types are 13 + 2 * length. Every value here fits a 1-byte varint, except the SQL which takes 2
        int sqlSerialType = 13 + 2 * sqlBytes.Length;
        Assert.InRange(sqlSerialType, 128, 16383);

        byte[] header =
        {
            7, // header size, including this byte
            (byte)(13 + 2 * type.Length),
            (byte)(13 + 2 * name.Length),
            (byte)(13 + 2 * name.Length),
            rootPageSerialType,
            (byte)(0x80 | (sqlSerialType >> 7)),
            (byte)(sqlSerialType & 0x7F)
        };

        return Concat(header, type, name, name, rootPage, sqlBytes);
    }

    private static void ReplaceOnce(byte[] data, byte[] find, byte[] replace)
    {
        Assert.Equal(find.Length, replace.Length);

        int index = data.AsSpan().IndexOf(find);
        Assert.True(index >= 0, "Pattern to patch was not found");
        Assert.True(data.AsSpan(index + 1).IndexOf(find) < 0, "Pattern to patch was found more than once");

        replace.CopyTo(data, index);
    }

    private static byte[] BigEndian(uint value) => new[]
    {
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value
    };

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(s => s).ToArray();
}
