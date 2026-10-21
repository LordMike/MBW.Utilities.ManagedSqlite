using System;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class Values_Tests
{
    /// <summary>
    /// The edges of each integer serial type (0 and 1 have their own, 8 and 9, with no payload), on both signs.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(-1L)]
    [InlineData(127L)]
    [InlineData(128L)]
    [InlineData(-128L)]
    [InlineData(-129L)]
    [InlineData(32767L)]
    [InlineData(32768L)]
    [InlineData(-32768L)]
    [InlineData(-32769L)]
    [InlineData(8388607L)]
    [InlineData(8388608L)]
    [InlineData(-8388608L)]
    [InlineData(-8388609L)]
    [InlineData(2147483647L)]
    [InlineData(2147483648L)]
    [InlineData(-2147483648L)]
    [InlineData(-2147483649L)]
    [InlineData(140737488355327L)]
    [InlineData(140737488355328L)]
    [InlineData(-140737488355328L)]
    [InlineData(-140737488355329L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Integers(long value)
    {
        SqliteParity.AssertSameAsSqlite($"CREATE TABLE tbl (v INTEGER); INSERT INTO tbl VALUES ({value});");
    }

    [Theory]
    [InlineData("0.0")]
    [InlineData("-0.0")]
    [InlineData("1.0")]
    [InlineData("-1.0")]
    [InlineData("0.125")]
    [InlineData("1000.5")]
    [InlineData("-1000.5")]
    [InlineData("1.7976931348623157e308")]
    [InlineData("-1.7976931348623157e308")]
    [InlineData("4.9e-324")]
    [InlineData("2.2250738585072014e-308")]
    [InlineData("0.1")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public void Reals(string literal)
    {
        SqliteParity.AssertSameAsSqlite($"CREATE TABLE tbl (v REAL); INSERT INTO tbl VALUES ({literal});");
    }

    /// <summary>
    /// SQLite stores NULL instead of a NaN (sqlite3VdbeMemSetDouble in vdbemem.c), so no SQL statement can put one in a
    /// file; a NaN comes only from another writer or damage, and serialGet in vdbeaux.c reads it as NULL. The stored 1.5
    /// (big-endian 3FF8000000000000, serial type 7) is overwritten with a NaN's bits (sign bit set, non-canonical
    /// payload); the text column after it proves the 8 bytes are still consumed.
    /// https://sqlite.org/src/file?name=src/vdbeaux.c, https://www.sqlite.org/fileformat.html#record_format
    /// </summary>
    [Fact]
    public void NaNReadsAsNull()
    {
        const ulong bits = 0xFFF0000000000001UL;

        SqliteParity.AssertSameAsSqlite("CREATE TABLE tbl (v REAL, w TEXT); INSERT INTO tbl VALUES (1.5, 'x');", data =>
        {
            int at = data.AsSpan().IndexOf(new byte[] { 0x3F, 0xF8, 0, 0, 0, 0, 0, 0 });
            Assert.True(at > 0);

            for (int i = 0; i < 8; i++)
                data[at + i] = (byte)(bits >> (56 - 8 * i));

            return data;
        });
    }

    public static TheoryData<string, string> TextCases()
    {
        TheoryData<string, string> data = new TheoryData<string, string>();

        foreach (string encoding in new[] { "UTF-8", "UTF-16le", "UTF-16be" })
        {
            data.Add(encoding, $@"
PRAGMA encoding = '{encoding}';
CREATE TABLE tbl (lang TEXT, v TEXT);
INSERT INTO tbl VALUES ('da', 'Quizdeltagerne spiste jordbær med fløde, mens cirkusklovnen Wolther spillede på xylofon.');
INSERT INTO tbl VALUES ('fr', 'Portez ce vieux whisky au juge blond qui fume sur son île intérieure, à côté de l''alcôve ovoïde, où les bûches se consument dans l''âtre, ce qui lui permet de penser à la cænogenèse de l''être dont il est question dans la cause ambiguë entendue à Moÿ, dans un capharnaüm qui, pense-t-il, diminue çà et là la qualité de son œuvre.');
INSERT INTO tbl VALUES ('iw', 'דג סקרן שט בים מאוכזב ולפתע מצא לו חברה איך הקליטה');
INSERT INTO tbl VALUES ('ru', 'В чащах юга жил бы цитрус? Да, но фальшивый экземпляр!');
INSERT INTO tbl VALUES ('emoji', '😀 outside the BMP');
INSERT INTO tbl VALUES ('empty', '');");

            // 150,000 characters, far past one page, so the text is spread over a chain of overflow pages
            data.Add(encoding + " very long", $@"
PRAGMA encoding = '{encoding}';
CREATE TABLE tbl (v TEXT);
INSERT INTO tbl VALUES (replace(hex(zeroblob(75000)), '0', 'x'));");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public void Text(string name, string script)
    {
        _ = name;
        SqliteParity.AssertSameAsSqlite(script);
    }

    /// <summary>
    /// An empty blob, one that fits in the leaf page, and one spread over a chain of overflow pages. With the default
    /// 4096 byte page a table leaf holds at most 4096 - 35 = 4061 bytes of record locally. A single-blob record has
    /// a 3 byte header (header size, then a 2 byte serial type for sizes of 52 to 8187), so 4058 bytes is the largest
    /// blob that stays local and 4059 the smallest that overflows.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(4058)]
    [InlineData(4059)]
    [InlineData(65535)]
    public void Blobs(int size)
    {
        SqliteParity.AssertSameAsSqlite($"CREATE TABLE tbl (v BLOB); INSERT INTO tbl VALUES (randomblob({size}));");
    }

    /// <summary>
    /// SQLite picks a column's affinity from substrings of its declared type, in this order: "INT" is INTEGER; "CHAR",
    /// "CLOB" or "TEXT" is TEXT; "BLOB", or no type, is BLOB; "REAL", "FLOA" or "DOUB" is REAL; anything else is NUMERIC
    /// (https://www.sqlite.org/datatype3.html#determination_of_column_affinity). Every column gets the same values: an
    /// integer, an integral real, integral and fractional numeric text, other text and a blob. SQLite stores them
    /// differently per affinity, and with REAL affinity writes integral values as integers that must read back as REAL.
    /// </summary>
    [Theory]
    [InlineData("REAL in FLOAT8", "FLOAT8")]
    [InlineData("REAL in REALVAL", "REALVAL")]
    [InlineData("REAL in DOUBLEX", "DOUBLEX")]
    [InlineData("REAL in a multi-word name", "LONG DOUBLE")]
    [InlineData("REAL with a size", "FLOAT(10)")]
    [InlineData("REAL in lower case", "floatval")]
    [InlineData("INT before CHAR", "CHARINT")]
    [InlineData("INT before FLOA", "FLOATING POINT")]
    [InlineData("INT before DOUB", "DOUBLE INT")]
    [InlineData("INT in POINT", "POINT")]
    [InlineData("BLOB before FLOA", "FLOAT BLOB")]
    [InlineData("NUMERIC for STRINGY", "STRINGY")]
    [InlineData("NUMERIC for STRING", "STRING")]
    [InlineData("BLOB for no type", "")]
    public void DeclaredTypeAffinity(string name, string declaredType)
    {
        _ = name;
        SqliteParity.AssertSameAsSqlite($"CREATE TABLE tbl (v {declaredType}); INSERT INTO tbl VALUES (3), (3.0), ('3'), ('1.5'), ('x'), (X'00');");
    }

    public static TheoryData<string, string> NullAndMixedCases()
    {
        return new TheoryData<string, string>
        {
            { "NULL in the middle", "CREATE TABLE tbl (a INTEGER, b TEXT, c REAL, d BLOB, e INTEGER); INSERT INTO tbl VALUES (1, NULL, NULL, NULL, 5); INSERT INTO tbl VALUES (NULL, 'x', NULL, X'00', NULL);" },
            { "all storage classes", "CREATE TABLE tbl (a, b, c, d, e); INSERT INTO tbl VALUES (NULL, 42, 1.5, 'text', X'CAFE'); INSERT INTO tbl VALUES (X'CAFE', 'text', 1.5, 42, NULL);" }
        };
    }

    [Theory]
    [MemberData(nameof(NullAndMixedCases))]
    public void NullAndMixed(string name, string script)
    {
        _ = name;
        SqliteParity.AssertSameAsSqlite(script);
    }
}
