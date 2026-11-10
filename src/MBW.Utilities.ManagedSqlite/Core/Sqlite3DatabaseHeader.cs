using System;
using System.Buffers.Binary;
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
// Public API on purpose: every field of the header is exposed for callers (such as the user version and application ID
// that applications tell their databases apart by), whether or not the library reads it. Don't remove a property
// because nothing inside the library uses it.
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
    public uint PageSize => U16(16) == 1 ? 65536u : U16(16);

    /// <summary>
    /// The file format write version (offset 18): <see cref="SqliteFileFormat.Wal"/> in WAL mode. Another value is
    /// kept as stored; above 2, SQLite opens the database read-only.
    /// </summary>
    /// <remarks>https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3326 (lockBtree)</remarks>
    public SqliteFileFormat WriteVersion => (SqliteFileFormat)_bytes[18];

    /// <summary>
    /// The file format read version (offset 19): <see cref="SqliteFileFormat.Wal"/> in WAL mode, see
    /// <see cref="Sqlite3Database.IsWalMode"/>. Another value is kept as stored: SQLite reads 0 as it reads 1, and a
    /// value above 2 fails the open, unless <see cref="SqliteValidation.Lenient"/>.
    /// </summary>
    /// <remarks>https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3329 (lockBtree)</remarks>
    public SqliteFileFormat ReadVersion => (SqliteFileFormat)_bytes[19];

    /// <summary>
    /// The unused bytes at the end of every page (offset 20), such as for an encryption extension
    /// </summary>
    public byte ReservedSpaceAtEndOfPage => _bytes[20];

    /// <summary>
    /// The maximum embedded payload fraction (offset 21), always 64
    /// </summary>
    public byte MaximumEmbeddedPayloadFraction => _bytes[21];

    /// <summary>
    /// The minimum embedded payload fraction (offset 22), always 32
    /// </summary>
    public byte MinimumEmbeddedPayloadFraction => _bytes[22];

    /// <summary>
    /// The leaf payload fraction (offset 23), always 32
    /// </summary>
    public byte LeafPayloadFraction => _bytes[23];

    /// <summary>
    /// The file change counter (offset 24), which SQLite increments on every committed transaction in rollback
    /// journal mode
    /// </summary>
    public uint ChangeCounter => U32(24);

    /// <summary>
    /// The database size in pages (offset 28). SQLite uses it only when it is not 0 and
    /// <see cref="VersionValidFor"/> equals <see cref="ChangeCounter"/>; otherwise the size of the file decides.
    /// </summary>
    public uint DatabaseSizeInPages => U32(28);

    /// <summary>
    /// The page number of the first freelist trunk page (offset 32), or 0 when there are no free pages
    /// </summary>
    public uint FirstFreelistTrunkPage => U32(32);

    /// <summary>
    /// The number of free pages (offset 36). <c>PRAGMA freelist_count</c> gives the same field as a signed 32-bit
    /// integer, which differs only for a count of 2^31 or more, more pages than a database can have.
    /// </summary>
    public uint FreelistPageCount => U32(36);

    /// <summary>
    /// The schema cookie (offset 40), which SQLite increments on every schema change, as <c>PRAGMA schema_version</c>
    /// gives it
    /// </summary>
    public int SchemaCookie => (int)U32(40);

    /// <summary>
    /// The schema format number (offset 44), 1 to 4, or 0 in a database that has never had a schema object, which
    /// SQLite reads as 1. A value whose low byte is above 4 fails the open, unless
    /// <see cref="SqliteValidation.Lenient"/>.
    /// </summary>
    /// <remarks>https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L340 (sqlite3InitOne)</remarks>
    public uint SchemaFormat => U32(44);

    /// <summary>
    /// The default page cache size (offset 48), as the deprecated <c>PRAGMA default_cache_size</c> sets it
    /// </summary>
    public int DefaultPageCacheSize => (int)U32(48);

    /// <summary>
    /// The page number of the largest root b-tree page (offset 52) in auto-vacuum or incremental-vacuum mode, or 0
    /// when auto-vacuum is off
    /// </summary>
    public uint LargestRootPage => U32(52);

    /// <summary>
    /// The text encoding field (offset 56), as stored: it may be 0 or outside <see cref="SqliteEncoding"/>.
    /// <see cref="Sqlite3Database.TextEncoding"/> is the encoding text is read with.
    /// </summary>
    public SqliteEncoding TextEncoding => (SqliteEncoding)U32(56);

    /// <summary>
    /// The user version (offset 60), which SQLite never uses itself and applications set with
    /// <c>PRAGMA user_version</c>, such as to number their schema versions
    /// </summary>
    public int UserVersion => (int)U32(60);

    /// <summary>
    /// Non-zero for incremental-vacuum mode, 0 otherwise (offset 64). With <see cref="LargestRootPage"/> it gives
    /// <c>PRAGMA auto_vacuum</c>: none when that is 0, else incremental when this is non-zero, else full.
    /// </summary>
    public uint IncrementalVacuumMode => U32(64);

    /// <summary>
    /// The application ID (offset 68), which applications set with <c>PRAGMA application_id</c> to mark the database
    /// as their file format
    /// </summary>
    public int ApplicationId => (int)U32(68);

    /// <summary>
    /// The value of <see cref="ChangeCounter"/> when <see cref="SqliteVersionNumber"/> was stored (offset 92)
    /// </summary>
    public uint VersionValidFor => U32(92);

    /// <summary>
    /// The <c>SQLITE_VERSION_NUMBER</c> of the SQLite library that most recently modified the database (offset 96),
    /// such as 3053004 for 3.53.4
    /// </summary>
    public uint SqliteVersionNumber => U32(96);

    /// <summary>
    /// Keeps the first <see cref="HeaderSize"/> bytes of <paramref name="data"/>, the start of the database file, which
    /// is shorter when the file is. Each property reads its field from them, at the offset the file format gives it.
    /// </summary>
    internal Sqlite3DatabaseHeader(ReadOnlySpan<byte> data, Sqlite3Settings settings)
    {
        // HDR-02: SQLite zero-fills a header shorter than 100 bytes, which then fails the magic string check or, if the
        // magic string survives, the page 1 b-tree header checks. (A 0-byte file is a valid empty database, HDR-01.)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L3943 (sqlite3PagerReadFileheader)
        Checks.Strict(data.Length >= HeaderSize, "HDR-02",
            $"the file is shorter than the structures in it say ({HeaderSize} byte header, the file is {data.Length} bytes)",
            structure: "file length");

        _bytes = data.Slice(0, HeaderSize).ToArray();

        // HDR-03: the magic string. lockBtree in btree.c:
        //   if( memcmp(page1, zMagicHeader, 16)!=0 ){ goto page1_init_failed; }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L3314
        Checks.Strict(Bytes.Slice(0, ExpectedHeader.Length).SequenceEqual(ExpectedHeader), "HDR-03",
            $"the magic bytes {Bytes.Slice(0, ExpectedHeader.Length).ToArray().ToHex()} did not match {ExpectedHeader.ToHex()}",
            structure: "magic bytes");

        Validate(settings);
    }

    /// <summary>
    /// The header's 100 bytes, as stored, including the 20 bytes reserved for expansion (offset 72) that no property
    /// has. For a 0-byte file, the header of the empty database it is read as.
    /// </summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    // https://www.sqlite.org/fileformat.html#the_database_header
    // Every multibyte field is big-endian. SQLite reads the 4-byte fields that PRAGMAs give (schema_version,
    // default_cache_size, user_version, application_id) as a signed 32-bit int, so those properties are int.
    // OP_ReadCookie in vdbe.c, which PRAGMA freelist_count uses too; the freelist count stays unsigned here, as a page
    // count, which the b-tree reads it as:
    //   int iMeta;
    //   sqlite3BtreeGetMeta(db->aDb[iDb].pBt, iCookie, (u32 *)&iMeta);
    //   pOut->u.i = iMeta;
    // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L4228 (OP_ReadCookie)
    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.Slice(offset));

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Bytes.Slice(offset));

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
}
