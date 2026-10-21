using System.Linq;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

/// <summary>
/// Each case builds a database with real SQLite and compares SQLite's dump of it with the library's.
/// </summary>
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

    public static TheoryData<string, string> PageSizes() => new()
    {
        { "512", PageSizeScript(512) },
        { "65536", PageSizeScript(65536) },
    };

    public static TheoryData<string, string> BTreeShapes() => new()
    {
        // 7919 is coprime to 2000, so every id appears once; then a range is rewritten in reverse so cells are out of key order
        { "shuffled insert 2000 rows", @"
PRAGMA page_size = 512;
CREATE TABLE t (Id INTEGER PRIMARY KEY, Value TEXT);
WITH RECURSIVE seq(x) AS (SELECT 0 UNION ALL SELECT x + 1 FROM seq WHERE x < 1999)
INSERT INTO t SELECT ((x * 7919) % 2000) + 1, 'row ' || (((x * 7919) % 2000) + 1) FROM seq;
DELETE FROM t WHERE Id BETWEEN 500 AND 1500;
WITH RECURSIVE seq(x) AS (SELECT 1500 UNION ALL SELECT x - 1 FROM seq WHERE x > 500)
INSERT INTO t SELECT x, 'row ' || x FROM seq;" },
        // Negative rowids and rowids >= 2^56 take a 9-byte varint in the cell header
        { "negative and very large rowids", @"
CREATE TABLE t (Value TEXT);
INSERT INTO t (rowid, Value) VALUES (-9223372036854775808, 'min'), (-5, 'negative'), (-1, 'minus one'), (0, 'zero'),
    (127, 'max 1 byte'), (128, 'min 2 bytes'), (72057594037927935, 'max 8 bytes'), (72057594037927936, 'min 9 bytes'),
    (9223372036854775807, 'max');" },
        // The overflow chain of the middle row is read between two rows whose payload is on the leaf page
        { "local row, overflowing row, local row on one leaf", @"
CREATE TABLE t (v);
INSERT INTO t VALUES (1), (randomblob(5000)), (2);" },
        // An index b-tree, keys inserted out of order, with enough rows for interior pages, whose cells hold rows too
        { "WITHOUT ROWID 3000 rows, 3 levels", @"
PRAGMA page_size = 512;
CREATE TABLE t (k TEXT PRIMARY KEY, v INTEGER) WITHOUT ROWID;
WITH RECURSIVE seq(x) AS (SELECT 0 UNION ALL SELECT x + 1 FROM seq WHERE x < 2999)
INSERT INTO t SELECT printf('key %05d', (x * 7919) % 3000), x FROM seq;" },
        // Index pages keep less of a payload locally than table pages, so these keys overflow in leaf and interior cells
        { "WITHOUT ROWID overflow pages", @"
PRAGMA page_size = 512;
CREATE TABLE t (k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID;
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 200)
INSERT INTO t SELECT printf('%04d', x) || printf('%.*c', x * 3, 'k'), CASE WHEN x = 100 THEN zeroblob(20000) ELSE x END FROM seq;" },
        // Pointer map pages sit between the b-tree pages, and are not part of any b-tree
        { "auto_vacuum with pointer map pages", @"
PRAGMA page_size = 512;
PRAGMA auto_vacuum = FULL;
CREATE TABLE a (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE b (Id INTEGER PRIMARY KEY, Value BLOB);
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 300)
INSERT INTO a SELECT x, 'a' || x FROM seq;
INSERT INTO b VALUES (1, zeroblob(3000));
DELETE FROM a WHERE Id % 3 = 0;" },
        { "deleted rows leave freeblocks and free pages", @"
PRAGMA page_size = 512;
CREATE TABLE t (Id INTEGER PRIMARY KEY, Value TEXT);
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 2000)
INSERT INTO t SELECT x, 'row ' || x FROM seq;
DELETE FROM t WHERE Id % 2 = 0;" },
    };

    public static TheoryData<string, string> Schemas()
    {
        StringBuilder many = new StringBuilder();
        foreach (int i in Enumerable.Range(0, 50))
        {
            many.Append($"CREATE TABLE Table{i:00} (Id INTEGER PRIMARY KEY, Value TEXT);\n");
            many.Append($"INSERT INTO Table{i:00} VALUES (1, 'v{i}');\n");
        }

        return new TheoryData<string, string>
        {
            // sqlite_master no longer fits on page 1, so it becomes an interior page over leaves
            { "50 tables", many.ToString() },
            // Rows written before ADD COLUMN have no value for the new column in their record, and read as NULL
            { "ALTER TABLE ADD COLUMN without default", @"
CREATE TABLE tbl (Id INTEGER PRIMARY KEY, MyInt INTEGER);
INSERT INTO tbl VALUES (0, 0), (1, 500), (2, 1000);
ALTER TABLE tbl ADD COLUMN NewColumn INTEGER;
UPDATE tbl SET NewColumn = 500 WHERE Id = 1;
INSERT INTO tbl VALUES (3, 1500, 3000);" },
            // Names ignore the case of ASCII letters only, as in SQLite, so these are two tables
            { "table names differing in non-ASCII case", @"
CREATE TABLE é (v TEXT);
CREATE TABLE É (v TEXT);
INSERT INTO é VALUES ('lower');
INSERT INTO É VALUES ('upper');" },
            { "index, view, trigger and sqlite_sequence", @"
CREATE TABLE tbl (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT UNIQUE);
CREATE INDEX tbl_name ON tbl (name DESC);
CREATE VIEW tbl_view AS SELECT name FROM tbl;
CREATE TRIGGER tbl_trigger AFTER INSERT ON tbl BEGIN SELECT 1; END;
INSERT INTO tbl (name) VALUES ('a'), ('b');" },
            // The record holds the primary key columns first, in key order and each once (SQLite drops the repeated a),
            // then the others in declared order
            { "WITHOUT ROWID composite key in another order, with a repeated column", @"
CREATE TABLE tbl (v TEXT, b INTEGER, a TEXT, PRIMARY KEY (a, b, a)) WITHOUT ROWID;
INSERT INTO tbl VALUES ('first', 2, 'y'), ('second', 1, 'y'), ('third', 9, 'x');" },
            // The virtual table is only listed, and its shadow tables are read, docs_idx and docs_config being WITHOUT ROWID
            { "fts5 shadow tables", @"
CREATE VIRTUAL TABLE docs USING fts5(title, body);
INSERT INTO docs VALUES ('hello world', 'first'), ('second row', 'hello again');" },
            { "quoted names with spaces and keywords", @"
CREATE TABLE ""my table"" (""select"" TEXT, ""a b"" INTEGER, ""q""""uote"" TEXT);
CREATE TABLE ""order"" (id INTEGER PRIMARY KEY, ""group"" TEXT);
INSERT INTO ""my table"" VALUES ('s', 1, 'q');
INSERT INTO ""order"" VALUES (1, 'g'), (2, 'h');" },
            // A STORED generated column is in the record at its position. A VIRTUAL one is not, so the columns after it
            // sit one position left there.
            { "STORED generated column next to VIRTUAL", @"
CREATE TABLE tbl (a INTEGER, v AS (a * 2) VIRTUAL, s INTEGER AS (a * 3) STORED, z TEXT);
INSERT INTO tbl (a, z) VALUES (1, 'one'), (2, 'two');" },
            // The record is the key, then the other stored columns: k, v, w. The VIRTUAL column g takes no position.
            { "WITHOUT ROWID VIRTUAL generated column before a stored column", @"
CREATE TABLE tbl (v TEXT, g AS (v || '!') VIRTUAL, w INTEGER, k TEXT PRIMARY KEY) WITHOUT ROWID;
INSERT INTO tbl (v, w, k) VALUES ('first', 1, 'b'), ('second', 2, 'a');" },
        };
    }

    [Theory]
    [MemberData(nameof(PageSizes))]
    public void PageSize(string name, string script) => SqliteParity.AssertSameAsSqlite(script);

    /// <summary>
    /// Bytes reserved at the end of each page shrink the usable size that the cell and overflow layout is computed
    /// from. 32 of 512 leaves 480, the smallest usable size SQLite allows.
    /// https://www.sqlite.org/fileformat.html#reserved_bytes_per_page
    /// </summary>
    [Fact]
    public void ReservedBytesPerPage() => SqliteParity.AssertSameAsSqlite(32, PageSizeScript(512));

    [Theory]
    [MemberData(nameof(BTreeShapes))]
    public void BTreeShape(string name, string script) => SqliteParity.AssertSameAsSqlite(script);

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Schema(string name, string script) => SqliteParity.AssertSameAsSqlite(script);
}
