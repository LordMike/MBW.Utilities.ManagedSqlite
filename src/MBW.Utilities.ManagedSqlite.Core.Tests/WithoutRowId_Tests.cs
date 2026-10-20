using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class WithoutRowId_Tests
{
    /// <summary>
    /// The Core API's presentation of a WITHOUT ROWID row, which the parity dump does not see: RowId reads 0, and
    /// ColumnData is the record as stored, primary key columns first.
    /// </summary>
    [Fact]
    public void RowsHaveNoRowIdAndRecordOrderColumns()
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE tbl (v TEXT, k TEXT PRIMARY KEY) WITHOUT ROWID;
INSERT INTO tbl VALUES ('second', 'b'), ('first', 'a');")));

        Sqlite3Row[] rows = db.GetTable("tbl").EnumerateRows().ToArray();

        Assert.All(rows, row => Assert.Equal(0, row.RowId));
        Assert.Equal(new[] { new object[] { "a", "first" }, new object[] { "b", "second" } }, rows.Select(row => row.ColumnData));
    }
}
