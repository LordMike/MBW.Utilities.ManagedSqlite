using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

/// <summary>
/// A file read straight from a seekable stream. Each read seeks and then reads under a lock, so threads that read the
/// file at once each get their own bytes.
/// </summary>
internal sealed class StreamFileView : IFileView
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly object _lock = new object();

    public long Length { get; }

    /// <summary>
    /// Wraps <paramref name="stream"/>, which the caller has checked is readable and seekable
    /// </summary>
    public StreamFileView(Stream stream, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Length = stream.Length;
    }

    public int Read(long position, byte[] buffer, int offset, int count)
    {
        if (position >= Length)
            return 0;

        lock (_lock)
        {
            // HDR-17: a position past what the stream can seek to is past the end of the file
            Checks.Strict(_stream.Seek(position, SeekOrigin.Begin) == position, "HDR-17",
                $"unable to seek to offset {position}, which the file refers to", structure: "file length");

            return _stream.ReadFully(buffer, offset, count);
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _stream.Dispose();
    }
}
