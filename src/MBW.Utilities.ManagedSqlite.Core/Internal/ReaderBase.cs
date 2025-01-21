using System;
using System.IO;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Internal.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

internal class PagedStream(Stream stream, DatabaseHeader header) : Stream
{
    private readonly Stream _stream = stream;
    private readonly Encoding _encoding = header.TextEncoding switch
    {
        SqliteEncoding.UTF8 => Encoding.UTF8,
        SqliteEncoding.UTF16LE => Encoding.Unicode,
        SqliteEncoding.UTF16BE => Encoding.BigEndianUnicode,
        _ => throw new SqliteInvalidEncodingException(header.TextEncoding)
    };

    public ushort PageSize { get; } = header.PageSize;
    public byte ReservedSpace { get; } = header.ReservedSpaceAtEndOfPage;

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

    private void SetPositionAndCheckSize(ulong position, uint sizeWanted, bool throwException = true)
    {
        SetPosition(position);
        CheckSize(sizeWanted, throwException);
    }

    private void SetPosition(ulong position)
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
        ulong position = (page - 1) * PageSize;
        position += offset;

        SetPositionAndCheckSize(position, (uint)(PageSize - offset));
    }

    public byte[] Read(int count) => _stream.ReadFully(count);

    public override void Flush() => _stream.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _stream.ReadFully(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
    public override void SetLength(long value) => _stream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

    public override bool CanRead => _stream.CanRead;
    public override bool CanSeek => _stream.CanSeek;
    public override bool CanWrite => _stream.CanWrite;
    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public string ReadString(uint bytes)
    {
        byte[] data = Read((int)bytes);
        return _encoding.GetString(data, 0, data.Length);
    }
}