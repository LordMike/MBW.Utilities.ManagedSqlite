using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class AlterTable_Tests
{
    /// <summary>
    /// ALTER TABLE ADD COLUMN does not rewrite existing rows, so a row written before it has a shorter record, and the
    /// Core API has no value at the added column's ordinal. (SQLite reads the column's DEFAULT there, which the Core
    /// API does not know.)
    /// </summary>
    [Fact]
    public void RowWrittenBeforeAddColumnHasNoValueThere()
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE tbl (Id INTEGER PRIMARY KEY, MyInt INTEGER);
INSERT INTO tbl VALUES (1, 500);
ALTER TABLE tbl ADD COLUMN NewColumn INTEGER DEFAULT 2000;")));

        Sqlite3Row row = db.GetTable("tbl").EnumerateRows().Single();

        Assert.True(row.TryGetOrdinal(1, out long myInt));
        Assert.Equal(500, myInt);
        Assert.False(row.TryGetOrdinal(2, out long _));
    }
}
