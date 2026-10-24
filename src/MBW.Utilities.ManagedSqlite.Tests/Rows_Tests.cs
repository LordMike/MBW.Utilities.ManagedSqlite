using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

/// <summary>
/// How the Core API presents rows, which the parity dump does not see: it reads rows by name through the table definition
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
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE tbl (Id INTEGER PRIMARY KEY, MyInt INTEGER);
INSERT INTO tbl VALUES (1, 500);
ALTER TABLE tbl ADD COLUMN NewColumn INTEGER DEFAULT 2000;")));

        Sqlite3Row row = db.GetTable("tbl").EnumerateRows().Single();

        Assert.True(row.TryGetOrdinal(1, out long myInt));
        Assert.Equal(500, myInt);
        Assert.False(row.TryGetOrdinal(2, out long _));
    }

    /// <summary>
    /// A WITHOUT ROWID row has no rowid (HasRowId is false, which the parity dump checks), so RowId reads 0, and
    /// ColumnData is the record as stored, primary key columns first
    /// </summary>
    [Fact]
    public void WithoutRowIdRowsHaveNoRowIdAndRecordOrderColumns()
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE tbl (v TEXT, k TEXT PRIMARY KEY) WITHOUT ROWID;
INSERT INTO tbl VALUES ('second', 'b'), ('first', 'a');")));

        Sqlite3Row[] rows = db.GetTable("tbl").EnumerateRows().ToArray();

        Assert.All(rows, row => Assert.Equal(0, row.RowId));
        Assert.Equal(new[] { new object[] { "a", "first" }, new object[] { "b", "second" } }, rows.Select(row => row.ColumnData));
    }

    /// <summary>
    /// Once ColumnData is read, the row lets go of its page and reads every field from those values: TryGetOrdinal and
    /// TryGetBlob return the same values as before, and a blob as the same bytes
    /// </summary>
    [Fact]
    public void FieldsReadTheSameAfterColumnData()
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE tbl (i INTEGER, t TEXT, b BLOB);
INSERT INTO tbl VALUES (7, 'seven', X'0102FF');")));

        Sqlite3Row row = db.GetTable("tbl").EnumerateRows().Single();
        object[] before = Enumerable.Range(0, 4).Select(i => row.TryGetOrdinal((ushort)i, out object value) ? value : "missing").ToArray();

        Assert.Equal(new object[] { 7L, "seven", new byte[] { 1, 2, 255 } }, row.ColumnData);

        Assert.Equal(before, Enumerable.Range(0, 4).Select(i => row.TryGetOrdinal((ushort)i, out object value) ? value : "missing"));
        Assert.True(row.TryGetBlob(2, out ReadOnlyMemory<byte> blob));
        Assert.Equal(new byte[] { 1, 2, 255 }, blob.ToArray());
        Assert.False(row.TryGetBlob(1, out _));
        Assert.False(row.TryGetBlob(3, out _));
    }

    /// <summary>
    /// Read through its <see cref="SqlTableColumn"/>, each stored column has SQLite's value, though its
    /// <see cref="SqlTableColumn.Ordinal"/> is not its place in the record: a WITHOUT ROWID table's record starts with
    /// its key, and a VIRTUAL generated column, which has no value, takes no place in it
    /// </summary>
    [Theory]
    [InlineData("a, b, c, PRIMARY KEY (c)", "WITHOUT ROWID")]
    [InlineData("a, b AS (a || 'b') VIRTUAL, c", "")]
    public void ColumnsReadAsSqlite(string columns, string options)
    {
        byte[] data = SqliteParity.Build($"CREATE TABLE tbl ({columns}) {options}; INSERT INTO tbl (a, c) VALUES ('a', 'c');");
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(data));
        Sqlite3Table table = db.GetTable("tbl");
        Sqlite3Row row = table.EnumerateRows().Single();

        foreach (SqlTableColumn column in table.GetTableDefinition().Columns)
        {
            object expected = column.IsVirtualGenerated ? "no value" : SqliteParity.ReadFirstValueWithSqlite<object>(data, $"SELECT {column.Name} FROM tbl");
            Assert.Equal(expected, row.TryGetOrdinal(column, out object value) ? value : "no value");
        }
    }

    /// <summary>
    /// A column of another table's definition is the caller's error, even with the same name and position
    /// </summary>
    [Fact]
    public void ColumnOfAnotherTableThrows()
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE tbl (a);
CREATE TABLE other (a);
INSERT INTO tbl VALUES (1);")));

        Sqlite3Row row = db.GetTable("tbl").EnumerateRows().Single();
        SqlTableColumn column = db.GetTable("other").GetTableDefinition().Columns.Single();

        Assert.Throws<ArgumentException>(() => row.TryGetOrdinal(column, out object _));
    }

    /// <summary>
    /// The generic accessors convert as SQLite's typed reads do: NULL is the type's default (null for int?), a REAL read
    /// as an integer is truncated toward zero, and an INTEGER reads as int? or double. Where SQLite would wrap or
    /// saturate a number, or parse text, they return false.
    /// </summary>
    [Theory]
    [InlineData("NULL", typeof(int?), true)]
    [InlineData("NULL", typeof(int), true)]
    [InlineData("7", typeof(int?), true)]
    [InlineData("7", typeof(double), true)]
    [InlineData("3.4028235e38", typeof(float), true)]
    [InlineData("3.5", typeof(long), true)]
    [InlineData("-3.5", typeof(long), true)]
    [InlineData("200", typeof(byte), true)]
    [InlineData("2", typeof(bool), true)]
    [InlineData("0.5", typeof(bool), true)]
    [InlineData("5000000000", typeof(int), false)]
    [InlineData("1e999", typeof(long), false)]
    [InlineData("'12'", typeof(long), false)]
    [InlineData("x'3132'", typeof(string), false)]
    public void TypedValuesConvertAsSqlite(string literal, Type type, bool converts)
    {
        typeof(Rows_Tests).GetMethod(nameof(AssertTypedValue), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).Invoke(null, new object[] { literal, converts });
    }

    private static void AssertTypedValue<T>(string literal, bool converts)
    {
        byte[] data = SqliteParity.Build($"CREATE TABLE tbl (v); INSERT INTO tbl VALUES ({literal});");
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(data));
        Sqlite3Row row = db.GetTable("tbl").EnumerateRows().Single();

        Assert.Equal(converts, row.TryGetValueByName("v", out T value));
        Assert.Equal(converts ? SqliteParity.ReadFirstValueWithSqlite<T>(data, "SELECT v FROM tbl") : default, value);
    }

    /// <summary>
    /// Two enumerations of one database share its reader and its page buffer, so reading a row of one table between
    /// two rows of another must not change what either returns. 512 byte pages put the 300 rows of each table on many
    /// leaf pages.
    /// </summary>
    [Fact]
    public void InterleavedEnumerationsReadTheSameRows()
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
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

    /// <summary>
    /// Once <see cref="Sqlite3Row.ColumnData"/> is read, the typed reads serve its values, with the same results as
    /// before: each reads only its own type, and TryGetDouble an INTEGER too
    /// </summary>
    [Fact]
    public void TypedReadsServeColumnDataOnceRead()
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE tbl (a, b, c, d, e);
INSERT INTO tbl VALUES (42, 1.5, 'text', x'0102', NULL);")));

        Sqlite3Row row = db.GetTable("tbl").EnumerateRows().Single();
        Assert.Equal(new object[] { 42L, 1.5, "text", new byte[] { 1, 2 }, null }, row.ColumnData);

        Assert.True(row.TryGetInt64(0, out long integer));
        Assert.Equal(42, integer);
        Assert.True(row.TryGetDouble(0, out double widened));
        Assert.Equal(42.0, widened);
        Assert.True(row.TryGetDouble(1, out double real));
        Assert.Equal(1.5, real);
        Assert.True(row.TryGetString(2, out string text));
        Assert.Equal("text", text);

        Assert.False(row.TryGetInt64(1, out _));
        Assert.False(row.TryGetString(3, out _));
        Assert.False(row.TryGetDouble(4, out _));
        Assert.False(row.TryGetInt64(5, out _));
    }

    /// <summary>
    /// With shared row buffers, a row is readable only while it is the latest: an earlier row throws rather than read
    /// fields a later row wrote. A row that read ColumnData in the loop keeps its values, and the last row stays
    /// readable after the loop.
    /// </summary>
    [Fact]
    public void SharedRowBuffersFailReadsOfEarlierRows()
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE tbl (a, b);
INSERT INTO tbl VALUES (1, 'one'), (2, 'two'), (3, 'three');")));

        List<Sqlite3Row> rows = new List<Sqlite3Row>();
        foreach (Sqlite3Row row in db.GetTable("tbl").EnumerateRows(shareRowBuffers: true))
        {
            Assert.True(row.TryGetInt64(0, out _));
            if (rows.Count == 1)
                _ = row.ColumnData;
            rows.Add(row);
        }

        Assert.Throws<InvalidOperationException>(() => rows[0].TryGetInt64(0, out _));
        Assert.Throws<InvalidOperationException>(() => rows[0].TryGetOrdinal(1, out _));
        Assert.Throws<InvalidOperationException>(() => rows[0].ColumnData);
        Assert.Equal(new object[] { 2L, "two" }, rows[1].ColumnData);
        Assert.True(rows[2].TryGetString(1, out string last));
        Assert.Equal("three", last);
    }

    /// <summary>
    /// Each enumeration of one <c>EnumerateRows(shareRowBuffers: true)</c> result has its own field table, so a nested
    /// enumeration of it leaves the outer row readable.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("WITHOUT ROWID")]
    public void SharedRowBuffersArePerEnumeration(string options)
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build($@"
CREATE TABLE tbl (a PRIMARY KEY, b) {options};
INSERT INTO tbl VALUES (1, 'one'), (2, 'two');")));

        IEnumerable<Sqlite3Row> rows = db.GetTable("tbl").EnumerateRows(shareRowBuffers: true);
        foreach (Sqlite3Row outer in rows)
        {
            Assert.True(outer.TryGetInt64(0, out long key));
            Assert.Equal(2, rows.Count(inner => inner.TryGetInt64(0, out _)));

            Assert.True(outer.TryGetInt64(0, out long again));
            Assert.Equal(key, again);
        }
    }

    /// <summary>
    /// A row as wide as SQLite allows (SQLITE_MAX_COLUMN, 2000 columns), with a long TEXT last so its serial type takes
    /// two header bytes. Every column reads, with and without shared row buffers. The parity dump, which adds a typeof
    /// per column, can't read a table this wide from SQLite, so the values are those the script wrote.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WidestRowReads(bool shareRowBuffers)
    {
        string longText = new string('v', 100);
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(
            $"CREATE TABLE t ({string.Join(", ", Enumerable.Range(0, 2000).Select(i => $"c{i}"))});" +
            $"INSERT INTO t VALUES ({string.Join(", ", Enumerable.Range(0, 1999))}, '{longText}');" +
            $"INSERT INTO t VALUES ({string.Join(", ", Enumerable.Range(0, 1999).Select(i => ~i))}, '{longText}');")));

        int rows = 0;
        foreach (Sqlite3Row row in db.GetTable("t").EnumerateRows(shareRowBuffers))
        {
            bool first = rows++ == 0;

            // In order, so the header is parsed one field per read, and completed by the last
            for (ushort i = 0; i < 1999; i++)
            {
                Assert.True(row.TryGetInt64(i, out long value));
                Assert.Equal(first ? i : ~i, value);
            }

            Assert.True(row.TryGetString(1999, out string text));
            Assert.Equal(longText, text);

            Assert.False(row.TryGetOrdinal(2000, out _));
        }

        Assert.Equal(2, rows);
    }

    /// <summary>
    /// A row reads its overflow pages only when a value on them is read, through the reader's shared page buffer, so a
    /// row kept after the enumeration moved on still reads its own values, after other rows read theirs. The text ends,
    /// and the number after it is, on the last overflow page, which ColumnData reads for both.
    /// </summary>
    [Fact]
    public void KeptRowsReadTheirOwnOverflowPages()
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
PRAGMA page_size = 512;
CREATE TABLE tbl (Id INTEGER PRIMARY KEY, Value TEXT, Number INTEGER);
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 20)
INSERT INTO tbl SELECT x, printf('%.*c%d', 1500, 'v', x), 1000 + x FROM seq;")));

        List<Sqlite3Row> rows = db.GetTable("tbl").EnumerateRows().ToList();
        rows.Reverse();

        Assert.Equal(Enumerable.Range(1, 20).Reverse().Select(x => new object[] { null, new string('v', 1500) + x, 1000L + x }),
            rows.Select(row => row.ColumnData));
    }

    /// <summary>
    /// The typed reads read values on overflow pages too: a TEXT that runs onto them, and the INTEGER and REAL after it
    /// on the last one, with and without shared row buffers
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedReadsReadOverflowPages(bool shareRowBuffers)
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
PRAGMA page_size = 512;
CREATE TABLE tbl (Id INTEGER PRIMARY KEY, Value TEXT, Number INTEGER, Real REAL);
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 5)
INSERT INTO tbl SELECT x, printf('%.*c%d', 1500, 'v', x), 1000 + x, x + 0.5 FROM seq;")));

        int x = 0;
        foreach (Sqlite3Row row in db.GetTable("tbl").EnumerateRows(shareRowBuffers))
        {
            x++;
            Assert.True(row.TryGetString(1, out string text));
            Assert.Equal(new string('v', 1500) + x, text);
            Assert.True(row.TryGetInt64(2, out long number));
            Assert.Equal(1000 + x, number);
            Assert.True(row.TryGetDouble(3, out double real));
            Assert.Equal(x + 0.5, real);
        }

        Assert.Equal(5, x);
    }
}
