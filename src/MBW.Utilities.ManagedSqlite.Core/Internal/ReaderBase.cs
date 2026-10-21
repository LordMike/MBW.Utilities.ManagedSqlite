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
    /// The number of pages in the database, used to reject page numbers past the end. From the header when its size is
    /// valid, otherwise from the file length, as in SQLite.
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

        // https://www.sqlite.org/fileformat.html#in_header_database_size
        // The in-header size (offset 28) is valid when it is not 0 and the version-valid-for number (offset 92) equals the
        // change counter (offset 24). SQLite then trusts it over the file length (pages after it are not part of the
        // database, as after a rollback that did not truncate the file), and rejects a size larger than the file.
        // Otherwise the file length decides. This is lockBtree in btree.c:
        //   nPage = get4byte(28+page1);
        //   sqlite3PagerPagecount(pBt->pPager, &nPageFile);
        //   if( nPage==0 || memcmp(24+page1, 92+page1, 4)!=0 ) nPage = nPageFile;
        //   ...
        //   if( nPage>nPageFile ) rc = SQLITE_CORRUPT_BKPT
        // https://sqlite.org/src/file?name=src/btree.c (lockBtree)
        //
        // The file's page count rounds a partial last page up, as pagerPagecount in pager.c does
        // ("nPage = (Pgno)((n+pPager->pageSize-1) / pPager->pageSize)"); SQLite then reads the missing bytes of that page
        // as zeros (SQLITE_IOERR_SHORT_READ handling in os_unix.c unixRead / os_win.c winRead), which this reader does not.
        // https://sqlite.org/src/file?name=src/pager.c (pagerPagecount)
        uint filePages = (uint)Math.Min(uint.MaxValue, (Length + PageSize - 1) / PageSize);
        bool headerSizeValid = header.DatabaseSizeInPages != 0 && header.VersionValidFor == header.ChangeCounter;

        // The pages that do exist are intact in such a file (truncated, or a wrong header), so this is a strict check that a
        // Lenient validation mode may replace by using the file's count; see issue #58.
        if (headerSizeValid && header.DatabaseSizeInPages > filePages)
            throw new SqliteCorruptException($"the header says the database has {header.DatabaseSizeInPages} pages, but the file holds {filePages}", structure: "file length");

        PageCount = headerSizeValid ? header.DatabaseSizeInPages : filePages;
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

        if (Read(_pageBuffer, 0, (int)PageSize) != PageSize)
            throw new EndOfStreamException();

        return _pageBuffer;
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
