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

    /// <summary>
    /// The caller's stream when <see cref="_stream"/> stands in for it (a 0-byte file), so it is still disposed
    /// </summary>
    private readonly Stream? _source;
    private readonly Encoding _encoding;

    /// <summary>
    /// The buffer <see cref="ReadPage(uint)"/> reads into, for pages that are done with before the next is read
    /// </summary>
    private byte[]? _pageBuffer;

    /// <summary>
    /// Set once Lenient has reported the partial last page (HDR-17), so it is reported once, not on every read of it
    /// </summary>
    private bool _partialPageReported;

    public DatabaseHeader Header { get; }

    /// <summary>
    /// The settings the database was opened with, which the consistency checks consult
    /// </summary>
    public Sqlite3Settings Settings { get; }

    /// <summary>
    /// The text encoding in use: the header's, read as SQLite reads it, or the fallback from the settings when the
    /// header's field is 0
    /// </summary>
    public SqliteEncoding TextEncoding { get; }

    public uint PageSize { get; }

    /// <summary>
    /// The page that holds byte offset 0x40000000 (SQLite's PENDING_BYTE), which SQLite never uses
    /// </summary>
    private readonly uint _lockBytePage;

    /// <summary>
    /// Reserved space at the end of every page
    /// </summary>
    public byte ReservedSpace { get; }

    /// <summary>
    /// The number of pages in the database, used to reject page numbers past the end. From the header when its size is
    /// valid, otherwise from the file length, as in SQLite.
    /// </summary>
    public uint PageCount { get; }

    private ReaderBase(Stream stream, Stream? source, DatabaseHeader header, Sqlite3Settings settings, SqliteEncoding textEncoding, Encoding encoding)
    {
        _stream = stream;
        _source = source;
        Length = stream.Length;

        Header = header;
        Settings = settings;
        PageSize = header.PageSize;
        _lockBytePage = 0x40000000 / PageSize + 1;
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
        // as zeros (SQLITE_IOERR_SHORT_READ handling in os_unix.c unixRead / os_win.c winRead). ReadPage does the same in
        // Lenient, and fails that page in Strict.
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

        // HDR-24, SCHEMA-02, checked after the page count as SQLite checks it in sqlite3InitOne, after lockBtree: the
        // schema format number's low byte, where 0 means 1, must be at most 4. SQLite fails the
        // schema load with "unsupported file format" (SQLITE_ERROR, not SQLITE_CORRUPT). sqlite3InitOne in prepare.c:
        //   pDb->pSchema->file_format = (u8)meta[BTREE_FILE_FORMAT-1];
        //   if( pDb->pSchema->file_format==0 ){
        //     pDb->pSchema->file_format = 1;
        //   }
        //   if( pDb->pSchema->file_format>SQLITE_MAX_FILE_FORMAT ){
        //     sqlite3SetString(pzErrMsg, db, "unsupported file format");
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L340 (sqlite3InitOne)
        // Formats 1 to 4 only add features to what 1 can store, and this reader reads them all the same, so Lenient
        // reads a higher number on.
        Checks.Relaxable((byte)header.SchemaFormat <= 4, settings, "HDR-24",
            $"unsupported schema format {(byte)header.SchemaFormat} (1 to 4)", structure: "database header");
    }

    /// <summary>
    /// Reads and validates the database header at the start of <paramref name="stream"/>, and returns a reader for
    /// the database's pages
    /// </summary>
    internal static ReaderBase Open(Stream stream, Sqlite3Settings settings)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        // A caller error, so it fails for every file, not only for those whose encoding field is 0
        if (settings.FallbackEncoding is { } fallback && fallback is not (SqliteEncoding.UTF8 or SqliteEncoding.UTF16LE or SqliteEncoding.UTF16BE))
            throw new SqliteInvalidEncodingException(fallback);

        byte[] headerData = new byte[DatabaseHeader.HeaderSize];

        int read = stream.ReadFully(headerData, 0, headerData.Length);

        // HDR-01, PAGE-17: a 0-byte file is a valid empty database. SQLite skips every header check when the file has no
        // pages, and a cursor on the schema's root page 1 is then empty. lockBtree and btreeCursor in btree.c:
        //   if( nPage>0 ){
        //     ...
        //   if( iTable<=1 ){
        //     if( iTable<1 ){
        //       return SQLITE_CORRUPT_BKPT;
        //     }else if( btreePagecount(pBt)==0 ){
        //       assert( wrFlag==0 );
        //       iTable = 0;
        //     }
        //   }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3306 (lockBtree)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L4719 (btreeCursor)
        // This reader stands in the database SQLite would create in that file: one empty schema page with the default
        // page size, and an encoding field of 0, so the encoding is the default (or FallbackEncoding) as in SQLite.
        Stream? source = null;
        // The file's length decides, as SQLite's page count does, so a stream handed over at its end is not mistaken for
        // an empty file
        if (read == 0 && stream.Length == 0)
        {
            source = stream;
            stream = new MemoryStream(EmptyDatabase(), false);
            read = stream.ReadFully(headerData, 0, headerData.Length);
        }

        DatabaseHeader header = new DatabaseHeader(headerData, read, settings);

        // HDR-30, REC-40, SCHEMA-01: SQLite never rejects the text encoding field. A non-zero value is masked to its low
        // two bits, where 0 means UTF-8; a field of 0 leaves the connection's default encoding, which is UTF-8 unless
        // configured (here: FallbackEncoding). sqlite3InitOne in prepare.c:
        //   if( meta[BTREE_TEXT_ENCODING-1] ){
        //     ...
        //     encoding = (u8)meta[BTREE_TEXT_ENCODING-1] & 3;
        //     if( encoding==0 ) encoding = SQLITE_UTF8;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L300
        uint rawEncoding = (uint)header.TextEncoding;
        SqliteEncoding textEncoding;
        if (rawEncoding == 0)
            textEncoding = settings.FallbackEncoding ?? SqliteEncoding.UTF8;
        else if ((rawEncoding & 3) == 0)
            textEncoding = SqliteEncoding.UTF8;
        else
            textEncoding = (SqliteEncoding)(rawEncoding & 3);

        Encoding encoding = textEncoding switch
        {
            SqliteEncoding.UTF8 => Encoding.UTF8,
            SqliteEncoding.UTF16LE => Encoding.Unicode,
            SqliteEncoding.UTF16BE => Encoding.BigEndianUnicode,
            _ => throw new InvalidOperationException($"unexpected encoding {textEncoding}")
        };

        return new ReaderBase(stream, source, header, settings, textEncoding, encoding);
    }

    /// <summary>
    /// A database of one page, 4096 bytes (SQLite's default page size), whose schema table is an empty leaf page
    /// </summary>
    private static byte[] EmptyDatabase()
    {
        const int pageSize = 4096;
        byte[] data = new byte[pageSize];

        Encoding.UTF8.GetBytes("SQLite format 3\0", 0, 16, data, 0);
        data[16] = pageSize >> 8;
        data[18] = 1; // write version: legacy
        data[19] = 1; // read version: legacy
        data[21] = 64; // payload fractions
        data[22] = 32;
        data[23] = 32;
        data[31] = 1; // database size in pages, valid since the version-valid-for number (0) equals the change counter (0)
        data[47] = 4; // schema format

        data[100] = 0x0D; // leaf table b-tree page, no freeblocks, no cells
        data[105] = pageSize >> 8; // cell content area starts at the end of the page

        return data;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _source?.Dispose();
    }

    internal void CheckSize(uint sizeWanted, bool throwException = true)
    {
        if (!throwException)
            return;

        // HDR-17: the file ends inside a structure that the file refers to. ReadPage handles the partial last page.
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

    internal void SeekPage(uint page, uint offset = 0, bool checkSize = true)
    {
        // PAGE-15: page 0 does not exist, and PAGE-14: pages above the page count are not part of the database.
        // getPageNormal in pager.c, and getAndInitPage in btree.c:
        //   if( pgno==0 ) return SQLITE_CORRUPT_BKPT;
        //   if( pgno>btreePagecount(pBt) ){
        //     *ppPage = 0;
        //     return SQLITE_CORRUPT_BKPT;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5597 (getPageNormal)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L2390 (getAndInitPage)
        Checks.Strict(page != 0, "PAGE-15", $"page number 0 is outside the database (1..{PageCount})", page, "page number");
        Checks.Strict(PageCount == 0 || page <= PageCount, "PAGE-14", $"page number {page} is outside the database (1..{PageCount})", page, "page number");

        // HDR-20, INTEG-03: the lock-byte page, which holds byte offset 0x40000000 (PENDING_BYTE) in a file over 1 GiB,
        // is never part of the database, so a reference to it is corrupt in every mode. getPageNormal in pager.c:
        //   if( pgno==PAGER_SJ_PGNO(pPager) ){
        //     rc = SQLITE_CORRUPT_BKPT;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5628 (getPageNormal)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3858 (lckPgno = PENDING_BYTE/pageSize + 1)
        Checks.Strict(page != _lockBytePage, "HDR-20", $"page {page} is the lock-byte page, which holds no data", page, "page number");

        // Note: Pages are 1-indexed
        // Widen before multiplying: uint * ushort is computed in 32 bits and wraps for offsets past 4 GiB
        ulong position = (ulong)(page - 1) * PageSize;
        position += offset;

        SetPositionAndCheckSize(position, (uint)(PageSize - offset), checkSize);
    }

    /// <summary>
    /// Reads a whole page into a buffer owned by this reader. The buffer is overwritten by the next call, so callers
    /// must be done with it before reading another page.
    /// </summary>
    internal byte[] ReadPage(uint page)
    {
        ReadPage(page, PageBuffer);
        return PageBuffer;
    }

    /// <summary>
    /// The buffer <see cref="ReadPage(uint)"/> reads into. Other readers of short-lived pages may read into it too, on
    /// the same terms.
    /// </summary>
    internal byte[] PageBuffer => _pageBuffer ??= new byte[PageSize];

    /// <summary>
    /// True when <paramref name="data"/> is <see cref="PageBuffer"/>, which is then overwritten by the next page read
    /// </summary>
    internal bool IsPageBuffer(byte[] data) => data == _pageBuffer;

    /// <summary>
    /// Reads a whole page into <paramref name="buffer"/>, which holds at least <see cref="PageSize"/> bytes
    /// </summary>
    internal void ReadPage(uint page, byte[] buffer)
    {
        SeekPage(page, checkSize: false);

        int read = Read(buffer, 0, (int)PageSize);
        if (read != PageSize)
        {
            // HDR-17: the file ends inside its last page. SQLite counts that page (pagerPagecount rounds up) and reads the
            // missing bytes as zeros, since a short read is not an error. readDbPage in pager.c:
            //   rc = sqlite3OsRead(pPager->fd, pPg->pData, pPager->pageSize, iOffset);
            //   if( rc==SQLITE_IOERR_SHORT_READ ){
            //     rc = SQLITE_OK;
            //   }
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3088 (readDbPage)
            // Those zeros are not the file's data, so SQLite then returns damaged values without an error. Strict is
            // stricter than SQLite here and fails; Lenient reads the zeros as SQLite does.
            if (!_partialPageReported)
            {
                Checks.Relaxable(false, Settings, "HDR-17",
                    $"page {page} is cut short by the end of the file ({read} of {PageSize} bytes)", page, "file length");
                _partialPageReported = true;
            }

            Array.Clear(buffer, read, (int)PageSize - read);
        }
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
