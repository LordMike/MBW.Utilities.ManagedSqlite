using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

/// <summary>
/// Reads a table whose pages lie past the 4 GiB mark.
/// LargeOffset.sparse is a 4.5 GB database written by SQLite (see LargeOffset_Create.py), stored as just its
/// non-zero pages: page 1, and LargeOffsetTable's root page and overflow page past 4 GiB.
/// </summary>
public class LargeOffset_Tests
{
    private const string TableName = "LargeOffsetTable";

    [Fact]
    public void TestTableBeyond4GiB()
    {
        using Stream stream = LoadSparse("MBW.Utilities.ManagedSqlite.Tests.Data.LargeOffset.sparse", out int pageSize);
        using Sqlite3Database db = new Sqlite3Database(stream);

        // Computed in 32 bits, this offset wraps around to a page that is all zeros
        Sqlite3SchemaRow schema = db.GetTables().Single(s => s.TableName == TableName);
        Assert.True((schema.RootPage - 1L) * pageSize > uint.MaxValue);

        Sqlite3Table tbl = db.GetTable(TableName);
        List<Sqlite3Row> rows = tbl.EnumerateRows().OrderBy(s => s.RowId).ToList();

        Assert.Equal(new long[] { 1, 2, 3 }, rows.Select(s => s.RowId));

        Assert.True(rows[0].TryGetOrdinal(1, out string one));
        Assert.Equal("one", one);

        Assert.True(rows[1].TryGetOrdinal(1, out string two));
        Assert.Equal("two", two);

        // Partly stored on the overflow page after the root page
        Assert.True(rows[2].TryGetOrdinal(1, out string large));
        Assert.Equal(new string('x', 5000), large);
    }

    /// <summary>
    /// Reads a sparse database: for each non-zero page, a 4-byte big-endian page number followed by the page.
    /// Page size and page count are taken from the database header in page 1.
    /// </summary>
    private static Stream LoadSparse(string resource, out int pageSize)
    {
        byte[] data;
        using (Stream fs = ResourceHelper.OpenResource(resource))
        using (MemoryStream ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            data = ms.ToArray();
        }

        Assert.Equal(1u, ReadUInt32(data, 0));

        // Header offset 16: page size, offset 28: page count
        pageSize = (data[4 + 16] << 8) | data[4 + 17];
        long pageCount = ReadUInt32(data, 4 + 28);

        Dictionary<long, byte[]> pages = new Dictionary<long, byte[]>();
        for (int offset = 0; offset < data.Length; offset += 4 + pageSize)
            pages.Add(ReadUInt32(data, offset), data.AsSpan(offset + 4, pageSize).ToArray());

        return new PageMappedStream(pageSize, pageCount * pageSize, pages);
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
