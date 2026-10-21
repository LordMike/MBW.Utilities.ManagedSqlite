using System;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

/// <summary>
/// A file read straight from a seekable stream
/// </summary>
internal sealed class StreamFileView : IFileView
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    public long Length { get; }

    public StreamFileView(Stream stream, bool leaveOpen)
    {
        // A disposed stream can neither read nor seek, so check reading first to give it the clearer message
        if (!stream.CanRead)
            throw new ArgumentException("The stream must be readable, and not disposed", nameof(stream));
        if (!stream.CanSeek)
            throw new ArgumentException("The stream must be seekable", nameof(stream));

        _stream = stream;
        _leaveOpen = leaveOpen;
        Length = stream.Length;
    }

    public int Read(long position, byte[] buffer, int offset, int count)
    {
        if (position >= Length)
            return 0;

        if (_stream.Seek(position, SeekOrigin.Begin) != position)
            throw new SqliteCorruptException("unable to seek to a position the file refers to", structure: "file length")
            {
                Data = { { nameof(position), position } }
            };

        return _stream.ReadFully(buffer, offset, count);
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _stream.Dispose();
    }
}
