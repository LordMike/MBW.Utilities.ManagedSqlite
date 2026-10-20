using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class VarInt_Tests
{
    private const byte Sentinel = 0xAB;

    /// <summary>
    /// Boundary values for each varint length: the largest value of length n-1 and the smallest of length n
    /// </summary>
    public static IEnumerable<object[]> BoundaryValues()
    {
        yield return new object[] { 0L, 1 };

        for (int length = 1; length < 8; length++)
        {
            long maxOfLength = (1L << (7 * length)) - 1;

            yield return new object[] { maxOfLength, length };
            yield return new object[] { maxOfLength + 1, length + 1 };
        }

        // 2^56 - 1 is the largest 8-byte value, anything above (or negative) takes 9 bytes
        yield return new object[] { (1L << 56) - 1, 8 };
        yield return new object[] { 1L << 56, 9 };
        yield return new object[] { long.MaxValue, 9 };
        yield return new object[] { -1L, 9 };
        yield return new object[] { long.MinValue, 9 };
        yield return new object[] { long.MinValue + 1, 9 };
        yield return new object[] { 0x0123456789ABCDEFL, 9 };
    }

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

    /// <summary>
    /// Encodes a value as an SQLite varint: up to 8 bytes of 7 bits (high bit = more follows), or 8 such bytes plus a 9th byte holding 8 bits
    /// </summary>
    private static byte[] Encode(long value)
    {
        ulong v = (ulong)value;

        if ((v >> 56) != 0)
        {
            byte[] res = new byte[9];
            res[8] = (byte)v;
            v >>= 8;

            for (int i = 7; i >= 0; i--)
            {
                res[i] = (byte)((v & 0x7F) | 0x80);
                v >>= 7;
            }

            return res;
        }

        List<byte> bytes = new List<byte> { (byte)(v & 0x7F) };
        v >>= 7;

        while (v != 0)
        {
            bytes.Insert(0, (byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }

        return bytes.ToArray();
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
    [MemberData(nameof(BoundaryValues))]
    public void ReadVarIntBoundaries(long value, int expectedLength)
    {
        byte[] encoded = Encode(value);
        Assert.Equal(expectedLength, encoded.Length);

        AssertDecodes(encoded, value, expectedLength);
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
