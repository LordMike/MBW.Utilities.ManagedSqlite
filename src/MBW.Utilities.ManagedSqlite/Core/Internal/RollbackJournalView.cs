using System;
using System.Collections.Generic;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

/// <summary>
/// The database file as it is after a hot rollback journal has been played back over it: the journal's original pages
/// replace the main file's, and the file is cut back to its size before the crashed write. Nothing is written; the
/// playback happens in this view only.
/// </summary>
/// <remarks>
/// <para>
/// A read-only SQLite connection refuses to open a database with a hot journal (SQLITE_READONLY_ROLLBACK), and a
/// writable one plays the journal back into the file first. This reader deliberately does what the writable connection
/// does, in memory, so it reads the last committed state instead of failing.
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5335 (sqlite3PagerSharedLock)
/// </para>
/// <para>
/// A journal that names a super-journal (a transaction over several attached databases) is played back only while the
/// super-journal exists, as in SQLite; when it is gone, the transaction committed (WALJ-45). See
/// <see cref="Apply"/> for when that cannot be known.
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L2885-L2896 (pager_playback)
/// </para>
/// https://www.sqlite.org/fileformat.html#the_rollback_journal
/// </remarks>
internal sealed class RollbackJournalView : IFileView
{
    // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L757 (aJournalMagic)
    private static readonly byte[] JournalMagic = { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 };

    // SQLite's own sector size for the main file, used until the journal's first header says otherwise. With
    // SQLITE_IOCAP_POWERSAFE_OVERWRITE, the default on unix and Windows, it is 512.
    // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L2791 (setSectorSize)
    private const int DefaultSectorSize = 512;

    // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L1663-L1665 (readJournalHdr), MAX_SECTOR_SIZE
    private const uint MaxSectorSize = 0x10000;

    // SQLite reads a super-journal name shorter than the VFS's mxPathname plus 1. That is 512 on unix, 1040 for the
    // default Windows VFS and 65534 for its long-path VFS. The journal may come from any of them, so the largest bound
    // is used: no other VFS writes a longer name.
    // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/os_unix.c#L180 (MAX_PATHNAME)
    // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/os_win.c#L144 (SQLITE_WIN32_MAX_PATH_BYTES)
    // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/os_win.c#L152-L153 (SQLITE_WINNT_MAX_PATH_BYTES)
    private const uint MaxSuperJournalName = 2 * 32767 + 1;

    // The lock-byte page holds the byte at offset 1 GiB, PENDING_BYTE
    // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.h#L53 (PAGER_SJ_PGNO_COMPUTED)
    private const long PendingByte = 0x40000000;

    private readonly IFileView _main;
    private readonly IFileView _journal;
    private readonly uint _pageSize;
    private readonly Dictionary<uint, long> _pages;

    public long Length { get; }

    private RollbackJournalView(IFileView main, IFileView journal, uint pageSize, Dictionary<uint, long> pages, long length)
    {
        _main = main;
        _journal = journal;
        _pageSize = pageSize;
        _pages = pages;
        Length = length;
    }

    /// <summary>
    /// Plays back <paramref name="journal"/> over <paramref name="main"/> if it is hot, and returns the result.
    /// Returns <paramref name="main"/> unchanged when the journal is not hot or its first header is not usable.
    /// <paramref name="initialPageSize"/> is the page size from the main file's header, used where the journal does
    /// not give its own. <paramref name="superJournalExists"/> tells whether a super-journal named by the journal
    /// exists; when it is null (a journal given as a stream), that cannot be known, which fails WALJ-45 in Strict and
    /// plays the journal back in Lenient. The view does not own either file.
    /// </summary>
    public static IFileView Apply(IFileView main, IFileView journal, uint initialPageSize, Func<string, bool>? superJournalExists, Sqlite3Settings settings)
    {
        // WALJ-34, WALJ-36, WALJ-37: a journal is hot when it exists, is not empty, its first byte is not 0, no connection holds a RESERVED lock,
        // and the database is not empty. A PERSIST journal after a commit has a zeroed header, and a TRUNCATE journal
        // is empty. This reader takes no locks and assumes no other connection is writing.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5180 (hasHotJournal)
        //   rc = pagerPagecount(pPager, &nPage);
        //   if( nPage==0 && !jrnlOpen ){ ... sqlite3OsDelete(pVfs, pPager->zJournal, 0);
        //   ...
        //   rc = sqlite3OsRead(pPager->jfd, (void *)&first, 1, 0);
        //   *pExists = (first!=0);
        byte[] first = new byte[1];
        if (main.Length == 0 || journal.Read(0, first, 0, 1) != 1 || first[0] == 0)
            return main;

        // WALJ-45: a journal that names a super-journal that no longer exists is not hot: the transaction over several
        // databases committed, and the journal only waits to be deleted. pager_playback in pager.c:
        //   rc = readSuperJournal(pPager->jfd, 1+(i64)pPager->pVfs->mxPathname, &zSuper);
        //   if( rc==SQLITE_OK && zSuper ){
        //     rc = sqlite3OsAccess(pVfs, zSuper, SQLITE_ACCESS_EXISTS, &res);
        //   }
        //   if( rc!=SQLITE_OK || !res ){
        //     goto end_playback;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L2891-L2896 (pager_playback)
        // A stream gives no file system to look in, so Strict cannot decide and fails. Lenient plays the journal back:
        // at worst the database reads as it was just before a transaction that did commit, never half-written.
        if (ReadSuperJournalName(journal) is { } superJournal)
        {
            if (superJournalExists != null)
            {
                if (!superJournalExists(superJournal))
                    return main;
            }
            else
            {
                Checks.Relaxable(false, settings, "WALJ-45",
                    $"the journal names the super-journal \"{superJournal}\", and only its existence decides whether the journal is played back",
                    structure: "rollback journal");
            }
        }

        long journalSize = journal.Length;
        uint pageSize = initialPageSize;
        long sectorSize = DefaultSectorSize;

        Dictionary<uint, long> pages = new Dictionary<uint, long>();
        byte[]? pageData = null;
        long length = main.Length;
        uint databaseSize = 0;
        bool truncated = false;

        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L2864 (pager_playback)
        // The journal is a series of segments, each a header padded to the sector size followed by nRec page records.
        long offset = 0;
        byte[] header = new byte[28];
        byte[] word = new byte[4];
        bool done = false;
        while (!done)
        {
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L1590 (readJournalHdr)
            // WALJ-46, WALJ-47: each header starts at the next multiple of the sector size (journalHdrOffset), and must fit in the file
            // with a whole sector, or the journal ends. A wrong magic ends it too. None of these are errors.
            //   pPager->journalOff = journalHdrOffset(pPager);
            //   if( pPager->journalOff+JOURNAL_HDR_SZ(pPager) > journalSize ){
            //     return SQLITE_DONE;
            offset = offset == 0 ? 0 : ((offset - 1) / sectorSize + 1) * sectorSize;
            if (offset + sectorSize > journalSize)
                break;

            if (journal.Read(offset, header, 0, header.Length) != header.Length)
                break;

            bool magicMatches = true;
            for (int i = 0; i < JournalMagic.Length; i++)
                magicMatches &= header[i] == JournalMagic[i];

            if (!magicMatches)
                break;

            int pos = 8;
            uint recordCount = ByteArrayReader.ReadUInt32(header, header.Length, ref pos);
            uint checksumInit = ByteArrayReader.ReadUInt32(header, header.Length, ref pos);
            uint originalSize = ByteArrayReader.ReadUInt32(header, header.Length, ref pos);

            if (offset == 0)
            {
                // WALJ-49, WALJ-50, WALJ-51: only the first header holds the sector size and page size. A page size of 0 comes from SQLite before
                // 3.5.8 and means the database's. A value out of range means the header is not to be trusted, and
                // nothing is played back.
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L1640-L1690 (readJournalHdr)
                //   if( iPageSize<512                  || iSectorSize<32
                //    || iPageSize>SQLITE_MAX_PAGE_SIZE || iSectorSize>MAX_SECTOR_SIZE
                //    || ((iPageSize-1)&iPageSize)!=0   || ((iSectorSize-1)&iSectorSize)!=0
                //   ){
                //     return SQLITE_DONE;
                uint journalSectorSize = ByteArrayReader.ReadUInt32(header, header.Length, ref pos);
                uint journalPageSize = ByteArrayReader.ReadUInt32(header, header.Length, ref pos);
                if (journalPageSize == 0)
                    journalPageSize = initialPageSize;

                if (journalPageSize < 512 || journalSectorSize < 32 || journalPageSize > 65536 || journalSectorSize > MaxSectorSize ||
                    (journalPageSize & (journalPageSize - 1)) != 0 || (journalSectorSize & (journalSectorSize - 1)) != 0)
                    break;

                // Playback uses the journal's page size and sector size from here on
                pageSize = journalPageSize;
                sectorSize = journalSectorSize;
            }

            offset += sectorSize;

            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L2924-L2927 (pager_playback)
            //   if( nRec==0xffffffff ){
            //     nRec = (int)((szJ - JOURNAL_HDR_SZ(pPager))/JOURNAL_PG_SZ(pPager));
            // WALJ-52, WALJ-53: a journal written with synchronous=OFF never updates its record count, so the file size gives it. A
            // count of 0 in a hot journal means the segment has no records (the special case for 0 is not for hot
            // journals).
            long records = recordCount == 0xffffffff ? (journalSize - sectorSize) / (pageSize + 8) : recordCount;

            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L2951-L2959 (pager_playback)
            //   if( pPager->journalOff==JOURNAL_HDR_SZ(pPager) ){
            //     rc = pager_truncate(pPager, mxPg);
            //     pPager->dbSize = mxPg;
            // WALJ-54: the first header cuts the file back to the size it had before the transaction, even when no record
            // follows. The page count in later headers is not used.
            if (!truncated)
            {
                truncated = true;
                databaseSize = originalSize;
                length = Truncate(length, originalSize, pageSize);
            }

            for (long r = 0; r < records; r++)
            {
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L2298 (pager_playback_one_page)
                // WALJ-55 to WALJ-59: a record is the page number, the original page and a checksum. A short read ends the playback
                // without an error, as does page 0, the lock-byte page or a wrong checksum. Pages past the original
                // size are skipped.
                //   if( pgno==0 || pgno==PAGER_SJ_PGNO(pPager) ){
                //     return SQLITE_DONE;
                //   }
                //   if( pgno>(Pgno)pPager->dbSize || ... ){
                //     return SQLITE_OK;
                //   }
                //   ...
                //   if( !isSavepnt && pager_cksum(pPager, (u8*)aData)!=cksum ){
                //     return SQLITE_DONE;
                long recordOffset = offset;
                offset += pageSize + 8;

                if (recordOffset + pageSize + 8 > journalSize)
                {
                    done = true;
                    break;
                }

                journal.Read(recordOffset, word, 0, 4);
                int wordPos = 0;
                uint page = ByteArrayReader.ReadUInt32(word, 4, ref wordPos);

                if (page == 0 || page == PendingByte / pageSize + 1)
                {
                    done = true;
                    break;
                }

                if (page > databaseSize)
                    continue;

                journal.Read(recordOffset + 4 + pageSize, word, 0, 4);
                wordPos = 0;
                uint checksum = ByteArrayReader.ReadUInt32(word, 4, ref wordPos);

                if (pageData == null || pageData.Length != pageSize)
                    pageData = new byte[pageSize];

                if (Checksum(journal, recordOffset + 4, pageData, checksumInit) != checksum)
                {
                    done = true;
                    break;
                }

                // WALJ-60: records are played back in journal order, so a later copy of a page wins, as on disk
                pages[page] = recordOffset + 4;
                length = Math.Max(length, (long)page * pageSize);
            }
        }

        if (!truncated)
            return main;

        return new RollbackJournalView(main, journal, pageSize, pages, length);
    }

    /// <summary>
    /// WALJ-43, WALJ-44: the super-journal name at the end of the journal, or null when there is none. The record is the
    /// name, its length, a checksum of its bytes and the journal magic; any of them out of place means there is none,
    /// and so does a checksum that does not match, which SQLite reads as "definitely roll back".
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L1303 (readSuperJournal)
    /// <code>
    ///   if( SQLITE_OK!=(rc = sqlite3OsFileSize(pJrnl, &amp;szJ))
    ///    || szJ&lt;16
    ///    || SQLITE_OK!=(rc = read32bits(pJrnl, szJ-16, &amp;len))
    ///    || len>=nSuper
    ///    || len>szJ-16
    ///    || len==0
    ///    || SQLITE_OK!=(rc = read32bits(pJrnl, szJ-12, &amp;cksum))
    ///    || SQLITE_OK!=(rc = sqlite3OsRead(pJrnl, aMagic, 8, szJ-8))
    ///    || memcmp(aMagic, aJournalMagic, 8)
    ///   ){
    ///     return rc;
    ///   }
    ///   ...
    ///       for(u=0; u&lt;len; u++){
    ///         cksum -= zOut[u];
    ///       }
    ///     }
    ///     if( rc!=SQLITE_OK || cksum || zOut[0]==0 ){
    /// </code>
    /// </summary>
    private static string? ReadSuperJournalName(IFileView journal)
    {
        long size = journal.Length;
        if (size < 16)
            return null;

        byte[] tail = new byte[16];
        if (journal.Read(size - 16, tail, 0, 16) != 16)
            return null;

        int pos = 0;
        uint length = ByteArrayReader.ReadUInt32(tail, 16, ref pos);
        uint checksum = ByteArrayReader.ReadUInt32(tail, 16, ref pos);
        for (int i = 0; i < JournalMagic.Length; i++)
        {
            if (tail[8 + i] != JournalMagic[i])
                return null;
        }

        if (length == 0 || length >= MaxSuperJournalName || length > size - 16)
            return null;

        byte[] name = new byte[length];
        if (journal.Read(size - 16 - length, name, 0, name.Length) != name.Length)
            return null;

        // The name is summed as C chars, which are signed on the platforms SQLite is built for
        unchecked
        {
            foreach (byte b in name)
                checksum -= (uint)(sbyte)b;
        }

        if (checksum != 0 || name[0] == 0)
            return null;

        // SQLite writes the name without a terminator, but looks it up as a C string, so a NUL within it ends it
        int end = Array.IndexOf(name, (byte)0);
        return Encoding.UTF8.GetString(name, 0, end < 0 ? name.Length : end);
    }

    /// <summary>
    /// The file length after SQLite's pager_truncate: cut back when longer, extended with zeros when at least a page
    /// shorter, left alone otherwise.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L2718 (pager_truncate)
    /// </summary>
    private static long Truncate(long length, uint pages, uint pageSize)
    {
        long newSize = (long)pages * pageSize;
        if (length > newSize)
            return newSize;

        if (length + pageSize <= newSize)
            return newSize;

        return length;
    }

    /// <summary>
    /// The journal record checksum: the header's nonce plus every 200th byte of the page, from the end.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L2251 (pager_cksum)
    /// <code>
    ///   u32 cksum = pPager->cksumInit;
    ///   int i = pPager->pageSize-200;
    ///   while( i>0 ){
    ///     cksum += aData[i];
    ///     i -= 200;
    ///   }
    /// </code>
    /// </summary>
    private static uint Checksum(IFileView journal, long offset, byte[] data, uint checksumInit)
    {
        journal.Read(offset, data, 0, data.Length);

        uint checksum = checksumInit;
        unchecked
        {
            for (int i = data.Length - 200; i > 0; i -= 200)
                checksum += data[i];
        }

        return checksum;
    }

    public int Read(long position, byte[] buffer, int offset, int count)
    {
        if (position >= Length)
            return 0;

        count = (int)Math.Min(count, Length - position);
        int done = 0;
        while (done < count)
        {
            long at = position + done;
            uint page = (uint)(at / _pageSize + 1);
            long inPage = at % _pageSize;
            int chunk = (int)Math.Min(count - done, _pageSize - inPage);

            if (_pages.TryGetValue(page, out long journalOffset))
            {
                _journal.Read(journalOffset + inPage, buffer, offset + done, chunk);
            }
            else
            {
                // Bytes past the main file's end are zeros, as the file was extended or reads come up short
                int read = at < _main.Length ? _main.Read(at, buffer, offset + done, chunk) : 0;
                Array.Clear(buffer, offset + done + read, chunk - read);
            }

            done += chunk;
        }

        return count;
    }

    public void Dispose()
    {
    }
}
