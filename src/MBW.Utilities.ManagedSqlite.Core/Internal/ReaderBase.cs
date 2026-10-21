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
/// Reads pages of an opened database. Create it with <see cref="Open(IFileView, IFileView?, IFileView?, Sqlite3Settings)"/>, which reads the database header first, so
/// the page size, encoding and page count are known and fixed for the reader's lifetime. Pages are cut from an
/// <see cref="IFileView"/>, the bytes of the file as SQLite's pager would see them: the main file, with a hot rollback
/// journal played back over it. A page that the <c>-wal</c> file holds a committed copy of is read from there instead.
/// </summary>
internal sealed class ReaderBase : IDisposable
{
    private readonly IFileView _file;
    private readonly WalIndex? _wal;
    private readonly IDisposable[] _owned;
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
    /// The number of pages in the database, used to reject page numbers past the end. From the header when its size is
    /// valid, otherwise from the file length, as in SQLite.
    /// </summary>
    public uint PageCount { get; }

    private ReaderBase(IFileView file, WalIndex? wal, IDisposable[] owned, DatabaseHeader header, SqliteEncoding textEncoding, Encoding encoding)
    {
        _file = file;
        _wal = wal;
        _owned = owned;

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
        //
        // With a WAL, the database size is the one its last committed transaction recorded, not the file's (WALJ-26):
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3325 (pagerPagecount)
        //   nPage = sqlite3WalDbsize(pPager->pWal);
        //   if( nPage==0 && ... ){ ... sqlite3OsFileSize(pPager->fd, &n) ...
        uint filePages = wal != null
            ? wal.PageCount
            : (uint)Math.Min(uint.MaxValue, (file.Length + PageSize - 1) / PageSize);
        bool headerSizeValid = header.DatabaseSizeInPages != 0 && header.VersionValidFor == header.ChangeCounter;

        // The pages that do exist are intact in such a file (truncated, or a wrong header), so this is a strict check that a
        // Lenient validation mode may replace by using the file's count; see issue #58.
        if (headerSizeValid && header.DatabaseSizeInPages > filePages)
            throw new SqliteCorruptException($"the header says the database has {header.DatabaseSizeInPages} pages, but the file holds {filePages}", structure: "file length");

        PageCount = headerSizeValid ? header.DatabaseSizeInPages : filePages;
    }

    /// <summary>
    /// Reads and validates the database header, and returns a reader for the database's pages. A hot
    /// <paramref name="journal"/> is played back over <paramref name="main"/> first, then the committed frames of
    /// <paramref name="wal"/> are laid over the result, in the order SQLite's pager does it. The reader owns all three
    /// files and disposes them, also when this method throws.
    /// </summary>
    internal static ReaderBase Open(IFileView main, IFileView? journal, IFileView? wal, Sqlite3Settings settings)
    {
        List<IDisposable> files = new List<IDisposable> { main };
        if (journal != null)
            files.Add(journal);
        if (wal != null)
            files.Add(wal);

        IDisposable[] owned = files.ToArray();

        try
        {
            return Open(main, journal, wal, owned, settings);
        }
        catch
        {
            foreach (IDisposable disposable in owned)
                DisposeQuietly(disposable);
            throw;
        }
    }

    private static ReaderBase Open(IFileView main, IFileView? journal, IFileView? walFile, IDisposable[] owned, Sqlite3Settings settings)
    {
        IFileView file = main;

        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5280-L5340 (sqlite3PagerSharedLock)
        // A hot journal is played back first, then the WAL is opened on the result
        // (pagerOpenWalIfPresent, https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3385).
        if (journal != null)
            file = RollbackJournalView.Apply(main, journal, InitialPageSize(main));

        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3399-L3405 (pagerOpenWalIfPresent)
        //   rc = pagerPagecount(pPager, &nPage);
        //   if( nPage==0 ){
        //     rc = sqlite3OsDelete(pPager->pVfs, pPager->zWal, 0);
        // A WAL next to an empty database is not used; SQLite deletes it.
        WalIndex? wal = walFile != null && file.Length > 0 ? WalIndex.Read(walFile) : null;

        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3341-L3352 (lockBtree)
        // Every header check runs on the newest committed page 1, which the WAL holds when a transaction changed it
        // (WALJ-25). The main file's page 1 may then be stale.
        byte[] headerData = new byte[DatabaseHeader.HeaderSize];

        int read = wal != null && wal.TryReadPage(1, headerData, headerData.Length)
            ? headerData.Length
            : file.Read(0, headerData, 0, headerData.Length);

        DatabaseHeader header = new DatabaseHeader(headerData, read);

        // WALJ-28: SQLite reads min(database page size, WAL page size) bytes of each frame, leaving the rest of the page
        // undefined, and only fails on a write or checkpoint. That cannot be reproduced, so this fails. A Lenient
        // validation mode (issue #58) may instead ignore the WAL; this becomes Checks.Relaxable once that lands.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L3666-L3672 (sqlite3WalReadFrame)
        if (wal != null && wal.PageSize != header.PageSize)
            throw new SqliteCorruptException($"the WAL page size {wal.PageSize} differs from the database page size {header.PageSize}", structure: "WAL header");

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

        return new ReaderBase(file, wal, owned, header, textEncoding, encoding);
    }

    /// <summary>
    /// The page size the pager starts with, from the raw header bytes of the main file, before any journal playback.
    /// An invalid value falls back to the default of 4096, as SQLite does.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L2707 (sqlite3BtreeOpen)
    /// <code>
    ///   pBt->pageSize = (zDbHeader[16]&lt;&lt;8) | (zDbHeader[17]&lt;&lt;16);
    ///   if( pBt->pageSize&lt;512 || pBt->pageSize>SQLITE_MAX_PAGE_SIZE || ((pBt->pageSize-1)&amp;pBt->pageSize)!=0 ){
    ///     pBt->pageSize = 0;
    /// </code>
    /// </summary>
    private static uint InitialPageSize(IFileView main)
    {
        // SQLite reads the 100-byte header into a zeroed buffer; a short read keeps the bytes it got and zero-fills the
        // rest, as the zeroed buffer here does
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3943 (sqlite3PagerReadFileheader)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/os_unix.c#L3575-L3577 (unixRead)
        byte[] bytes = new byte[2];
        main.Read(16, bytes, 0, 2);

        uint pageSize = (uint)(bytes[0] << 8 | bytes[1] << 16);
        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
            return 4096;

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
    /// Reads a whole page into a buffer owned by this reader. The buffer is overwritten by the next call, so callers
    /// must be done with it before reading another page.
    /// </summary>
    internal byte[] ReadPage(uint page)
    {
        if (page == 0 || (PageCount > 0 && page > PageCount))
            throw new SqliteCorruptException($"page number {page} is outside the database (1..{PageCount})", page, "page number");

        // Note: Pages are 1-indexed
        // Widen before multiplying: uint * uint is computed in 32 bits and wraps for offsets past 4 GiB
        long position = (long)(page - 1) * PageSize;

        if (_pageBuffer == null || _pageBuffer.Length != PageSize)
            _pageBuffer = new byte[PageSize];

        _pageBufferPage = 0;
        if (_wal != null)
        {
            if (_wal.TryReadPage(page, _pageBuffer, (int)PageSize))
            {
                _pageBufferPage = page;
                return _pageBuffer;
            }

            // A committed transaction can grow the database without the file growing; the pages it did not write are
            // zeros, as SQLite reads them past the end of the file.
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3086-L3090 (readDbPage)
            if (position >= _file.Length)
            {
                Array.Clear(_pageBuffer, 0, _pageBuffer.Length);
                _pageBufferPage = page;
                return _pageBuffer;
            }
        }

        if (_file.Length - position < PageSize)
            throw new SqliteCorruptException("the file is shorter than the structures in it say", structure: "file length")
            {
                Data =
                {
                    { "Position", position },
                    { "sizeWanted", PageSize },
                    { "SourceLength", _file.Length }
                }
            };

        if (_file.Read(position, _pageBuffer, 0, (int)PageSize) != PageSize)
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
    /// https://sqlite.org/src/file?name=src/btree.c (accessPayload), https://sqlite.org/src/file?name=src/pager.c
    /// </remarks>
    internal void ReadFromPage(uint page, uint offset, byte[] buffer, int bufferOffset, int count)
    {
        // SQLite never reads past the end of a page: accessPayload copies at most the local part of a payload, which
        // fits in the page's usable size, and fetches every overflow page whole through the pager.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5125 (accessPayload)
        if (offset + (ulong)count > PageSize)
            throw new SqliteCorruptException($"a read of {count} bytes at offset {offset} runs past the end of the page", page, "page bounds");

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

    internal string DecodeString(byte[] buffer, int offset, int count)
    {
        return _encoding.GetString(buffer, offset, count);
    }
}
