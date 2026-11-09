using System;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Core;

/// <summary>
/// The 100-byte header at the start of a database file (https://www.sqlite.org/fileformat.html#the_database_header),
/// as <see cref="Sqlite3Database.Header"/> read it: from the newest committed page 1, so from the <c>-wal</c> file or
/// after a hot <c>-journal</c> is played back when the database was opened with one. Every field holds the value
/// stored in the file. A field that SQLite reads differently from its stored value says so, and
/// <see cref="Sqlite3Database"/> has the value as it is used.
/// </summary>
/// <remarks>
/// A 0-byte file is a valid empty database, which has no header. It is read as the database SQLite would create in it:
/// a page size of 4096, one page, legacy file format versions, the payload fractions 64, 32 and 32, schema format 4,
/// and 0 in every other field.
/// </remarks>
public sealed class Sqlite3DatabaseHeader
{
    internal const int HeaderSize = 100;

    private static readonly byte[] ExpectedHeader = {
        (byte) 'S', (byte) 'Q', (byte) 'L', (byte) 'i', (byte) 't', (byte) 'e', (byte) ' ', (byte) 'f',
        (byte) 'o',(byte) 'r', (byte) 'm', (byte) 'a', (byte) 't', (byte) ' ', (byte) '3', 0
    };

    private readonly byte[] _bytes;

    /// <summary>
    /// The page size in bytes, a power of two from 512 to 65536 (offset 16; the stored value 1 means 65536)
    /// </summary>
    public uint PageSize { get; }

    /// <summary>
    /// The file format write version (offset 18): <see cref="SqliteFileFormat.Wal"/> in WAL mode. Another value is
    /// kept as stored; above 2, SQLite opens the database read-only.
    /// </summary>
    /// <remarks>https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3326 (lockBtree)</remarks>
    public SqliteFileFormat WriteVersion { get; }

    /// <summary>
    /// The file format read version (offset 19): <see cref="SqliteFileFormat.Wal"/> in WAL mode, see
    /// <see cref="Sqlite3Database.IsWalMode"/>. Another value is kept as stored: SQLite reads 0 as it reads 1, and a
    /// value above 2 fails the open, unless <see cref="SqliteValidation.Lenient"/>.
    /// </summary>
    /// <remarks>https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3329 (lockBtree)</remarks>
    public SqliteFileFormat ReadVersion { get; }

    /// <summary>
    /// The unused bytes at the end of every page (offset 20), such as for an encryption extension
    /// </summary>
    public byte ReservedSpaceAtEndOfPage { get; }

    /// <summary>
    /// The maximum embedded payload fraction (offset 21), always 64
    /// </summary>
    public byte MaximumEmbeddedPayloadFraction { get; }

    /// <summary>
    /// The minimum embedded payload fraction (offset 22), always 32
    /// </summary>
    public byte MinimumEmbeddedPayloadFraction { get; }

    /// <summary>
    /// The leaf payload fraction (offset 23), always 32
    /// </summary>
    public byte LeafPayloadFraction { get; }

    /// <summary>
    /// The file change counter (offset 24), which SQLite increments on every committed transaction in rollback
    /// journal mode
    /// </summary>
    public uint ChangeCounter { get; }

    /// <summary>
    /// The database size in pages (offset 28). SQLite uses it only when it is not 0 and
    /// <see cref="VersionValidFor"/> equals <see cref="ChangeCounter"/>; otherwise the size of the file decides.
    /// </summary>
    public uint DatabaseSizeInPages { get; }

    /// <summary>
    /// The page number of the first freelist trunk page (offset 32), or 0 when there are no free pages
    /// </summary>
    public uint FirstFreelistTrunkPage { get; }

    /// <summary>
    /// The number of free pages (offset 36). <c>PRAGMA freelist_count</c> gives the same field as a signed 32-bit
    /// integer, which differs only for a count of 2^31 or more, more pages than a database can have.
    /// </summary>
    public uint FreelistPageCount { get; }

    /// <summary>
    /// The schema cookie (offset 40), which SQLite increments on every schema change, as <c>PRAGMA schema_version</c>
    /// gives it
    /// </summary>
    public int SchemaCookie { get; }

    /// <summary>
    /// The schema format number (offset 44), 1 to 4, or 0 in a database that has never had a schema object, which
    /// SQLite reads as 1. A value whose low byte is above 4 fails the open, unless
    /// <see cref="SqliteValidation.Lenient"/>.
    /// </summary>
    /// <remarks>https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L340 (sqlite3InitOne)</remarks>
    public uint SchemaFormat { get; }

    /// <summary>
    /// The default page cache size (offset 48), as the deprecated <c>PRAGMA default_cache_size</c> sets it
    /// </summary>
    public int DefaultPageCacheSize { get; }

    /// <summary>
    /// The page number of the largest root b-tree page (offset 52) in auto-vacuum or incremental-vacuum mode, or 0
    /// when auto-vacuum is off
    /// </summary>
    public uint LargestRootPage { get; }

    /// <summary>
    /// The text encoding field (offset 56), as stored: it may be 0 or outside <see cref="SqliteEncoding"/>.
    /// <see cref="Sqlite3Database.TextEncoding"/> is the encoding text is read with.
    /// </summary>
    public SqliteEncoding TextEncoding { get; }

    /// <summary>
    /// The user version (offset 60), which SQLite never uses itself and applications set with
    /// <c>PRAGMA user_version</c>, such as to number their schema versions
    /// </summary>
    public int UserVersion { get; }

    /// <summary>
    /// Non-zero for incremental-vacuum mode, 0 otherwise (offset 64). With <see cref="LargestRootPage"/> it gives
    /// <c>PRAGMA auto_vacuum</c>: none when that is 0, else incremental when this is non-zero, else full.
    /// </summary>
    public uint IncrementalVacuumMode { get; }

    /// <summary>
    /// The application ID (offset 68), which applications set with <c>PRAGMA application_id</c> to mark the database
    /// as their file format
    /// </summary>
    public int ApplicationId { get; }

    /// <summary>
    /// The value of <see cref="ChangeCounter"/> when <see cref="SqliteVersionNumber"/> was stored (offset 92)
    /// </summary>
    public uint VersionValidFor { get; }

    /// <summary>
    /// The <c>SQLITE_VERSION_NUMBER</c> of the SQLite library that most recently modified the database (offset 96),
    /// such as 3053004 for 3.53.4
    /// </summary>
    public uint SqliteVersionNumber { get; }

    /// <summary>
    /// Parses the first <see cref="HeaderSize"/> bytes of the database file. <paramref name="length"/> is how many
    /// bytes of <paramref name="data"/> the file actually had.
    /// </summary>
    internal Sqlite3DatabaseHeader(byte[] data, int length, Sqlite3Settings settings)
    {
        // HDR-02: SQLite zero-fills a header shorter than 100 bytes, which then fails the magic string check or, if the
        // magic string survives, the page 1 b-tree header checks. (A 0-byte file is a valid empty database, HDR-01.)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3943 (sqlite3PagerReadFileheader)
        Checks.Strict(length >= HeaderSize, "HDR-02",
            $"the file is shorter than the structures in it say ({HeaderSize} byte header, the file is {length} bytes)",
            structure: "file length");

        // HDR-03: the magic string. lockBtree in btree.c:
        //   if( memcmp(page1, zMagicHeader, 16)!=0 ){ goto page1_init_failed; }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3314
        Checks.Strict(HasMagic(data), "HDR-03",
            $"the magic bytes {data.Take(ExpectedHeader.Length).ToArray().ToHex()} did not match {ExpectedHeader.ToHex()}",
            structure: "magic bytes");

        _bytes = new byte[HeaderSize];
        Array.Copy(data, _bytes, HeaderSize);

        int pos = ExpectedHeader.Length;

        // https://www.sqlite.org/fileformat.html#the_database_header
        // The page size is stored as a big-endian 2-byte value, where 1 means 65536
        ushort rawPageSize = ByteArrayReader.ReadUInt16(data, length, ref pos);
        PageSize = rawPageSize == 1 ? 65536u : rawPageSize;
        WriteVersion = (SqliteFileFormat)ByteArrayReader.ReadByte(data, length, ref pos);
        ReadVersion = (SqliteFileFormat)ByteArrayReader.ReadByte(data, length, ref pos);
        ReservedSpaceAtEndOfPage = ByteArrayReader.ReadByte(data, length, ref pos);
        MaximumEmbeddedPayloadFraction = ByteArrayReader.ReadByte(data, length, ref pos);
        MinimumEmbeddedPayloadFraction = ByteArrayReader.ReadByte(data, length, ref pos);
        LeafPayloadFraction = ByteArrayReader.ReadByte(data, length, ref pos);
        ChangeCounter = ByteArrayReader.ReadUInt32(data, length, ref pos);
        DatabaseSizeInPages = ByteArrayReader.ReadUInt32(data, length, ref pos);
        FirstFreelistTrunkPage = ByteArrayReader.ReadUInt32(data, length, ref pos);
        FreelistPageCount = ByteArrayReader.ReadUInt32(data, length, ref pos);

        // SQLite reads the 4-byte fields that PRAGMAs give (schema_version, default_cache_size, user_version,
        // application_id) as a signed 32-bit int. OP_ReadCookie in vdbe.c, which PRAGMA freelist_count uses too; the
        // freelist count stays unsigned here, as a page count, which the b-tree reads it as:
        //   int iMeta;
        //   sqlite3BtreeGetMeta(db->aDb[iDb].pBt, iCookie, (u32 *)&iMeta);
        //   pOut->u.i = iMeta;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L4228 (OP_ReadCookie)
        SchemaCookie = (int)ByteArrayReader.ReadUInt32(data, length, ref pos);
        SchemaFormat = ByteArrayReader.ReadUInt32(data, length, ref pos);
        DefaultPageCacheSize = (int)ByteArrayReader.ReadUInt32(data, length, ref pos);
        LargestRootPage = ByteArrayReader.ReadUInt32(data, length, ref pos);
        TextEncoding = (SqliteEncoding)ByteArrayReader.ReadUInt32(data, length, ref pos);
        UserVersion = (int)ByteArrayReader.ReadUInt32(data, length, ref pos);
        IncrementalVacuumMode = ByteArrayReader.ReadUInt32(data, length, ref pos);
        ApplicationId = (int)ByteArrayReader.ReadUInt32(data, length, ref pos);
        pos += 20; // reserved for expansion, zero; GetBytes has them
        VersionValidFor = ByteArrayReader.ReadUInt32(data, length, ref pos);
        SqliteVersionNumber = ByteArrayReader.ReadUInt32(data, length, ref pos);

        Validate(settings);
    }

    /// <summary>
    /// A copy of the header's 100 bytes, as stored, including the 20 bytes reserved for expansion (offset 72) that no
    /// property has. For a 0-byte file, the header of the empty database it is read as.
    /// </summary>
    public byte[] GetBytes() => (byte[])_bytes.Clone();

    private void Validate(Sqlite3Settings settings)
    {
        // https://www.sqlite.org/fileformat.html#page_size
        // A power of two between 512 and 65536. The stored value 1 has already been mapped to 65536.
        bool validPageSize = PageSize >= 512 && PageSize <= 65536 && (PageSize & (PageSize - 1)) == 0;

        // HDR-09, lockBtree in btree.c:
        //   if( ((pageSize-1)&pageSize)!=0 || pageSize>SQLITE_MAX_PAGE_SIZE || pageSize<=256 ){ goto page1_init_failed; }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3374
        Checks.Strict(validPageSize, "HDR-09", $"invalid page size {PageSize}", structure: "database header");

        // https://www.sqlite.org/fileformat.html#reserved_bytes_per_page
        // The usable size of a page may not be less than 480
        uint usableSize = PageSize - ReservedSpaceAtEndOfPage;
        // HDR-11, lockBtree in btree.c: "if( usableSize<480 ){ goto page1_init_failed; }"
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3416
        Checks.Strict(usableSize >= 480, "HDR-11",
            $"usable page size {usableSize} is less than 480 (page size {PageSize}, reserved space {ReservedSpaceAtEndOfPage})",
            structure: "database header");

        // HDR-05: a read version above 2 means a format this reader (and SQLite) does not know, so SQLite treats the
        // file as not a database (SQLITE_NOTADB). lockBtree in btree.c:
        //   if( page1[19]>2 ){ goto page1_init_failed; }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3329
        // https://www.sqlite.org/fileformat.html#file_format_version_numbers
        // The b-tree itself is unchanged by that byte, so Lenient reads on.
        Checks.Relaxable((byte)ReadVersion <= 2, settings, "HDR-05",
            $"file format read version {(byte)ReadVersion} is not supported (1 or 2)", structure: "database header");

        // HDR-08: the payload fractions must be 64, 32 and 32 (SQLITE_NOTADB otherwise). lockBtree in btree.c:
        //   if( memcmp(&page1[21], "\100\040\040",3)!=0 ){ goto page1_init_failed; }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3365
        // https://www.sqlite.org/fileformat.html#payload_fractions
        // SQLite never consults them when reading (the local payload limits are computed from the usable size), so
        // Lenient reads on.
        Checks.Relaxable(MaximumEmbeddedPayloadFraction == 64 && MinimumEmbeddedPayloadFraction == 32 && LeafPayloadFraction == 32,
            settings, "HDR-08",
            $"payload fractions {MaximumEmbeddedPayloadFraction}, {MinimumEmbeddedPayloadFraction}, {LeafPayloadFraction} must be 64, 32, 32",
            structure: "database header");

    }

    private static bool HasMagic(byte[] data)
    {
        for (int i = 0; i < ExpectedHeader.Length; i++)
        {
            if (data[i] != ExpectedHeader[i])
                return false;
        }

        return true;
    }
}
