using System;
using System.IO;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

public static class StreamExtensions
{
    internal static void CheckSize(this Stream stream, uint sizeWanted, bool throwException = true)
    {
        if (!throwException)
            return;

        if (stream.Length - stream.Position < sizeWanted)
        {
            throw new ArgumentException("Source stream does not have enough data")
            {
                Data =
                {
                    { nameof(Stream.Position), stream.Position },
                    { nameof(sizeWanted), sizeWanted },
                    { "SourceLength", stream.Length }
                }
            };
        }
    }

    internal static void SetPositionAndCheckSize(this Stream stream, ulong position, uint sizeWanted, bool throwException = true)
    {
        SetPosition(stream, position);
        CheckSize(stream, sizeWanted, throwException);
    }

    internal static void SetPosition(this Stream stream, ulong position)
    {
        ulong newPosition = (ulong)stream.Seek((long)position, SeekOrigin.Begin);

        if (newPosition != position)
            throw new ArgumentException("Unable to seek to position") { Data = { { nameof(position), position } } };
    }

    internal static ushort ReadUInt16(this Stream stream)
    {
        ushort res = (ushort)stream.ReadByte();
        res <<= 8;
        res += (ushort)stream.ReadByte();

        return res;
    }

    internal static uint ReadUInt32(this Stream stream)
    {
        uint res = (uint)stream.ReadByte();
        res <<= 8;

        res += (uint)stream.ReadByte();
        res <<= 8;

        res += (uint)stream.ReadByte();
        res <<= 8;

        res += (uint)stream.ReadByte();

        return res;
    }

    internal static short ReadInt16(this Stream stream)
    {
        short res = (short)stream.ReadByte();
        res <<= 8;
        res += (short)stream.ReadByte();

        return res;
    }

    internal static int ReadInt32(this Stream stream)
    {
        int res = stream.ReadByte();
        res <<= 8;

        res += stream.ReadByte();
        res <<= 8;

        res += stream.ReadByte();
        res <<= 8;

        res += stream.ReadByte();

        return res;
    }

    internal static long ReadVarInt(this Stream stream) => ReadVarInt(stream, out byte _);

    internal static long ReadVarInt(this Stream stream, out byte readBytes)
    {
        long res = 0;

        // Decode huffman encoding
        //  xyyy yyyy       x = if this is the last byte
        //                  y = data
        // Each byte provides 7 bits of the final data, and one bit to indicate followup bytes
        // The first 8 bytes are like this, the potential 9th byte is all data (8 bits data)

        for (readBytes = 1; readBytes < 9; readBytes++)
        {
            byte tmp = (byte)stream.ReadByte();

            res <<= 7;
            res += tmp & 0x7F;

            if ((tmp & 0x80) == 0x00)
            {
                // Last byte
                return res;
            }
        }

        // Read final byte
        res <<= 8;
        res += stream.ReadByte();

        readBytes++;

        return res;
    }

    internal static long ReadInteger(this Stream stream, byte bytes)
    {
        long res = 0;

        for (int i = 0; i < bytes; i++)
        {
            byte tmp = (byte)stream.ReadByte();

            res <<= 8;
            res += tmp;
        }

        if (((1L << (bytes * 8 - 1)) & res) > 0)
        {
            // Number was negative
            long extra = -1L; // 0xFFFF FFFF FFFF FFFF in binary
            extra <<= bytes * 8;

            res |= extra;
        }

        return res;
    }
}