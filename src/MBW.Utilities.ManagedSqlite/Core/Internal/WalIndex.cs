using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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

    // The log is read in whole frames, about this many bytes at a time, rather than with one read per frame
    private const int ChunkSize = 1024 * 1024;

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
        Checksum(bigEndianChecksum, header.AsSpan(0, WalHeaderSize - 8), ref s1, ref s2);
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

        // At least one frame, and no more than the log holds. The buffer comes from the shared pool, so opening a large
        // log does not allocate a fresh megabyte each time; if a read throws, the buffer is left to the GC.
        int chunkFrames = (int)Math.Min(frameCount, Math.Max(1, ChunkSize / frameSize));
        byte[] chunk = ArrayPool<byte>.Shared.Rent(chunkFrames * frameSize);
        long chunkFirst = 0, chunkEnd = 0;

        for (long i = 0; i < frameCount; i++)
        {
            long offset = WalHeaderSize + i * frameSize;
            if (i == chunkEnd)
            {
                // A short read ends at the end of the file: the whole frames it did read are checked as usual, and the
                // next read, of a partial frame or nothing, ends the log
                int count = (int)Math.Min(chunkFrames, frameCount - i) * frameSize;
                chunkFirst = i;
                chunkEnd = i + file.Read(offset, chunk, 0, count) / frameSize;
                if (chunkEnd == i)
                    break;
            }

            ReadOnlySpan<byte> frame = chunk.AsSpan((int)(i - chunkFirst) * frameSize, frameSize);

            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L1000 (walDecodeFrame)
            // WALJ-14 to WALJ-17: a frame is valid when its salts equal the header's, its page number is not 0, and the running checksum
            // over the WAL header and every frame so far (the first 8 bytes of each frame header, then its page)
            // equals the one it stores. The first invalid frame ends the log: it is a torn write, or a frame left
            // over from before the log was restarted. Neither is an error.
            uint page = BinaryPrimitives.ReadUInt32BigEndian(frame);
            uint commitSize = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(4));

            bool saltsMatch = frame.Slice(8, 8).SequenceEqual(header.AsSpan(16, 8));
            if (!saltsMatch || page == 0)
                break;

            Checksum(bigEndianChecksum, frame.Slice(0, 8), ref s1, ref s2);
            Checksum(bigEndianChecksum, frame.Slice(FrameHeaderSize), ref s1, ref s2);

            if (s1 != BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(16)) || s2 != BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(20)))
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

        ArrayPool<byte>.Shared.Return(chunk);

        if (pageCount == 0)
            return null;

        return new WalIndex(file, pageSize, pageCount, committed);
    }

    /// <summary>
    /// Copies the newest committed copy of <paramref name="page"/> into <paramref name="buffer"/>, and returns false when
    /// the log holds none.
    /// </summary>
    public bool TryReadPage(uint page, byte[] buffer, int count)
    {
        if (!_frames.TryGetValue(page, out long offset))
            return false;

        // The frame was read whole when the log was indexed, so a short read now means the file shrank under us: an I/O
        // error, as SQLite's SQLITE_IOERR_SHORT_READ, not damage in the bytes
        if (_file.Read(offset, buffer, 0, count) != count)
            throw new IOException($"The WAL frame of page {page} was cut short after the log was read");

        return true;
    }

    /// <summary>
    /// SQLite's WAL checksum: two running sums over the data as 32-bit words, in pairs. The length is a multiple of 8.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/wal.c#L856 (walChecksumBytes)
    /// <code>
    ///   s1 += aData[0] + s2;
    ///   s2 += aData[1] + s1;
    /// </code>
    /// SQLite has one loop that reads the words in the machine's byte order and one that swaps them; the two loops
    /// here read them as little-endian or big-endian, so neither branches per word.
    /// </summary>
    private static void Checksum(bool bigEndian, ReadOnlySpan<byte> data, ref uint s1, ref uint s2)
    {
        unchecked
        {
            if (bigEndian)
            {
                for (int i = 0; i < data.Length; i += 8)
                {
                    s1 += BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i)) + s2;
                    s2 += BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i + 4)) + s1;
                }
            }
            else
            {
                for (int i = 0; i < data.Length; i += 8)
                {
                    s1 += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i)) + s2;
                    s2 += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4)) + s1;
                }
            }
        }
    }
}
