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
    /// The settings the database was opened with, which the consistency checks consult
    /// </summary>
    public Sqlite3Settings Settings { get; }

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

    private ReaderBase(Stream stream, DatabaseHeader header, Sqlite3Settings settings, SqliteEncoding textEncoding, Encoding encoding)
    {
        _stream = stream;
        Length = stream.Length;

        Header = header;
        Settings = settings;
        PageSize = header.PageSize;
        ReservedSpace = header.ReservedSpaceAtEndOfPage;
        TextEncoding = textEncoding;
        _encoding = encoding;

        // https://www.sqlite.org/fileformat.html#in_header_database_size
        // The in-header size (offset 28) is valid when it is not 0 and the version-valid-for number (offset 92) equals the
        // change counter (offset 24). SQLite then trusts it over the file length (pages after it are not part of the
        // database, as after a rollback that did not truncate the file), and rejects a size larger than the file.
        // Otherwise the file length decides. This is lockBtree in btree.c:
        //   nPage = get4byte(28+(u8*)pPage1->aData);
        //   sqlite3PagerPagecount(pBt->pPager, (int*)&nPageFile);
        //   if( nPage==0 || memcmp(24+(u8*)pPage1->aData, 92+(u8*)pPage1->aData,4)!=0 ){
        //     nPage = nPageFile;
        //   }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3298 (lockBtree)
        //
        // The file's page count rounds a partial last page up, as pagerPagecount in pager.c does
        // ("nPage = (Pgno)((n+pPager->pageSize-1) / pPager->pageSize)"); SQLite then reads the missing bytes of that page
        // as zeros (SQLITE_IOERR_SHORT_READ handling in os_unix.c unixRead / os_win.c winRead), which this reader does not.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3325 (pagerPagecount)
        uint filePages = (uint)Math.Min(uint.MaxValue, (Length + PageSize - 1) / PageSize);
        bool headerSizeValid = header.DatabaseSizeInPages != 0 && header.VersionValidFor == header.ChangeCounter;

        // HDR-14: a valid in-header size larger than the file is corrupt, unless writable_schema is on (which this reader
        // has no equivalent of). lockBtree in btree.c:
        //   if( nPage>nPageFile ){
        //     if( sqlite3WritableSchema(pBt->db)==0 ){
        //       rc = SQLITE_CORRUPT_BKPT;
        // The pages that do exist are intact in such a file (truncated, or a wrong header), so Lenient uses the file's
        // count instead, as SQLite does with writable_schema.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3405 (lockBtree)
        PageCount = headerSizeValid && Checks.Relaxable(header.DatabaseSizeInPages <= filePages, settings, "HDR-14",
                $"the header says the database has {header.DatabaseSizeInPages} pages, but the file holds {filePages}",
                structure: "file length")
            ? header.DatabaseSizeInPages
            : filePages;
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

        DatabaseHeader header = new DatabaseHeader(headerData, read, settings);

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

        return new ReaderBase(stream, header, settings, textEncoding, encoding);
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    internal void CheckSize(uint sizeWanted, bool throwException = true)
    {
        if (!throwException)
            return;

        // HDR-17: the file ends inside a structure that the file refers to. PAGE-14 keeps page numbers within the file's
        // page count, so this fails only inside a partial last page. SQLite zero-fills that page instead (HDR-17 is
        // tolerated there); this reader is stricter until part 1.7 of #58 settles it.
        long dataLeft = Length - _stream.Position;
        Checks.Strict(dataLeft >= sizeWanted, "HDR-17",
            $"the file is shorter than the structures in it say ({sizeWanted} bytes wanted at offset {_stream.Position}, the file is {Length} bytes)",
            structure: "file length");
    }

    internal void SetPositionAndCheckSize(ulong position, uint sizeWanted, bool throwException = true)
    {
        SetPosition(position);

        CheckSize(sizeWanted, throwException);
    }

    internal void SetPosition(ulong position)
    {
        ulong newPosition = (ulong)_stream.Seek((long)position, SeekOrigin.Begin);

        Checks.Strict(newPosition == position, "HDR-17", $"unable to seek to offset {position}, which the file refers to",
            structure: "file length");
    }

    internal void SeekPage(uint page, uint offset = 0)
    {
        // PAGE-15: page 0 does not exist, and PAGE-14: pages above the page count are not part of the database.
        // getPageNormal in pager.c, and getAndInitPage in btree.c:
        //   if( pgno>btreePagecount(pBt) ){ rc = SQLITE_CORRUPT_BKPT; goto getAndInitPage_error1; }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5597
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L2390
        Checks.Strict(page != 0, "PAGE-15", $"page number 0 is outside the database (1..{PageCount})", page, "page number");
        Checks.Strict(PageCount == 0 || page <= PageCount, "PAGE-14", $"page number {page} is outside the database (1..{PageCount})", page, "page number");

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
    /// <remarks>
    /// This is a one-page version of what SQLite's pager does: the b-tree layer never reads cell content from the file,
    /// it gets the whole page from the page cache (btreeGetPage / sqlite3PagerGet in btree.c and pager.c) and
    /// accessPayload copies the local part of a payload out of that page before walking the overflow chain, each
    /// overflow page again fetched whole through the cache.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5125 (accessPayload), https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5772
    /// </remarks>
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
