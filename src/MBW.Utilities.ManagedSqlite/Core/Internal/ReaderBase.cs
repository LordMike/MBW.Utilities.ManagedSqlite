using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

/// <summary>
/// Reads pages of an opened database. Create it with <see cref="Open"/>, which reads the database header first, so
/// the page size, encoding and page count are known and fixed for the reader's lifetime. Pages are cut from an
/// <see cref="IFileView"/>, the bytes of the file as SQLite's pager would see them: the main file, with a hot rollback
/// journal played back over it. A page that the <c>-wal</c> file holds a committed copy of is read from there instead.
/// </summary>
internal sealed class ReaderBase : IDisposable
{
    public long Length => _file.Length;

    private readonly IFileView _file;

    private readonly WalIndex? _wal;

    /// <summary>
    /// Every file the reader owns and disposes: the caller's main file, journal and WAL, and the stand-in for a 0-byte
    /// file
    /// </summary>
    private readonly IDisposable[] _owned;
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

    private ReaderBase(IFileView file, WalIndex? wal, IDisposable[] owned, DatabaseHeader header, Sqlite3Settings settings, SqliteEncoding textEncoding, Encoding encoding)
    {
        _file = file;
        _wal = wal;
        _owned = owned;

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
        //
        // WALJ-26: with a WAL, the database size is the one its last committed transaction recorded, not the file's.
        // pagerPagecount in pager.c:
        //   nPage = sqlite3WalDbsize(pPager->pWal);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3325 (pagerPagecount)
        uint filePages = wal != null
            ? wal.PageCount
            : (uint)Math.Min(uint.MaxValue, (Length + PageSize - 1) / PageSize);
        bool headerSizeValid = header.DatabaseSizeInPages != 0 && header.VersionValidFor == header.ChangeCounter;

        // HDR-14: a valid in-header size larger than the file is corrupt, unless writable_schema is on (which this reader
        // has no equivalent of). lockBtree in btree.c:
        //   if( nPage>nPageFile ){
        //     if( sqlite3WritableSchema(pBt->db)==0 ){
        //       rc = SQLITE_CORRUPT_BKPT;
        // The pages that do exist are intact in such a file (truncated, or a wrong header), so Lenient uses the file's
        // count instead, as SQLite does with writable_schema.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3405 (lockBtree)
        // WALJ-26 is the same check against the WAL's page count, and Relaxable for the same reason.
        bool sizeFits = header.DatabaseSizeInPages <= filePages;
        PageCount = headerSizeValid && (wal != null
                ? Checks.Relaxable(sizeFits, settings, "WALJ-26",
                    $"the header says the database has {header.DatabaseSizeInPages} pages, but the WAL's last commit holds {filePages}",
                    structure: "file length")
                : Checks.Relaxable(sizeFits, settings, "HDR-14",
                    $"the header says the database has {header.DatabaseSizeInPages} pages, but the file holds {filePages}",
                    structure: "file length"))
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
    /// Reads and validates the database header, and returns a reader for the database's pages. A hot
    /// <paramref name="journal"/> is played back over <paramref name="main"/> first, then the committed frames of
    /// <paramref name="wal"/> are laid over the result, in the order SQLite's pager does it. The reader owns all three
    /// files and disposes them, also when this method throws. <paramref name="superJournalExists"/> looks up a
    /// super-journal the journal names, or is null when there is no file system to look in.
    /// </summary>
    internal static ReaderBase Open(IFileView main, IFileView? journal, IFileView? wal, Sqlite3Settings settings, Func<string, bool>? superJournalExists = null)
    {
        List<IDisposable> owned = new List<IDisposable> { main };
        if (journal != null)
            owned.Add(journal);
        if (wal != null)
            owned.Add(wal);

        try
        {
            return OpenCore(main, journal, wal, owned, settings, superJournalExists);
        }
        catch
        {
            foreach (IDisposable disposable in owned)
                DisposeQuietly(disposable);
            throw;
        }
    }

    private static ReaderBase OpenCore(IFileView main, IFileView? journal, IFileView? walFile, List<IDisposable> owned, Sqlite3Settings settings, Func<string, bool>? superJournalExists)
    {
        // A caller error, so it fails for every file, not only for those whose encoding field is 0
        if (settings.FallbackEncoding is { } fallback && fallback is not (SqliteEncoding.UTF8 or SqliteEncoding.UTF16LE or SqliteEncoding.UTF16BE))
            throw new SqliteInvalidEncodingException(fallback);

        IFileView file = main;

        // WALJ-39, WALJ-42: a hot journal is played back (where a read-only SQLite connection fails with
        // SQLITE_READONLY_ROLLBACK, this reader does what a writable one does, in memory), then the WAL is opened on the
        // result. sqlite3PagerSharedLock in pager.c,
        // and pagerOpenWalIfPresent, which it calls after the playback:
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5280 (sqlite3PagerSharedLock)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3385 (pagerOpenWalIfPresent)
        if (journal != null)
            file = RollbackJournalView.Apply(main, journal, InitialPageSize(main), superJournalExists, settings);

        // WALJ-01, WALJ-02: a WAL is used whatever the header's read version says, but not next to an empty database,
        // where SQLite deletes it. pagerOpenWalIfPresent in pager.c:
        //   rc = pagerPagecount(pPager, &nPage);
        //   if( nPage==0 ){
        //     rc = sqlite3OsDelete(pPager->pVfs, pPager->zWal, 0);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3399 (pagerOpenWalIfPresent)
        WalIndex? wal = walFile != null && file.Length > 0 ? WalIndex.Read(walFile) : null;

        // WALJ-28: a WAL whose page size differs from the database's can only belong to another database. SQLite reads
        // min(database page size, WAL page size) bytes of each frame, leaving the rest of the page undefined, and fails
        // only on a write or checkpoint. That cannot be reproduced, so Strict fails, and Lenient ignores the WAL and
        // reads the file alone. The WAL is compared with the page size in the main file's header, before the header is
        // read from page 1 in the WAL, which normally carries the WAL's own page size. When the main file's page size
        // is not valid, there is nothing to compare with yet; SQLite then takes the page size from page 1 in the WAL
        // (lockBtree resets the pager's page size to it), and the check below compares that. sqlite3WalReadFrame in
        // wal.c:
        //   sz = pWal->hdr.szPage;
        //   sz = (sz&0xfe00) + ((sz&0x0001)<<16);
        //   testcase( sz<=32768 );
        //   testcase( sz>=65536 );
        //   iOffset = walFrameOffset(iRead, sz) + WAL_FRAME_HDRSIZE;
        //   return sqlite3OsRead(pWal->pWalFd, pOut, (nOut>sz ? sz : nOut), iOffset);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L3666 (sqlite3WalReadFrame)
        if (wal != null && HeaderPageSize(file) is { } filePageSize && !Checks.Relaxable(wal.PageSize == filePageSize, settings, "WALJ-28",
                $"the WAL page size {wal.PageSize} differs from the database page size {filePageSize}", structure: "WAL header"))
            wal = null;

        // WALJ-25, WALJ-27, WALJ-61: every header check runs on the newest committed page 1, after any journal playback,
        // and it also gives the page size. The WAL holds that page 1 when a transaction changed it; the main file's page
        // 1 may then be stale. lockBtree reads page 1 through the pager once the WAL is open:
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3341 (lockBtree)
        byte[] headerData = new byte[DatabaseHeader.HeaderSize];

        int read = wal != null && wal.TryReadPage(1, headerData, headerData.Length)
            ? headerData.Length
            : file.Read(0, headerData, 0, headerData.Length);

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
        // A hot journal that rolls back the transaction that created the database leaves it empty too.
        if (file.Length == 0)
        {
            file = new StreamFileView(new MemoryStream(EmptyDatabase(), false), false);
            owned.Add(file);
            read = file.Read(0, headerData, 0, headerData.Length);
        }

        DatabaseHeader header = new DatabaseHeader(headerData, read, settings);

        // WALJ-28 again, on the page size the database is read with: a page 1 in the WAL that gives another page size
        // than the WAL's own leaves SQLite reading part of each page too
        if (wal != null && !Checks.Relaxable(wal.PageSize == header.PageSize, settings, "WALJ-28",
                $"the WAL page size {wal.PageSize} differs from the database page size {header.PageSize}", structure: "WAL header"))
        {
            wal = null;
            read = file.Read(0, headerData, 0, headerData.Length);
            header = new DatabaseHeader(headerData, read, settings);
        }

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

        return new ReaderBase(file, wal, owned.ToArray(), header, settings, textEncoding, encoding);
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

    /// <summary>
    /// The page size the pager starts with, from the raw header bytes of the main file, before any journal playback.
    /// An invalid value falls back to the default of 4096, as SQLite does. sqlite3BtreeOpen in btree.c:
    ///   pBt->pageSize = (zDbHeader[16]&lt;&lt;8) | (zDbHeader[17]&lt;&lt;16);
    ///   if( pBt->pageSize&lt;512 || pBt->pageSize>SQLITE_MAX_PAGE_SIZE || ((pBt->pageSize-1)&amp;pBt->pageSize)!=0 ){
    ///     pBt->pageSize = 0;
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L2707 (sqlite3BtreeOpen)
    /// </summary>
    private static uint InitialPageSize(IFileView main) => HeaderPageSize(main) ?? 4096;

    /// <summary>
    /// The page size in the file's header, or null when it is not a valid page size
    /// </summary>
    private static uint? HeaderPageSize(IFileView main)
    {
        // SQLite reads the 100-byte header into a zeroed buffer; a short read keeps the bytes it got and zero-fills the
        // rest, as the zeroed buffer here does
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3943 (sqlite3PagerReadFileheader)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/os_unix.c#L3575-L3577 (unixRead)
        byte[] bytes = new byte[2];
        main.Read(16, bytes, 0, 2);

        uint pageSize = (uint)(bytes[0] << 8 | bytes[1] << 16);
        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
            return null;

        return pageSize;
    }

    public void Dispose()
    {
        // Every file is disposed even when one throws; the first error is rethrown
        Exception? error = null;
        foreach (IDisposable disposable in _owned)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception e)
            {
                error ??= e;
            }
        }

        if (error != null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch
        {
            // The open's own error is the one to report
        }
    }

    /// <summary>
    /// Checks that <paramref name="page"/> is a page of the database that may be read
    /// </summary>
    private void CheckPage(uint page)
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
        CheckPage(page);

        // Note: Pages are 1-indexed
        // Widen before multiplying: uint * uint is computed in 32 bits and wraps for offsets past 4 GiB
        long position = (long)(page - 1) * PageSize;

        if (_wal != null)
        {
            if (_wal.TryReadPage(page, buffer, (int)PageSize))
                return;

            // WALJ-29: a committed transaction can grow the database without the file growing; the pages it did not
            // write are zeros, as SQLite reads them past the end of the file. readDbPage in pager.c:
            //   rc = sqlite3OsRead(pPager->fd, pPg->pData, pPager->pageSize, iOffset);
            //   if( rc==SQLITE_IOERR_SHORT_READ ){
            //     rc = SQLITE_OK;
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3086-L3090 (readDbPage)
            if (position >= _file.Length)
            {
                Array.Clear(buffer, 0, (int)PageSize);
                return;
            }
        }

        int read = _file.Read(position, buffer, 0, (int)PageSize);
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

    internal string DecodeString(byte[] buffer, int offset, int count)
    {
        return _encoding.GetString(buffer, offset, count);
    }
}
