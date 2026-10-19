using System.Linq;
using System.Threading.Tasks;
using MBW.Utilities.ManagedSqlite.Differential.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Differential.Tests;

/// <summary>
/// Differential tests: each case is a SQL script that creates and fills a table named <c>tbl</c>. Real SQLite builds
/// the database file from it, and the library must return exactly the rows SQLite returns for
/// <c>SELECT rowid, * FROM tbl ORDER BY rowid</c>: same rowids, same values with the same storage class, same order.
/// </summary>
/// <remarks>
/// Pragmas that shape the file (page_size, encoding, auto_vacuum) are simply the first statements of a script.
/// Some cases fail on master on purpose; they document known bugs and turn green as the fixes land.
/// </remarks>
public class TableData_Tests
{
    /// <summary>
    /// A table built from a recursive CTE: <c>i</c> runs 1..<paramref name="rows"/>, and <c>big.s</c> is a
    /// 240,000-character text whose content differs at every offset, so <c>substr(big.s, i, n)</c> gives position
    /// dependent payloads that catch overflow pages stitched in the wrong order.
    /// </summary>
    private static string Fill(int rows, string selectList, string table = "tbl") =>
        $"WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < {rows}), " +
        "digits(j) AS (SELECT 1 UNION ALL SELECT j + 1 FROM digits WHERE j < 40000), " +
        "big(s) AS (SELECT group_concat(printf('%06d', j), '') FROM digits) " +
        $"INSERT INTO {table} SELECT {selectList} FROM seq, big;";

    public static TheoryData<string, string> Scripts()
    {
        TheoryData<string, string> data = new TheoryData<string, string>();

        // Values and storage classes
        data.Add("empty table", "CREATE TABLE tbl(a);");
        data.Add("single text row", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES ('hello');");
        data.Add("two rows on one leaf page", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES ('first'); INSERT INTO tbl VALUES ('second');");
        data.Add("nulls", "CREATE TABLE tbl(a, b, c); INSERT INTO tbl VALUES (NULL, NULL, NULL); INSERT INTO tbl VALUES (NULL, 'x', NULL);");
        data.Add("empty string and empty blob", "CREATE TABLE tbl(a, b); INSERT INTO tbl VALUES ('', X'');");
        data.Add("integer widths 1 to 8 bytes",
            "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (2), (-1), (127), (-128), (128), (-129), (32767), (-32768), (32768), (-32769), " +
            "(8388607), (-8388608), (8388608), (-8388609), (2147483647), (-2147483648), (2147483648), (-2147483649), " +
            "(140737488355327), (-140737488355328), (140737488355328), (-140737488355329), " +
            "(9223372036854775807), (-9223372036854775808);");
        data.Add("integer constants 0 and 1", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (0); INSERT INTO tbl VALUES (1);");
        data.Add("single integer 1", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (1);");
        data.Add("reals", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (0.5), (-0.5), (3.141592653589793), (1.5e300), (-1.5e-300), " +
                          "(1.7976931348623157e308), (-1.7976931348623157e308), (2.2250738585072014e-308), (4.9e-324), (123456789.125);");
        data.Add("negative zero", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (-0.0);");
        data.Add("infinities", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (9e999), (-9e999);");
        data.Add("REAL column with integral values (stored as integers on disk)",
            "CREATE TABLE tbl(a REAL); INSERT INTO tbl VALUES (2.0), (-3.0), (1e15), (2.5);");
        data.Add("unicode text", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES ('æøå ÆØÅ'), ('日本語'), ('😀🎉'), ('é'), ('Ω≈ç√∫'), ('tab\there');");
        data.Add("blobs", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (X'00'), (X'FF'), (X'0001020304FEFF'), (zeroblob(100));");
        data.Add("mixed storage classes in one column", "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (NULL), (42), (2.5), ('text'), (X'BEEF'), (''), (X'');");
        data.Add("column affinities",
            "CREATE TABLE tbl(i INTEGER, t TEXT, b BLOB, n NUMERIC, r REAL, x); " +
            "INSERT INTO tbl VALUES ('12', 34, X'01', '5.0', 2.5, '7'), ('abc', 2.5, 'txt', '1e3', '0.25', 8.5);");
        data.Add("100 columns (multi-byte record header)",
            "CREATE TABLE tbl(" + string.Join(", ", Enumerable.Range(0, 100).Select(i => "c" + i)) + "); " +
            "INSERT INTO tbl VALUES (" + string.Join(", ", Enumerable.Range(0, 100).Select(i => (i * 1000 + 7).ToString())) + ");");
        data.Add("INTEGER PRIMARY KEY (NULL in the record)", "CREATE TABLE tbl(id INTEGER PRIMARY KEY, v); INSERT INTO tbl VALUES (42, 'a'), (7, 'b');");

        // Rowids
        data.Add("rowid 0", "CREATE TABLE tbl(a); INSERT INTO tbl(rowid, a) VALUES (0, 'zero');");
        data.Add("large rowids below 2^56 (up to 8-byte varints)",
            "CREATE TABLE tbl(a); INSERT INTO tbl(rowid, a) VALUES (127, '2^7-1'), (128, '2^7'), (4294967296, '2^32'), (72057594037927935, '2^56-1');");
        // Rowids that need the 9-byte varint form: negative ones, and 2^56 and above
        data.Add("rowid 2^56", "CREATE TABLE tbl(a); INSERT INTO tbl(rowid, a) VALUES (72057594037927936, '2^56');");
        data.Add("rowid max", "CREATE TABLE tbl(a); INSERT INTO tbl(rowid, a) VALUES (9223372036854775807, 'max');");
        data.Add("rowid -1", "CREATE TABLE tbl(a); INSERT INTO tbl(rowid, a) VALUES (-1, 'neg');");
        data.Add("rowid min", "CREATE TABLE tbl(a); INSERT INTO tbl(rowid, a) VALUES (-9223372036854775808, 'min');");
        data.Add("sparse rowids below 2^56 in a multi-level tree",
            "CREATE TABLE tbl(a); " + Fill(3000, "i * 24019198012642, 1000 + i").Replace("INSERT INTO tbl SELECT", "INSERT INTO tbl(rowid, a) SELECT"));
        data.Add("sparse negative and huge rowids in a multi-level tree",
            "CREATE TABLE tbl(a); " + Fill(3000, "(i - 1500) * 6148914691236517, 1000 + i").Replace("INSERT INTO tbl SELECT", "INSERT INTO tbl(rowid, a) SELECT"));
        data.Add("5000 rows multi-level tree", "CREATE TABLE tbl(a, b); " + Fill(5000, "1000 + i, 'row ' || i"));

        // Page sizes (65536 is stored as 1 in the header)
        foreach (int pageSize in new[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 })
            data.Add($"page size {pageSize}",
                $"PRAGMA page_size = {pageSize}; CREATE TABLE tbl(a, b, c); " +
                Fill(1500, "1000 + i, substr(big.s, i, (i * 37) % 3000), CAST(substr(big.s, i * 3, (i * 53) % 5000) AS BLOB)"));

        // Text encodings, with text that spills to overflow pages
        foreach (string encoding in new[] { "UTF-8", "UTF-16le", "UTF-16be" })
            data.Add($"encoding {encoding}",
                $"PRAGMA encoding = '{encoding}'; CREATE TABLE tbl(a, b); " +
                Fill(500, "1000 + i, replace(replace(replace(substr(big.s, i, (i * 41) % 6000), '1', 'æ'), '2', '😀'), '3', '日')"));

        // auto_vacuum adds pointer-map pages, which a reader must skip
        foreach (string mode in new[] { "NONE", "FULL", "INCREMENTAL" })
            data.Add($"auto_vacuum {mode} with deletes",
                $"PRAGMA auto_vacuum = {mode}; CREATE TABLE tbl(a, b); " + Fill(3000, "1000 + i, substr(big.s, i, i % 300)") +
                " DELETE FROM tbl WHERE a % 3 = 0;" + (mode == "INCREMENTAL" ? " PRAGMA incremental_vacuum(10);" : ""));

        // Free pages and rewritten files
        data.Add("deletes leave a freelist",
            "CREATE TABLE tbl(a, b); " + Fill(3000, "1000 + i, substr(big.s, i, 200)") + " DELETE FROM tbl WHERE a % 3 = 0; DELETE FROM tbl WHERE a BETWEEN 2000 AND 3000;");
        data.Add("VACUUM after deletes",
            "CREATE TABLE tbl(a, b); " + Fill(3000, "1000 + i, substr(big.s, i, 200)") + " DELETE FROM tbl WHERE a % 2 = 0; VACUUM;");
        data.Add("updates grow and shrink rows",
            "CREATE TABLE tbl(a, b); " + Fill(400, "1000 + i, 'short'") +
            " UPDATE tbl SET b = substr((SELECT group_concat(printf('%06d', rowid), '') FROM tbl), a, 9000) WHERE a % 4 = 0; UPDATE tbl SET b = NULL WHERE a % 7 = 0;");
        data.Add("table dropped and recreated",
            "CREATE TABLE tbl(a); " + Fill(1000, "substr(big.s, i, 500)") + " DROP TABLE tbl; CREATE TABLE tbl(a, b); " + Fill(300, "1000 + i, -i"));

        // Overflow chains
        data.Add("240 KB text over many overflow pages", "PRAGMA page_size = 512; CREATE TABLE tbl(a); " + Fill(1, "big.s"));
        data.Add("240 KB blob over many overflow pages", "CREATE TABLE tbl(a); " + Fill(2, "CAST(substr(big.s, i) AS BLOB)"));
        data.Add("payload sizes 0 to 1200 around the overflow threshold, page size 512",
            "PRAGMA page_size = 512; CREATE TABLE tbl(a); " + Fill(1201, "CAST(substr(big.s, 1, i - 1) AS BLOB)"));
        data.Add("payload sizes 3950 to 4250 around the overflow threshold, page size 4096",
            "CREATE TABLE tbl(a); " + Fill(301, "substr(big.s, i, 3949 + i)"));

        // Table kinds
        data.Add("WITHOUT ROWID", "CREATE TABLE tbl(k TEXT PRIMARY KEY, v) WITHOUT ROWID; INSERT INTO tbl VALUES ('b', 2), ('a', 'x'), ('c', NULL);");
        data.Add("virtual table (fts5)", "CREATE VIRTUAL TABLE tbl USING fts5(a, b); INSERT INTO tbl VALUES ('hello world', 'x'), ('second row', 'y');");

        // Schema changes and generated columns
        data.Add("ALTER TABLE ADD COLUMN with DEFAULT",
            "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (10); ALTER TABLE tbl ADD COLUMN b TEXT DEFAULT 'dflt'; INSERT INTO tbl VALUES (20, 'x');");
        data.Add("ALTER TABLE ADD COLUMN without DEFAULT",
            "CREATE TABLE tbl(a); INSERT INTO tbl VALUES (10); ALTER TABLE tbl ADD COLUMN b TEXT; INSERT INTO tbl VALUES (20, 'x');");
        data.Add("generated STORED column", "CREATE TABLE tbl(a, b AS (a || '!') STORED, c); INSERT INTO tbl(a, c) VALUES ('x', 'third');");
        data.Add("generated VIRTUAL column", "CREATE TABLE tbl(a, b AS (a || '!') VIRTUAL, c); INSERT INTO tbl(a, c) VALUES ('x', 'third');");

        // Seeded random schemas and rows
        foreach (int seed in RandomScripts.Seeds)
            data.Add($"random seed {seed}", RandomScripts.Generate(seed));

        return data;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public Task LibraryReturnsSameRowsAsSqlite(string name, string script)
    {
        return CaseTimeout.Run(() => DifferentialRunner.AssertTableMatches(script, "tbl"), name);
    }
}
