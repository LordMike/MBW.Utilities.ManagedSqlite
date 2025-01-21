using System;
using System.IO;

namespace MBW.Utilities.ManagedSqlite.Core.Internal.Helpers;

internal class CellStream(PagedStream stream) : Stream
{
    private uint _currentPage;
    private ushort _dataOffset;
    private ushort _dataLengthRemaining;
    private uint _nextPage;
    private long _fullLengthRemaining;

    public void SetParameters(uint page, ushort dataOffset, ushort dataLength, uint overflowPage, long fullDataSize)
    {
        _currentPage = page;
        _dataOffset = dataOffset;
        _dataLengthRemaining = dataLength;
        _nextPage = overflowPage;
        _fullLengthRemaining = fullDataSize;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Check if we're EOF
        if (_dataLengthRemaining == 0)
            return 0;

        // Read as much as possible on this page
        ushort canRead = (ushort)Math.Min(count, _dataLengthRemaining);

        stream.SeekPage(_currentPage, _dataOffset);
        stream.Read(buffer, offset, canRead);

        // Current page tracking
        _dataOffset += canRead;
        _dataLengthRemaining -= canRead;

        // Global state tracking
        _fullLengthRemaining -= canRead;

        // Special case, moving to the next page
        if (_dataLengthRemaining == 0 && _nextPage > 0)
        {
            _currentPage = _nextPage;

            // Overflow pages begin with a 4 byte header pointing to the next page
            stream.SeekPage(_currentPage);
            _nextPage = stream.ReadUInt32();

            _dataOffset = sizeof(uint);
            _dataLengthRemaining = (ushort)Math.Min(stream.PageSize - _dataOffset - stream.ReservedSpace, _fullLengthRemaining);
        }

        return canRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
    public override void SetLength(long value) => stream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
    public override void Flush() => stream.Flush();

    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => stream.CanSeek;
    public override bool CanWrite => stream.CanWrite;
    public override long Length => stream.Length;

    public override long Position
    {
        get => stream.Position;
        set => stream.Position = value;
    }
}