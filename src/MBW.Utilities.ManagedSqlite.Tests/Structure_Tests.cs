using System.Diagnostics.CodeAnalysis;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

/// <summary>
/// Each case builds a database with real SQLite and compares SQLite's dump of it with the library's.
/// </summary>
[SuppressMessage("Usage", "xUnit1026", Justification = "A theory's name parameter only labels its case in the test runner")]
public class Structure_Tests
{
    private static string PageSizeScript(int pageSize)
    {
        // The blobs are the numbers 1, 2, 3, ... zero-padded to 6 characters, concatenated and cut to length
        return $@"
PRAGMA page_size = {pageSize};
CREATE TABLE SmallTable (Id INTEGER PRIMARY KEY, Name TEXT);
INSERT INTO SmallTable VALUES (1, 'One'), (2, 'Two'), (3, 'Three');
CREATE TABLE BlobTable (Id INTEGER PRIMARY KEY, Data BLOB);
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40000),
  s(v) AS (SELECT group_concat(printf('%06d', i), '') FROM (SELECT i FROM n ORDER BY i))
INSERT INTO BlobTable SELECT 1, CAST(substr(v, 1, 60000) AS BLOB) FROM s UNION ALL SELECT 2, CAST(substr(v, 1, 200000) AS BLOB) FROM s;";
    }

    [Theory]
    [InlineData(512)]
    [InlineData(65536)]
    public void PageSize(int pageSize) => SqliteParity.AssertSameAsSqlite(PageSizeScript(pageSize));

    /// <summary>
    /// Bytes reserved at the end of each page shrink the usable size that the cell and overflow layout is computed
    /// from. 32 of 512 leaves 480, the smallest usable size SQLite allows.
    /// https://www.sqlite.org/fileformat.html#reserved_bytes_per_page
    /// </summary>
    [Fact]
    public void ReservedBytesPerPage() => SqliteParity.AssertSameAsSqlite(32, PageSizeScript(512));

    [Theory]
    // 7919 is coprime to 2000, so every id appears once; then a range is rewritten in reverse so cells are out of key order
    [InlineData("shuffled insert 2000 rows", @"
PRAGMA page_size = 512;
CREATE TABLE t (Id INTEGER PRIMARY KEY, Value TEXT);
WITH RECURSIVE seq(x) AS (SELECT 0 UNION ALL SELECT x + 1 FROM seq WHERE x < 1999)
INSERT INTO t SELECT ((x * 7919) % 2000) + 1, 'row ' || (((x * 7919) % 2000) + 1) FROM seq;
DELETE FROM t WHERE Id BETWEEN 500 AND 1500;
WITH RECURSIVE seq(x) AS (SELECT 1500 UNION ALL SELECT x - 1 FROM seq WHERE x > 500)
INSERT INTO t SELECT x, 'row ' || x FROM seq;")]
    // Negative rowids and rowids >= 2^56 take a 9-byte varint in the cell header
    [InlineData("negative and very large rowids", @"
CREATE TABLE t (Value TEXT);
INSERT INTO t (rowid, Value) VALUES (-9223372036854775808, 'min'), (-5, 'negative'), (-1, 'minus one'), (0, 'zero'),
    (127, 'max 1 byte'), (128, 'min 2 bytes'), (72057594037927935, 'max 8 bytes'), (72057594037927936, 'min 9 bytes'),
    (9223372036854775807, 'max');")]
    // The overflow chain of the middle row is read between two rows whose payload is on the leaf page
    [InlineData("local row, overflowing row, local row on one leaf", @"
CREATE TABLE t (v);
INSERT INTO t VALUES (1), (randomblob(5000)), (2);")]
    // An index b-tree, keys inserted out of order, with enough rows for interior pages, whose cells hold rows too
    [InlineData("WITHOUT ROWID 3000 rows, 3 levels", @"
PRAGMA page_size = 512;
CREATE TABLE t (k TEXT PRIMARY KEY, v INTEGER) WITHOUT ROWID;
WITH RECURSIVE seq(x) AS (SELECT 0 UNION ALL SELECT x + 1 FROM seq WHERE x < 2999)
INSERT INTO t SELECT printf('key %05d', (x * 7919) % 3000), x FROM seq;")]
    // Index pages keep less of a payload locally than table pages, so these keys overflow in leaf and interior cells
    [InlineData("WITHOUT ROWID overflow pages", @"
PRAGMA page_size = 512;
CREATE TABLE t (k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID;
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 200)
INSERT INTO t SELECT printf('%04d', x) || printf('%.*c', x * 3, 'k'), CASE WHEN x = 100 THEN zeroblob(20000) ELSE x END FROM seq;")]
    // Pointer map pages sit between the b-tree pages, and are not part of any b-tree
    [InlineData("auto_vacuum with pointer map pages", @"
PRAGMA page_size = 512;
PRAGMA auto_vacuum = FULL;
CREATE TABLE a (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE b (Id INTEGER PRIMARY KEY, Value BLOB);
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 300)
INSERT INTO a SELECT x, 'a' || x FROM seq;
INSERT INTO b VALUES (1, zeroblob(3000));
DELETE FROM a WHERE Id % 3 = 0;")]
    [InlineData("deleted rows leave freeblocks and free pages", @"
PRAGMA page_size = 512;
CREATE TABLE t (Id INTEGER PRIMARY KEY, Value TEXT);
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 2000)
INSERT INTO t SELECT x, 'row ' || x FROM seq;
DELETE FROM t WHERE Id % 2 = 0;")]
    public void BTreeShape(string name, string script) => SqliteParity.AssertSameAsSqlite(script);

    [Theory]
    // sqlite_master no longer fits on page 1, so it becomes an interior page with a cell and a right-most pointer, each
    // to a leaf
    [InlineData("sqlite_master over several pages", @"
PRAGMA page_size = 512;
CREATE TABLE t1 (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE t2 (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE t3 (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE t4 (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE t5 (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE t6 (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE t7 (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE t8 (Id INTEGER PRIMARY KEY, Value TEXT);
INSERT INTO t8 VALUES (1, 'v8');")]
    // Rows written before ADD COLUMN have no value for the new column in their record, and read as NULL
    [InlineData("ALTER TABLE ADD COLUMN without default", @"
CREATE TABLE tbl (Id INTEGER PRIMARY KEY, MyInt INTEGER);
INSERT INTO tbl VALUES (0, 0), (1, 500), (2, 1000);
ALTER TABLE tbl ADD COLUMN NewColumn INTEGER;
UPDATE tbl SET NewColumn = 500 WHERE Id = 1;
INSERT INTO tbl VALUES (3, 1500, 3000);")]
    // Names ignore the case of ASCII letters only, as in SQLite, so these are two tables
    [InlineData("table names differing in non-ASCII case", @"
CREATE TABLE é (v TEXT);
CREATE TABLE É (v TEXT);
INSERT INTO é VALUES ('lower');
INSERT INTO É VALUES ('upper');")]
    [InlineData("index, view, trigger and sqlite_sequence", @"
CREATE TABLE tbl (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT UNIQUE);
CREATE INDEX tbl_name ON tbl (name DESC);
CREATE VIEW tbl_view AS SELECT name FROM tbl;
CREATE TRIGGER tbl_trigger AFTER INSERT ON tbl BEGIN SELECT 1; END;
INSERT INTO tbl (name) VALUES ('a'), ('b');")]
    public void Schema(string name, string script) => SqliteParity.AssertSameAsSqlite(script);
}
