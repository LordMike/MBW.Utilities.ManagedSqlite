using System;
using System.IO;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

/// <summary>
/// Reads pages of an opened database. Create it with <see cref="Open"/>, which reads the database header first, so
/// the page size, encoding and page count are known and fixed for the reader's lifetime.
/// </summary>
internal sealed class ReaderBase : IDisposable
{
    public long Length { get; }

    public long Position => _stream.Position;

    private readonly Stream _stream;
    private readonly Encoding _encoding;
    private readonly byte[] _uint32Buffer = new byte[4];

    private byte[]? _pageBuffer;

    /// <summary>
    /// The page <see cref="_pageBuffer"/> holds, 0 while it holds none
    /// </summary>
    private uint _pageBufferPage;

    public DatabaseHeader Header { get; }

    /// <summary>
    /// The text encoding in use: the header's, or the fallback from the settings when the header's is invalid
    /// </summary>
    public SqliteEncoding TextEncoding { get; }

    public uint PageSize { get; }

    /// <summary>
    /// Reserved space at the end of every page
    /// </summary>
    public byte ReservedSpace { get; }

    /// <summary>
    /// The number of pages in the database, used to reject page numbers past the end
    /// </summary>
    public uint PageCount { get; }

    private ReaderBase(Stream stream, DatabaseHeader header, SqliteEncoding textEncoding, Encoding encoding)
    {
        _stream = stream;
        Length = stream.Length;

        Header = header;
        PageSize = header.PageSize;
        ReservedSpace = header.ReservedSpaceAtEndOfPage;
        TextEncoding = textEncoding;
        _encoding = encoding;

        // Database Size in pages adjustment
        // https://www.sqlite.org/fileformat.html#in_header_database_size
        uint expectedPages = (uint)(Length / PageSize);

        // TODO: Warn on mismatch
        PageCount = Math.Max(expectedPages, header.DatabaseSizeInPages);
    }

    /// <summary>
    /// Reads and validates the database header at the start of <paramref name="stream"/>, and returns a reader for
    /// the database's pages
    /// </summary>
    internal static ReaderBase Open(Stream stream, Sqlite3Settings settings)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        byte[] headerData = new byte[DatabaseHeader.HeaderSize];

        int read = stream.ReadFully(headerData, 0, headerData.Length);

        DatabaseHeader header = new DatabaseHeader(headerData, read);

        SqliteEncoding textEncoding = header.TextEncoding;
        if (!Enum.IsDefined(typeof(SqliteEncoding), textEncoding) && settings.FallbackEncoding.HasValue)
            textEncoding = settings.FallbackEncoding.Value;

        Encoding encoding = textEncoding switch
        {
            SqliteEncoding.UTF8 => Encoding.UTF8,
            SqliteEncoding.UTF16LE => Encoding.Unicode,
            SqliteEncoding.UTF16BE => Encoding.BigEndianUnicode,
            _ => throw new SqliteInvalidEncodingException(textEncoding)
        };

        return new ReaderBase(stream, header, textEncoding, encoding);
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    internal void CheckSize(uint sizeWanted, bool throwException = true)
    {
        if (!throwException)
            return;

        long dataLeft = Length - _stream.Position;
        if (dataLeft < sizeWanted)
        {
            throw new SqliteCorruptException("the file is shorter than the structures in it say", structure: "file length")
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
            throw new SqliteCorruptException("unable to seek to a position the file refers to", structure: "file length")
            {
                Data = { { nameof(position), position } }
            };
    }

    internal void SeekPage(uint page, uint offset = 0)
    {
        if (page == 0 || (PageCount > 0 && page > PageCount))
            throw new SqliteCorruptException($"page number {page} is outside the database (1..{PageCount})", page, "page number");

        // Note: Pages are 1-indexed
        // Widen before multiplying: uint * ushort is computed in 32 bits and wraps for offsets past 4 GiB
        ulong position = (ulong)(page - 1) * PageSize;
        position += offset;

        SetPositionAndCheckSize(position, (uint)(PageSize - offset));
    }

    /// <summary>
    /// Reads a whole page into a buffer owned by this reader. The buffer is overwritten by the next call, so callers
    /// must be done with it before reading another page.
    /// </summary>
    internal byte[] ReadPage(uint page)
    {
        SeekPage(page);

        if (_pageBuffer == null || _pageBuffer.Length != PageSize)
            _pageBuffer = new byte[PageSize];

        _pageBufferPage = 0;
        if (Read(_pageBuffer, 0, (int)PageSize) != PageSize)
            throw new EndOfStreamException();

        _pageBufferPage = page;
        return _pageBuffer;
    }

    /// <summary>
    /// Copies <paramref name="count"/> bytes from <paramref name="offset"/> on <paramref name="page"/> into
    /// <paramref name="buffer"/>. The bytes come from the page buffer, which is filled when it holds another page, so
    /// the rows of a leaf page cost one read of the page and no seek and read of the stream per row.
    /// </summary>
    internal void ReadFromPage(uint page, uint offset, byte[] buffer, int bufferOffset, int count)
    {
        if (offset + (ulong)count > PageSize)
        {
            SeekPage(page, offset);
            if (Read(buffer, bufferOffset, count) != count)
                throw new EndOfStreamException();
            return;
        }

        byte[] data = page == _pageBufferPage && _pageBuffer != null ? _pageBuffer : ReadPage(page);
        Buffer.BlockCopy(data, (int)offset, buffer, bufferOffset, count);
    }

    /// <summary>
    /// Reads a big-endian 32-bit integer from <paramref name="page"/>, through the page buffer.
    /// </summary>
    internal uint ReadUInt32FromPage(uint page, uint offset)
    {
        ReadFromPage(page, offset, _uint32Buffer, 0, 4);

        int pos = 0;
        return ByteArrayReader.ReadUInt32(_uint32Buffer, 4, ref pos);
    }

    public uint ReadUInt32()
    {
        int read = _stream.ReadFully(_uint32Buffer, 0, 4);

        int pos = 0;
        return ByteArrayReader.ReadUInt32(_uint32Buffer, read, ref pos);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        return _stream.ReadFully(buffer, offset, count);
    }

    internal string DecodeString(byte[] buffer, int offset, int count)
    {
        return _encoding.GetString(buffer, offset, count);
    }
}
