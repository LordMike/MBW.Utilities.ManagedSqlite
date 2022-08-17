using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

public class ReaderBase : IDisposable
{
    public long Length { get; }

    public long Position => _stream.Position;

    private readonly Stream _stream;
    private readonly BinaryReader _binaryReader;

    public SqliteEncoding TextEncoding { get; private set; }

    private Encoding _encoding;

    public ushort PageSize { get; private set; }

    /// <summary>
    /// Reserved space at the end of every page
    /// </summary>
    public byte ReservedSpace { get; private set; }

    public ReaderBase(Stream stream)
    {
        _stream = stream;
        Length = _stream.Length;

        _binaryReader = new BinaryReader(stream);
    }

    internal ReaderBase(Stream stream, ReaderBase origin)
        : this(stream)
    {
        PageSize = origin.PageSize;
        ReservedSpace = origin.ReservedSpace;
        TextEncoding = origin.TextEncoding;
        _encoding = origin._encoding;
    }

    internal void ApplySqliteDatabaseHeader(DatabaseHeader header, Sqlite3Settings settings)
    {
        PageSize = header.PageSize;
        ReservedSpace = header.ReservedSpaceAtEndOfPage;
        TextEncoding = header.TextEncoding;

        if (!Enum.IsDefined(typeof(SqliteEncoding), TextEncoding) && settings.FallbackEncoding.HasValue)
            TextEncoding = settings.FallbackEncoding.Value;

        switch (TextEncoding)
        {
            case SqliteEncoding.UTF8:
                _encoding = Encoding.UTF8;
                break;
            case SqliteEncoding.UTF16LE:
                _encoding = Encoding.Unicode;
                break;
            case SqliteEncoding.UTF16BE:
                _encoding = Encoding.BigEndianUnicode;
                break;
            default:
                throw new SqliteInvalidEncodingException(TextEncoding);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }

    internal bool CheckMagicBytes(byte[] comparison, bool throwException = true)
    {
        return CheckMagicBytes((uint)comparison.Length, comparison, throwException);
    }

    internal bool CheckMagicBytes(uint toRead, byte[] comparison, bool throwException = true)
    {
        Debug.Assert(toRead >= comparison.Length);
        CheckSize(toRead);

        byte[] data = _stream.ReadFully((int)toRead);

        bool res = data.SequenceEqual(comparison);
        if (!res && throwException)
            throw new ArgumentException("The requested magic bytes did not match")
            {
                // Note: This is the position after read
                Data =
                {
                    { nameof(Stream.Position), _stream.Position },
                    { nameof(toRead), toRead },
                    { nameof(comparison), comparison.ToHex() },
                    { nameof(data), data.ToHex() }
                }
            };

        return res;
    }

    internal void CheckSize(uint sizeWanted, bool throwException = true)
    {
        if (!throwException)
            return;

        long dataLeft = Length - _stream.Position;
        if (dataLeft < sizeWanted)
        {
            throw new ArgumentException("Source stream does not have enough data")
            {
                Data =
                {
                    { nameof(Stream.Position), _stream.Position },
                    { nameof(sizeWanted), sizeWanted },
                    { "SourceLength", Length }
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
            throw new ArgumentException("Unable to seek to position")
            {
                Data = { { nameof(position), position } }
            };
    }

    internal void SeekPage(uint page, ushort offset = 0)
    {
        if (page == 0)
            throw new ArgumentOutOfRangeException(nameof(page));

        // Note: Pages are 1-indexed
        ulong position = (page - 1) * PageSize;
        position += offset;

        SetPositionAndCheckSize(position, (uint)(PageSize - offset));
    }

    internal void Skip(uint bytes)
    {
        _stream.Seek(bytes, SeekOrigin.Current);
    }

    public byte ReadByte()
    {
        return _binaryReader.ReadByte();
    }

    public ushort ReadUInt16()
    {
        ushort res = _binaryReader.ReadByte();
        res <<= 8;
        res += _binaryReader.ReadByte();

        return res;
    }

    public uint ReadUInt32()
    {
        uint res = _binaryReader.ReadByte();
        res <<= 8;

        res += _binaryReader.ReadByte();
        res <<= 8;

        res += _binaryReader.ReadByte();
        res <<= 8;

        res += _binaryReader.ReadByte();

        return res;
    }

    public short ReadInt16()
    {
        short res = _binaryReader.ReadByte();
        res <<= 8;
        res += _binaryReader.ReadByte();

        return res;
    }

    public int ReadInt32()
    {
        int res = _binaryReader.ReadByte();
        res <<= 8;

        res += _binaryReader.ReadByte();
        res <<= 8;

        res += _binaryReader.ReadByte();
        res <<= 8;

        res += _binaryReader.ReadByte();

        return res;
    }

    public long ReadVarInt()
    {
        return ReadVarInt(out byte _);
    }

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

            if ((tmp & 0x80) == 0x00)
            {
                // Last byte
                return res;
            }
        }

        // Read final byte
        res <<= 8;
        res += ReadByte();

        readBytes++;

        return res;
    }

    public void SkipVarInt()
    {
        // Decode huffman encoding
        //  xyyy yyyy       x = if this is the last byte
        //                  y = data
        // Each byte provides 7 bits of the final data, and one bit to indicate followup bytes
        // The first 8 bytes are like this, the potential 9th byte is all data (8 bits data)

        for (byte readBytes = 1; readBytes < 9; readBytes++)
        {
            byte tmp = ReadByte();

            if ((tmp & 0x80) == 0x00)
            {
                // Last byte
                return;
            }
        }

        // Skip final byte
        ReadByte();
    }

    public byte[] Read(int count)
    {
        return _stream.ReadFully(count);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        return _stream.ReadFully(buffer, offset, count);
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
            long extra = -1L;       // 0xFFFF FFFF FFFF FFFF in binary
            extra <<= bytes * 8;

            res |= extra;
        }

        return res;
    }

    public string ReadString(uint bytes)
    {
        byte[] data = Read((int)bytes);
        return _encoding.GetString(data, 0, data.Length);
    }
}