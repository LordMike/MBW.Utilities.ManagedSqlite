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
        { "1024", PageSizeScript(1024) },
        { "65536", PageSizeScript(65536) },
    };

    public static TheoryData<string, string> BTreeShapes() => new()
    {
        { "ordered 5000 rows, 3 levels", @"
PRAGMA page_size = 512;
CREATE TABLE t (Id INTEGER PRIMARY KEY, Value TEXT);
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 5000)
INSERT INTO t SELECT x, 'row ' || x FROM seq;" },
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
            many.Append($"WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 500) INSERT INTO Table{i:00} SELECT x, 'v' || x FROM seq;\n");
        }

        return new TheoryData<string, string>
        {
            { "50 tables of 500 rows", many.ToString() },
            // Rows written before ADD COLUMN have no value for the new column in their record, and read as NULL
            { "ALTER TABLE ADD COLUMN without default", @"
CREATE TABLE tbl (Id INTEGER PRIMARY KEY, MyInt INTEGER);
INSERT INTO tbl VALUES (0, 0), (1, 500), (2, 1000);
ALTER TABLE tbl ADD COLUMN NewColumn INTEGER;
UPDATE tbl SET NewColumn = 500 WHERE Id = 1;
INSERT INTO tbl VALUES (3, 1500, 3000);" },
            { "index, view, trigger and sqlite_sequence", @"
CREATE TABLE tbl (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT UNIQUE);
CREATE INDEX tbl_name ON tbl (name DESC);
CREATE VIEW tbl_view AS SELECT name FROM tbl;
CREATE TRIGGER tbl_trigger AFTER INSERT ON tbl BEGIN SELECT 1; END;
INSERT INTO tbl (name) VALUES ('a'), ('b');" },
            { "mixed-case names", @"
CREATE TABLE MixedCaseTable (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE TABLE lower (Id INTEGER PRIMARY KEY, Value TEXT);
INSERT INTO MixedCaseTable VALUES (1, 'one'), (2, 'two');
INSERT INTO lower VALUES (1, 'x');" },
            { "quoted names with spaces and keywords", @"
CREATE TABLE ""my table"" (""select"" TEXT, ""a b"" INTEGER, ""q""""uote"" TEXT);
CREATE TABLE ""order"" (id INTEGER PRIMARY KEY, ""group"" TEXT);
INSERT INTO ""my table"" VALUES ('s', 1, 'q');
INSERT INTO ""order"" VALUES (1, 'g'), (2, 'h');" },
            // A VIRTUAL generated column is not in the record, so the columns after it sit one position left there
            { "VIRTUAL generated column before stored columns", @"
CREATE TABLE tbl (a, b TEXT GENERATED ALWAYS AS (a || '!') VIRTUAL, c, d INTEGER);
INSERT INTO tbl (a, c, d) VALUES ('x', 'third', 4);" },
            // Without VIRTUAL or STORED, a generated column is VIRTUAL
            { "generated column in short AS form", @"
CREATE TABLE tbl (a, b AS (a || '!'), c);
INSERT INTO tbl (a, c) VALUES ('x', 'third');" },
            // A STORED generated column is in the record at its position, a VIRTUAL one is not
            { "STORED generated column next to VIRTUAL", @"
CREATE TABLE tbl (a INTEGER, v AS (a * 2) VIRTUAL, s INTEGER AS (a * 3) STORED, z TEXT);
INSERT INTO tbl (a, z) VALUES (1, 'one'), (2, 'two');" },
            // rtree rather than fts5: fts5's shadow tables include WITHOUT ROWID tables, which the dump can't select rowid from
            { "rtree virtual table and plain table", @"
CREATE VIRTUAL TABLE boxes USING rtree(id, minX, maxX, minY, maxY);
INSERT INTO boxes VALUES (1, 0, 10, 0, 10), (2, 5, 15, 5, 15), (3, -3, 3, -3, 3);
CREATE TABLE Plain (Id INTEGER PRIMARY KEY, Value TEXT);
INSERT INTO Plain VALUES (1, 'one'), (2, 'two');" },
        };
    }

    [Theory]
    [MemberData(nameof(PageSizes))]
    public void PageSize(string name, string script) => SqliteParity.AssertSameAsSqlite(script);

    [Theory]
    [MemberData(nameof(BTreeShapes))]
    public void BTreeShape(string name, string script) => SqliteParity.AssertSameAsSqlite(script);

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Schema(string name, string script) => SqliteParity.AssertSameAsSqlite(script);
}
