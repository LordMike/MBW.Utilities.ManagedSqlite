using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class EnumerateRows_Tests
{
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
