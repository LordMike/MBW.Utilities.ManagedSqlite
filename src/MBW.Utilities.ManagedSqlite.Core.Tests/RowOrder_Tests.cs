using System;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

/// <summary>
/// Tables in RowOrder.db use a 512 byte page size, so their b-trees have interior pages (depth 3).
/// EnumerateRows() must yield rows in rowid order, like SQLite does for a full table scan.
/// </summary>
public class RowOrder_Tests : IDisposable
{
    private readonly Stream _stream;

    public RowOrder_Tests()
    {
        _stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.RowOrder.db");
    }

    [Theory]
    [InlineData("OrderedTable", 5000)]
    [InlineData("ShuffledTable", 2000)]
    public void EnumerateRowsYieldsAscendingRowIds(string table, int expectedCount)
    {
        using Sqlite3Database db = new Sqlite3Database(_stream);

        Sqlite3Table tbl = db.GetTable(table);
        long[] rowIds = tbl.EnumerateRows().Select(s => s.RowId).ToArray();

        long[] expected = Enumerable.Range(1, expectedCount).Select(s => (long)s).ToArray();
        Assert.Equal(expected, rowIds);
    }

    [Theory]
    [InlineData("OrderedTable")]
    [InlineData("ShuffledTable")]
    public void EnumerateRowsMatchesRowContent(string table)
    {
        using Sqlite3Database db = new Sqlite3Database(_stream);

        Sqlite3Table tbl = db.GetTable(table);

        foreach (Sqlite3Row row in tbl.EnumerateRows())
        {
            Assert.True(row.TryGetOrdinal(1, out string value));
            Assert.Equal("row " + row.RowId, value);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }
}
