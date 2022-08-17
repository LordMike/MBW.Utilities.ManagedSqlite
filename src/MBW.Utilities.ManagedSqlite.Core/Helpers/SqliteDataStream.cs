using System;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Internal;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

internal class SqliteDataStream : ReadonlyStream
{
    private readonly ReaderBase _reader;
    private uint _currentPage;
    private ushort _dataOffset;
    private ushort _dataLengthRemaining;
    private uint _nextPage;

    private long _position;
    private long _fullLengthRemaining;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="reader"></param>
    /// <param name="page">The initial page</param>
    /// <param name="dataOffset">The offset to the initial data</param>
    /// <param name="dataLength">The length of the initial data in the initial page</param>
    /// <param name="overflowPage">The pointer to the first overflow page</param>
    /// <param name="fullDataSize">The final length of all data in this stream</param>
    public SqliteDataStream(ReaderBase reader, uint page, ushort dataOffset, ushort dataLength, uint overflowPage, long fullDataSize)
    {
        _reader = reader;
        _currentPage = page;
        _dataOffset = dataOffset;
        _dataLengthRemaining = dataLength;
        _nextPage = overflowPage;

        Length = fullDataSize;
        _fullLengthRemaining = fullDataSize;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Check if we're EOF
        if (_dataLengthRemaining == 0)
            return 0;

        // Read as much as possible on this page
        ushort canRead = (ushort)Math.Min(count, _dataLengthRemaining);

        _reader.SeekPage(_currentPage, _dataOffset);
        _reader.Read(buffer, offset, canRead);

        // Current page tracking
        _dataOffset += canRead;
        _dataLengthRemaining -= canRead;

        // Global state tracking
        _position += canRead;
        _fullLengthRemaining -= canRead;

        // Special case, moving to the next page
        if (_dataLengthRemaining == 0 && _nextPage > 0)
        {
            _currentPage = _nextPage;

            // Overflow pages begin with a 4 byte header pointing to the next page
            _reader.SeekPage(_currentPage);
            _nextPage = _reader.ReadUInt32();

            _dataOffset = sizeof(uint);
            _dataLengthRemaining = (ushort)Math.Min(_reader.PageSize - _dataOffset - _reader.ReservedSpace, _fullLengthRemaining);
        }

        return canRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    public override bool CanRead { get; } = true;
    public override bool CanSeek { get; } = false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => throw new NotImplementedException();
    }
}