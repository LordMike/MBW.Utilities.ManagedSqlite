using System;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

public class Values_Tests
{
    /// <summary>
    /// The largest and smallest value of each integer serial type: 0 and 1 have their own (8 and 9, with no payload),
    /// then 1, 2, 3, 4, 6 and 8 byte two's complement. The largest sets every bit below the sign; the smallest is the
    /// sign bit alone.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(127L)]
    [InlineData(-128L)]
    [InlineData(32767L)]
    [InlineData(-32768L)]
    [InlineData(8388607L)]
    [InlineData(-8388608L)]
    [InlineData(2147483647L)]
    [InlineData(-2147483648L)]
    [InlineData(140737488355327L)]
    [InlineData(-140737488355328L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Integers(long value)
    {
        SqliteParity.AssertSameAsSqlite($"CREATE TABLE tbl (v INTEGER); INSERT INTO tbl VALUES ({value});");
    }

    /// <summary>
    /// SQLite stores a REAL with no fractional part as an integer (0.0 as serial type 8, -1.0 as a 1 byte integer), and
    /// a REAL column reads it back as a REAL. 1e999 is infinity.
    /// </summary>
    [Theory]
    [InlineData("0.0")]
    [InlineData("-1.0")]
    [InlineData("-1000.5")]
    [InlineData("1e999")]
    public void Reals(string literal)
    {
        SqliteParity.AssertSameAsSqlite($"CREATE TABLE tbl (v REAL); INSERT INTO tbl VALUES ({literal});");
    }

    /// <summary>
    /// SQLite stores NULL instead of a NaN (sqlite3VdbeMemSetDouble in vdbemem.c), so no SQL statement can put one in a
    /// file; a NaN comes only from another writer or damage, and serialGet in vdbeaux.c reads it as NULL. The stored 1.5
    /// (big-endian 3FF8000000000000, serial type 7) is overwritten with a NaN's bits (sign bit set, non-canonical
    /// payload); the text column after it proves the 8 bytes are still consumed.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbeaux.c#L4071, https://www.sqlite.org/fileformat.html#record_format
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

    /// <summary>
    /// SQLite reads the reserved serial types 10 and 11 as NULL (sqlite3VdbeSerialGet in vdbeaux.c,
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbeaux.c#L4123). The NULL in a's record header (serial type 0, the byte after the
    /// header size) is overwritten, and the text after it proves no bytes are consumed.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void ReservedSerialTypeReadsAsNull(byte serialType)
    {
        SqliteParity.AssertSameAsSqlite("CREATE TABLE tbl (a, b TEXT); INSERT INTO tbl VALUES (NULL, 'one');", data =>
        {
            DbPatcher patcher = new DbPatcher(data);
            int record = patcher.GetRecordOffset(2, 0, out _);
            return patcher.WriteByte(record + 1, 0, serialType).Data;
        });
    }

    /// <summary>
    /// Text in each encoding: non-ASCII, a character outside the BMP (a UTF-16 surrogate pair), empty, and 150,000
    /// characters, far past one page, spread over a chain of overflow pages
    /// </summary>
    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public void Text(string encoding)
    {
        SqliteParity.AssertSameAsSqlite($@"
PRAGMA encoding = '{encoding}';
CREATE TABLE tbl (lang TEXT, v TEXT);
INSERT INTO tbl VALUES ('da', 'Quizdeltagerne spiste jordbær med fløde, mens cirkusklovnen Wolther spillede på xylofon.');
INSERT INTO tbl VALUES ('emoji', '😀 outside the BMP');
INSERT INTO tbl VALUES ('empty', '');
INSERT INTO tbl VALUES ('very long', replace(hex(zeroblob(75000)), '0', 'x'));");
    }

    /// <summary>
    /// An empty blob, the largest that stays on the leaf page, and the smallest that overflows. With the default
    /// 4096 byte page a table leaf holds at most 4096 - 35 = 4061 bytes of record locally. A single-blob record has
    /// a 3 byte header (header size, then a 2 byte serial type for sizes of 52 to 8187), so 4058 bytes is the largest
    /// blob that stays local and 4059 the smallest that overflows.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4058)]
    [InlineData(4059)]
    public void Blobs(int size)
    {
        SqliteParity.AssertSameAsSqlite($"CREATE TABLE tbl (v BLOB); INSERT INTO tbl VALUES (randomblob({size}));");
    }

    /// <summary>
    /// NULL (serial type 0, no bytes) between columns that have bytes
    /// </summary>
    [Fact]
    public void NullInTheMiddle()
    {
        SqliteParity.AssertSameAsSqlite("CREATE TABLE tbl (a INTEGER, b TEXT, c REAL, d BLOB, e INTEGER); INSERT INTO tbl VALUES (1, NULL, NULL, NULL, 5); INSERT INTO tbl VALUES (NULL, 'x', NULL, X'00', NULL);");
    }
}
