using System;
using System.IO;

namespace MBW.Utilities.ManagedSqlite.Core.Internal.Helpers;

internal ref struct SqliteDataStream(ReaderBase reader, uint page, ushort dataOffset, ushort dataLength, uint overflowPage, long fullDataSize)
{
    private uint _currentPage = page;
    private ushort _dataOffset = dataOffset;
    private ushort _dataLengthRemaining = dataLength;
    private uint _nextPage = overflowPage;
    private long _fullLengthRemaining = fullDataSize;

    public int Read(byte[] buffer, int offset, int count)
    {
        // Check if we're EOF
        if (_dataLengthRemaining == 0)
            return 0;

        // Read as much as possible on this page
        ushort canRead = (ushort)Math.Min(count, _dataLengthRemaining);

        reader.SeekPage(_currentPage, _dataOffset);
        reader.Read(buffer, offset, canRead);

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
            reader.SeekPage(_currentPage);
            _nextPage = reader.ReadUInt32();

            _dataOffset = sizeof(uint);
            _dataLengthRemaining = (ushort)Math.Min(reader.PageSize - _dataOffset - reader.ReservedSpace, _fullLengthRemaining);
        }

        return canRead;
    }
}