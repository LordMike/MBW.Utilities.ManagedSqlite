using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

/// <summary>
/// How the Core API presents rows, which the parity dump does not see: it reads rows by name through the Sql package
/// </summary>
public class Rows_Tests
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

    /// <summary>
    /// A WITHOUT ROWID row has no rowid, so RowId reads 0, and ColumnData is the record as stored, primary key columns
    /// first
    /// </summary>
    [Fact]
    public void WithoutRowIdRowsHaveNoRowIdAndRecordOrderColumns()
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE tbl (v TEXT, k TEXT PRIMARY KEY) WITHOUT ROWID;
INSERT INTO tbl VALUES ('second', 'b'), ('first', 'a');")));

        Sqlite3Row[] rows = db.GetTable("tbl").EnumerateRows().ToArray();

        Assert.All(rows, row => Assert.Equal(0, row.RowId));
        Assert.Equal(new[] { new object[] { "a", "first" }, new object[] { "b", "second" } }, rows.Select(row => row.ColumnData));
    }

    /// <summary>
    /// Two enumerations of one database share its reader and its page buffer, so reading a row of one table between
    /// two rows of another must not change what either returns. 512 byte pages put the 300 rows of each table on many
    /// leaf pages.
    /// </summary>
    [Fact]
    public void InterleavedEnumerationsReadTheSameRows()
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(SqliteParity.Build(@"
PRAGMA page_size = 512;
CREATE TABLE a (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE b (Id INTEGER PRIMARY KEY, Value TEXT);
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 300)
INSERT INTO a SELECT x, 'a' || x FROM seq;
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 300)
INSERT INTO b SELECT x, 'b' || x FROM seq;")));

        List<string> interleavedA = new List<string>();
        List<string> interleavedB = new List<string>();
        using (IEnumerator<Sqlite3Row> a = db.GetTable("a").EnumerateRows().GetEnumerator())
        using (IEnumerator<Sqlite3Row> b = db.GetTable("b").EnumerateRows().GetEnumerator())
        {
            while (a.MoveNext() && b.MoveNext())
            {
                interleavedA.Add((string)a.Current.ColumnData[1]);
                interleavedB.Add((string)b.Current.ColumnData[1]);
            }
        }

        Assert.Equal(Enumerable.Range(1, 300).Select(x => "a" + x), interleavedA);
        Assert.Equal(Enumerable.Range(1, 300).Select(x => "b" + x), interleavedB);
    }
}
