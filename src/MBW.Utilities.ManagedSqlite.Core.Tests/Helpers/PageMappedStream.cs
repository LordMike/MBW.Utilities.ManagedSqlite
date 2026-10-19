using System;
using System.Collections.Generic;
using System.IO;

namespace MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;

/// <summary>
/// Read-only stream presenting a virtual file of any length, made of fixed-size pages.
/// Only the pages given are backed by data, every other page reads as zeros, so a file
/// of many GiB costs no more memory than its mapped pages.
/// </summary>
internal sealed class PageMappedStream : Stream
{
    private readonly int _pageSize;
    private readonly IReadOnlyDictionary<long, byte[]> _pages;
    private long _position;

    /// <param name="pageSize">Size of each page in bytes</param>
    /// <param name="length">The virtual length of the stream</param>
    /// <param name="pages">Page contents keyed by 1-based page number, each exactly <paramref name="pageSize"/> bytes</param>
    public PageMappedStream(int pageSize, long length, IReadOnlyDictionary<long, byte[]> pages)
    {
        foreach (byte[] page in pages.Values)
        {
            if (page.Length != pageSize)
                throw new ArgumentException("All pages must be exactly one page long", nameof(pages));
        }

        _pageSize = pageSize;
        _pages = pages;
        Length = length;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = 0;

        while (read < count && _position < Length)
        {
            long pageNumber = _position / _pageSize + 1;
            int inPage = (int)(_position % _pageSize);
            int chunk = (int)Math.Min(Math.Min(count - read, _pageSize - inPage), Length - _position);

            if (_pages.TryGetValue(pageNumber, out byte[] page))
                Array.Copy(page, inPage, buffer, offset + read, chunk);
            else
                Array.Clear(buffer, offset + read, chunk);

            read += chunk;
            _position += chunk;
        }

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0)
            throw new IOException("Cannot seek before the start of the stream");

        _position = newPosition;
        return _position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
