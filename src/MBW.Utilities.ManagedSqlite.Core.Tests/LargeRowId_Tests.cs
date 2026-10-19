using System;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

/// <summary>
/// Rowids that are negative or >= 2^56 are stored as 9-byte varints in the cell header
/// </summary>
public class LargeRowId_Tests : IDisposable
{
    private static readonly (long RowId, string Name, long MyInt)[] Expected =
    {
        (long.MinValue, "long min", long.MinValue),
        (-1L, "minus one", -1L),
        (1L, "one", 100L),
        (2L, "two", 200L),
        (127L, "max 1 byte", 127L),
        (128L, "min 2 bytes", 128L),
        ((1L << 56) - 1, "max 8 bytes", (1L << 56) - 1),
        (1L << 56, "min 9 bytes", 1L << 56),
        (long.MaxValue, "long max", long.MaxValue)
    };

    private readonly Stream _stream;

    public LargeRowId_Tests()
    {
        _stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.LargeRowId.db");
    }

    [Fact]
    public void ReadsAllRowsWithLargeRowIds()
    {
        using Sqlite3Database db = new Sqlite3Database(_stream);

        Sqlite3Table tbl = db.GetTable("LargeRowIdTable");

        // Row order is not asserted here, only that every row decodes correctly
        Sqlite3Row[] rows = tbl.EnumerateRows().OrderBy(s => s.RowId).ToArray();

        Assert.Equal(Expected.Length, rows.Length);

        for (int i = 0; i < Expected.Length; i++)
        {
            Sqlite3Row row = rows[i];

            Assert.Equal(Expected[i].RowId, row.RowId);

            Assert.True(row.TryGetOrdinal(0, out string name));
            Assert.Equal(Expected[i].Name, name);

            Assert.True(row.TryGetOrdinal(1, out long myInt));
            Assert.Equal(Expected[i].MyInt, myInt);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }
}
