using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class VarInt_Tests
{
    private const byte Sentinel = 0xAB;

    public static IEnumerable<object[]> KnownEncodings()
    {
        yield return new object[] { new byte[] { 0x00 }, 0L };
        yield return new object[] { new byte[] { 0x7F }, 127L };
        yield return new object[] { new byte[] { 0x81, 0x00 }, 128L };
        yield return new object[] { new byte[] { 0xFF, 0x7F }, 16383L };
        yield return new object[] { new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, (1L << 56) - 1 };
        yield return new object[] { new byte[] { 0x80, 0xC0, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 }, 1L << 56 };
        yield return new object[] { new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, -1L };
        yield return new object[] { new byte[] { 0xBF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, long.MaxValue };
        yield return new object[] { new byte[] { 0xC0, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 }, long.MinValue };
    }

    private static void AssertDecodes(byte[] encoded, long expectedValue, int expectedLength)
    {
        // Append a sentinel byte, to verify that exactly the varint was consumed
        byte[] data = new byte[encoded.Length + 1];
        encoded.CopyTo(data, 0);
        data[encoded.Length] = Sentinel;

        int pos = 0;
        Assert.Equal(expectedValue, ByteArrayReader.ReadVarInt(data, data.Length, ref pos));
        Assert.Equal(expectedLength, pos);
    }

    [Theory]
    [MemberData(nameof(KnownEncodings))]
    public void ReadVarIntKnownEncodings(byte[] encoded, long expected)
    {
        AssertDecodes(encoded, expected, encoded.Length);
    }

    [Fact]
    public void ByteArrayReadVarIntPastLengthThrows()
    {
        // A varint whose continuation bit points past the readable length, as in a truncated record
        byte[] data = { 0x81, 0x81, 0x00 };
        int pos = 0;

        Assert.Throws<EndOfStreamException>(() => ByteArrayReader.ReadVarInt(data, 2, ref pos));
    }
}
