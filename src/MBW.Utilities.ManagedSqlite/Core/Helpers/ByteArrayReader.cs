using System.IO;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

/// <summary>
/// Big-endian and varint decoding from a byte array, reading at most <c>length</c> bytes. Reading past the end
/// throws <see cref="EndOfStreamException"/>, like the stream based readers do.
/// </summary>
internal static class ByteArrayReader
{
    public static long ReadVarInt(byte[] data, int length, ref int pos)
    {
        // SQLite varint: 8 bytes of 7 bits each, then a 9th byte of 8 bits
        long res = 0;
        for (int i = 0; i < 8; i++)
        {
            if (pos >= length)
                throw new EndOfStreamException();

            byte tmp = data[pos++];
            res = (res << 7) | (long)(tmp & 0x7F);

            if ((tmp & 0x80) == 0x00)
                return res;
        }

        if (pos >= length)
            throw new EndOfStreamException();

        return (res << 8) | data[pos++];
    }

    /// <summary>
    /// Reads a big-endian two's complement integer of 1 to 8 bytes
    /// </summary>
    public static long ReadInteger(byte[] data, int length, ref int pos, int bytes)
    {
        if (bytes > length - pos)
            throw new EndOfStreamException();

        // The sign comes from the first byte
        long res = (sbyte)data[pos++];
        for (int i = 1; i < bytes; i++)
            res = (res << 8) | data[pos++];

        return res;
    }

    public static byte ReadByte(byte[] data, int length, ref int pos)
    {
        if (pos >= length)
            throw new EndOfStreamException();

        return data[pos++];
    }

    public static ushort ReadUInt16(byte[] data, int length, ref int pos)
    {
        if (2 > length - pos)
            throw new EndOfStreamException();

        ushort res = (ushort)((data[pos] << 8) | data[pos + 1]);
        pos += 2;
        return res;
    }

    public static uint ReadUInt32(byte[] data, int length, ref int pos)
    {
        if (4 > length - pos)
            throw new EndOfStreamException();

        uint res = ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16) | ((uint)data[pos + 2] << 8) | data[pos + 3];
        pos += 4;
        return res;
    }
}
