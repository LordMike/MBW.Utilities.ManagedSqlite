using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class PageSize_Tests
{
    private static Stream OpenDb(uint pageSize)
    {
        return ResourceHelper.OpenResource($"MBW.Utilities.ManagedSqlite.Core.Tests.Data.PageSize{pageSize}.db");
    }

    /// <summary>
    /// Builds the blob the fixture scripts insert: "000001000002000003..." cut to <paramref name="length"/> bytes
    /// </summary>
    private static byte[] ExpectedBlob(int length)
    {
        StringBuilder sb = new StringBuilder(length + 6);
        for (int i = 1; sb.Length < length; i++)
            sb.Append(i.ToString("D6"));

        return Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    [Theory]
    [InlineData(512u)]
    [InlineData(65536u)]
    public void TestPageSizeHeader(uint pageSize)
    {
        using Stream stream = OpenDb(pageSize);
        using Sqlite3Database db = new Sqlite3Database(stream);

        Assert.Equal(pageSize, db.Header.PageSize);
    }

    [Theory]
    [InlineData(512u)]
    [InlineData(65536u)]
    public void TestSmallTable(uint pageSize)
    {
        using Stream stream = OpenDb(pageSize);
        using Sqlite3Database db = new Sqlite3Database(stream);

        Sqlite3Table tbl = db.GetTable("SmallTable");
        List<Sqlite3Row> rows = tbl.EnumerateRows().OrderBy(s => s.RowId).ToList();

        Assert.Equal(new long[] { 1, 2, 3 }, rows.Select(s => s.RowId).ToArray());

        string[] names = rows.Select(s => Assert.IsType<string>(s.ColumnData[1])).ToArray();
        Assert.Equal(new[] { "One", "Two", "Three" }, names);
    }

    [Theory]
    [InlineData(512u, 1, 60000)]
    [InlineData(512u, 2, 200000)]
    [InlineData(65536u, 1, 60000)]
    [InlineData(65536u, 2, 200000)]
    public void TestBlob(uint pageSize, long rowId, int expectedLength)
    {
        using Stream stream = OpenDb(pageSize);
        using Sqlite3Database db = new Sqlite3Database(stream);

        Sqlite3Table tbl = db.GetTable("BlobTable");
        Sqlite3Row row = tbl.GetRowById(rowId);

        Assert.NotNull(row);
        Assert.True(row.TryGetOrdinal(1, out byte[] actual));

        Assert.Equal(ExpectedBlob(expectedLength), actual);
    }
}
