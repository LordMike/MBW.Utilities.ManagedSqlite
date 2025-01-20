using System;
using System.IO;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Internal.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

public class ReaderBase
{
    private readonly Stream _stream;
    private readonly BinaryReader _binaryReader;
    private Encoding _encoding;
    private ushort _pageSize;

    public ReaderBase(Stream stream)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        _stream = stream;
        _binaryReader = new BinaryReader(stream);
    }

    public long Position => _stream.Position;

    internal void ApplySqliteDatabaseHeader(DatabaseHeader header)
    {
        _encoding = header.TextEncoding switch
        {
            SqliteEncoding.UTF8 => Encoding.UTF8,
            SqliteEncoding.UTF16LE => Encoding.Unicode,
            SqliteEncoding.UTF16BE => Encoding.BigEndianUnicode,
            _ => throw new SqliteInvalidEncodingException(header.TextEncoding)
        };

        _pageSize = header.PageSize;
    }

    internal void CheckSize(uint sizeWanted, bool throwException = true)
    {
        if (!throwException)
            return;

        if (_stream.Length - _stream.Position < sizeWanted)
        {
            throw new ArgumentException("Source stream does not have enough data")
            {
                Data =
                {
                    { nameof(Stream.Position), _stream.Position },
                    { nameof(sizeWanted), sizeWanted },
                    { "SourceLength", _stream.Length }
                }
            };
        }
    }

    internal void SetPositionAndCheckSize(ulong position, uint sizeWanted, bool throwException = true)
    {
        SetPosition(position);
        CheckSize(sizeWanted, throwException);
    }

    internal void SetPosition(ulong position)
    {
        ulong newPosition = (ulong)_stream.Seek((long)position, SeekOrigin.Begin);

        if (newPosition != position)
            throw new ArgumentException("Unable to seek to position") { Data = { { nameof(position), position } } };
    }

    internal void SeekPage(uint page, ushort offset = 0)
    {
        if (page == 0)
            throw new ArgumentOutOfRangeException(nameof(page));

        // Note: Pages are 1-indexed
        ulong position = (page - 1) * _pageSize;
        position += offset;

        SetPositionAndCheckSize(position, (uint)(_pageSize - offset));
    }

    internal void Skip(uint bytes) => _stream.Seek(bytes, SeekOrigin.Current);

    public byte ReadByte() => _binaryReader.ReadByte();
    public ushort ReadUInt16() => _binaryReader.ReadUInt16();
    public uint ReadUInt32() => _binaryReader.ReadUInt32();
    public short ReadInt16() => _binaryReader.ReadInt16();
    public int ReadInt32() => _binaryReader.ReadInt32();

    public long ReadVarInt() => ReadVarInt(out byte _);

    public long ReadVarInt(out byte readBytes)
    {
        long res = 0;

        // Decode huffman encoding
        //  xyyy yyyy       x = if this is the last byte
        //                  y = data
        // Each byte provides 7 bits of the final data, and one bit to indicate followup bytes
        // The first 8 bytes are like this, the potential 9th byte is all data (8 bits data)

        for (readBytes = 1; readBytes < 9; readBytes++)
        {
            byte tmp = ReadByte();

            res <<= 7;
            res += tmp & 0x7F;

            if ((tmp & 0x80) == 0x00) // Last byte
                return res;
        }

        // Read final byte
        res <<= 8;
        res += ReadByte();

        readBytes++;

        return res;
    }

    public long ReadInteger(byte bytes)
    {
        long res = 0;

        for (int i = 0; i < bytes; i++)
        {
            byte tmp = ReadByte();

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

    public byte[] Read(int count) => _stream.ReadFully(count);
    public int Read(byte[] buffer, int offset, int count) => _stream.ReadFully(buffer, offset, count);

    public string ReadString(uint bytes)
    {
        byte[] data = Read((int)bytes);
        return _encoding.GetString(data, 0, data.Length);
    }
}