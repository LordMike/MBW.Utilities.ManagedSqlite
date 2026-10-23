using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

/// <summary>
/// The committed frames of a <c>-wal</c> file: for each page, the newest copy of it that a committed transaction wrote.
/// Built the way SQLite rebuilds its wal-index when there is no usable <c>-shm</c> file, by reading the whole log.
/// </summary>
/// <remarks>
/// https://www.sqlite.org/fileformat.html#the_write_ahead_log
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L1390 (walIndexRecover)
/// The <c>-shm</c> file is only a cache of this index plus the locks between connections, so it is never read.
/// </remarks>
internal sealed class WalIndex
{
    // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L277 (WAL_MAX_VERSION), #L477-L480 (sizes), #L491 (WAL_MAGIC)
    private const uint WalMagic = 0x377f0682;
    private const uint WalVersion = 3007000;
    private const int WalHeaderSize = 32;
    private const int FrameHeaderSize = 24;

    private readonly IFileView _file;
    private readonly Dictionary<uint, long> _frames;

    /// <summary>
    /// The page size of the frames, from the WAL header
    /// </summary>
    public uint PageSize { get; }

    /// <summary>
    /// The database size in pages after the last committed transaction, or 0 when the log holds no committed
    /// transaction. SQLite then takes the page count from the database file instead (sqlite3WalDbsize, pagerPagecount).
    /// </summary>
    public uint PageCount { get; }

    private WalIndex(IFileView file, uint pageSize, uint pageCount, Dictionary<uint, long> frames)
    {
        _file = file;
        PageSize = pageSize;
        PageCount = pageCount;
        _frames = frames;
    }

    /// <summary>
    /// Reads the log in <paramref name="file"/>. Returns null when it holds no committed transaction, as SQLite then
    /// reads the database file alone.
    /// </summary>
    public static WalIndex? Read(IFileView file)
    {
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L1418-L1421 (walIndexRecover)
        //   rc = sqlite3OsFileSize(pWal->pWalFd, &nSize);
        //   if( nSize>WAL_HDRSIZE ){
        // WALJ-06: a log no longer than its header is empty.
        long size = file.Length;
        if (size <= WalHeaderSize)
            return null;

        byte[] header = new byte[WalHeaderSize];
        if (file.Read(0, header, 0, WalHeaderSize) != WalHeaderSize)
            return null;

        int pos = 0;
        uint magic = ByteArrayReader.ReadUInt32(header, WalHeaderSize, ref pos);
        uint version = ByteArrayReader.ReadUInt32(header, WalHeaderSize, ref pos);
        uint pageSize = ByteArrayReader.ReadUInt32(header, WalHeaderSize, ref pos);

        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L1445-L1453 (walIndexRecover)
        //   if( (magic&0xFFFFFFFE)!=WAL_MAGIC
        //    || szPage&(szPage-1)
        //    || szPage>SQLITE_MAX_PAGE_SIZE
        //    || szPage<512
        //   ){
        //     goto finished;
        //   }
        // WALJ-07, WALJ-09: a log with a wrong magic or page size is ignored as a whole, without an error. The page size is a plain
        // 32-bit value here: 65536 is stored as is, unlike the database header's "1".
        if ((magic & 0xFFFFFFFE) != WalMagic || (pageSize & (pageSize - 1)) != 0 || pageSize > 65536 || pageSize < 512)
            return null;

        // WALJ-08: the low bit of the magic says in which byte order the checksums add up the 32-bit words: big-endian when set,
        // little-endian when clear. SQLite compares it with the machine's byte order to pick its native or byte-swapped
        // loop, which comes to the same thing on any machine.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L856 (walChecksumBytes)
        bool bigEndianChecksum = (magic & 1) != 0;

        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L1460-L1468 (walIndexRecover)
        //   walChecksumBytes(pWal->hdr.bigEndCksum==SQLITE_BIGENDIAN, aBuf, WAL_HDRSIZE-2*4, 0, pWal->hdr.aFrameCksum);
        //   if( pWal->hdr.aFrameCksum[0]!=sqlite3Get4byte(&aBuf[24]) || ... ) goto finished;
        // WALJ-10: a wrong header checksum, too, means the log is ignored. WALJ-12: the salts and checkpoint sequence
        // may hold any value; the salts are only compared with each frame's.
        uint s1 = 0, s2 = 0;
        Checksum(bigEndianChecksum, header, 0, WalHeaderSize - 8, ref s1, ref s2);
        pos = 24;
        if (s1 != ByteArrayReader.ReadUInt32(header, WalHeaderSize, ref pos) || s2 != ByteArrayReader.ReadUInt32(header, WalHeaderSize, ref pos))
            return null;

        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L1470-L1476 (walIndexRecover)
        //   if( version!=WAL_MAX_VERSION ){
        //     rc = SQLITE_CANTOPEN_BKPT;
        // WALJ-11: a valid log in a format version SQLite does not know cannot be opened. Ignoring it would read stale
        // data, so this fails in every mode.
        Checks.Strict(version == WalVersion, "WALJ-11", $"unsupported WAL format version {version} (expected {WalVersion})",
            structure: "WAL header");

        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L1489 (walIndexRecover)
        //   iLastFrame = (nSize - WAL_HDRSIZE) / szFrame;
        // WALJ-13: a partial frame at the end of the log is not counted.
        int frameSize = (int)pageSize + FrameHeaderSize;
        long frameCount = (size - WalHeaderSize) / frameSize;

        Dictionary<uint, long> committed = new Dictionary<uint, long>();
        List<KeyValuePair<uint, long>> pending = new List<KeyValuePair<uint, long>>();
        uint pageCount = 0;

        byte[] frame = new byte[frameSize];
        for (long i = 0; i < frameCount; i++)
        {
            long offset = WalHeaderSize + i * frameSize;
            if (file.Read(offset, frame, 0, frameSize) != frameSize)
                break;

            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L1000 (walDecodeFrame)
            // WALJ-14 to WALJ-17: a frame is valid when its salts equal the header's, its page number is not 0, and the running checksum
            // over the WAL header and every frame so far (the first 8 bytes of each frame header, then its page)
            // equals the one it stores. The first invalid frame ends the log: it is a torn write, or a frame left
            // over from before the log was restarted. Neither is an error.
            pos = 0;
            uint page = ByteArrayReader.ReadUInt32(frame, frameSize, ref pos);
            uint commitSize = ByteArrayReader.ReadUInt32(frame, frameSize, ref pos);

            bool saltsMatch = frame[8] == header[16] && frame[9] == header[17] && frame[10] == header[18] && frame[11] == header[19] &&
                              frame[12] == header[20] && frame[13] == header[21] && frame[14] == header[22] && frame[15] == header[23];
            if (!saltsMatch || page == 0)
                break;

            Checksum(bigEndianChecksum, frame, 0, 8, ref s1, ref s2);
            Checksum(bigEndianChecksum, frame, FrameHeaderSize, (int)pageSize, ref s1, ref s2);

            pos = 16;
            if (s1 != ByteArrayReader.ReadUInt32(frame, frameSize, ref pos) || s2 != ByteArrayReader.ReadUInt32(frame, frameSize, ref pos))
                break;

            pending.Add(new KeyValuePair<uint, long>(page, offset + FrameHeaderSize));

            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L1512-L1518 (walIndexRecover)
            //   if( nTruncate ){
            //     pWal->hdr.mxFrame = iFrame;
            //     pWal->hdr.nPage = nTruncate;
            // WALJ-18, WALJ-19, WALJ-24: a frame with a database size ends a transaction, and that size, whatever it
            // is, becomes the page count. Frames after the last one belong to a transaction that never committed, and
            // are dropped. WALJ-20, WALJ-21: frames of pages past that count, or of the lock-byte page, are kept like
            // any other, as the reader never asks for them, and the newest committed copy of a page wins.
            if (commitSize != 0)
            {
                foreach (KeyValuePair<uint, long> entry in pending)
                    committed[entry.Key] = entry.Value;

                pending.Clear();
                pageCount = commitSize;
            }
        }

        if (pageCount == 0)
            return null;

        return new WalIndex(file, pageSize, pageCount, committed);
    }

    /// <summary>
    /// True when the log holds a committed copy of <paramref name="page"/>
    /// </summary>
    public bool Contains(uint page) => _frames.ContainsKey(page);

    /// <summary>
    /// Copies the newest committed copy of <paramref name="page"/> into <paramref name="buffer"/>, and returns false when
    /// the log holds none.
    /// </summary>
    public bool TryReadPage(uint page, byte[] buffer, int count)
    {
        if (!_frames.TryGetValue(page, out long offset))
            return false;

        if (_file.Read(offset, buffer, 0, count) != count)
            throw new SqliteCorruptException($"the WAL frame of page {page} is cut short", page, "WAL frame");

        return true;
    }

    /// <summary>
    /// SQLite's WAL checksum: two running sums over the data as 32-bit words, in pairs.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L856 (walChecksumBytes)
    /// <code>
    ///   s1 += aData[0] + s2;
    ///   s2 += aData[1] + s1;
    /// </code>
    /// </summary>
    private static void Checksum(bool bigEndian, byte[] data, int offset, int length, ref uint s1, ref uint s2)
    {
        unchecked
        {
            for (int i = offset; i < offset + length; i += 8)
            {
                s1 += Word(bigEndian, data, i) + s2;
                s2 += Word(bigEndian, data, i + 4) + s1;
            }
        }
    }

    private static uint Word(bool bigEndian, byte[] data, int i)
    {
        return bigEndian
            ? (uint)(data[i] << 24 | data[i + 1] << 16 | data[i + 2] << 8 | data[i + 3])
            : (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
    }
}
