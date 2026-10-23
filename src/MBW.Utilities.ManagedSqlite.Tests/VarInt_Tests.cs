using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

public class VarInt_Tests
{
    [Theory]
    [InlineData(new byte[] { 0x00 }, 0L)]
    [InlineData(new byte[] { 0x7F }, 127L)]
    [InlineData(new byte[] { 0x81, 0x00 }, 128L)]
    [InlineData(new byte[] { 0xFF, 0x7F }, 16383L)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, (1L << 56) - 1)]
    [InlineData(new byte[] { 0x80, 0xC0, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 }, 1L << 56)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, -1L)]
    [InlineData(new byte[] { 0xBF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, long.MaxValue)]
    [InlineData(new byte[] { 0xC0, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 }, long.MinValue)]
    public void ReadVarIntKnownEncodings(byte[] encoded, long expected)
    {
        // A sentinel byte after the varint, to verify that exactly the varint was consumed
        byte[] data = new byte[encoded.Length + 1];
        encoded.CopyTo(data, 0);
        data[encoded.Length] = 0xAB;

        int pos = 0;
        Assert.Equal(expected, ByteArrayReader.ReadVarInt(data, data.Length, ref pos));
        Assert.Equal(encoded.Length, pos);
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
