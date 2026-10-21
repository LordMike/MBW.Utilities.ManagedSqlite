using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

internal sealed class DatabaseHeader
{
    public const int HeaderSize = 100;

    private static readonly byte[] _expectedHeader = {
        (byte) 'S', (byte) 'Q', (byte) 'L', (byte) 'i', (byte) 't', (byte) 'e', (byte) ' ', (byte) 'f',
        (byte) 'o',(byte) 'r', (byte) 'm', (byte) 'a', (byte) 't', (byte) ' ', (byte) '3', 0
    };

    /// <summary>
    /// The database page size in bytes (512 to 65536)
    /// </summary>
    public uint PageSize { get; }

    public FileWriteVersion WriteVersion { get; }

    public FileReadVersion ReadVersion { get; }

    public byte ReservedSpaceAtEndOfPage { get; }

    public byte MaximumEmbeddedPayloadFraction { get; }

    public byte MinimumEmbeddedPayloadFraction { get; }

    public byte LeafPayloadFraction { get; }

    public uint ChangeCounter { get; }

    public uint DatabaseSizeInPages { get; }

    public uint FirstFreelistTrunkPage { get; }

    public uint FreeListPages { get; }

    public uint SchemaCookie { get; }

    public uint SchemaFormat { get; }

    public uint DefaultPageCacheSize { get; }

    public uint Value7 { get; }

    public SqliteEncoding TextEncoding { get; }

    public uint UserVersion { get; }

    public uint IncrementalVacuumMode { get; }

    public uint ApplicationId { get; }

    public uint VersionValidFor { get; }

    public uint Version { get; }

    /// <summary>
    /// Parses the first <see cref="HeaderSize"/> bytes of the database file. <paramref name="length"/> is how many
    /// bytes of <paramref name="data"/> the file actually had.
    /// </summary>
    internal DatabaseHeader(byte[] data, int length, Sqlite3Settings settings)
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
        Checks.Strict(data.Take(_expectedHeader.Length).SequenceEqual(_expectedHeader), "HDR-03",
            $"the magic bytes {data.Take(_expectedHeader.Length).ToArray().ToHex()} did not match {_expectedHeader.ToHex()}",
            structure: "magic bytes");

        int pos = _expectedHeader.Length;

        // The page size is stored as a big-endian 2-byte value, where 1 means 65536
        ushort rawPageSize = ByteArrayReader.ReadUInt16(data, length, ref pos);
        PageSize = rawPageSize == 1 ? 65536u : rawPageSize;
        WriteVersion = (FileWriteVersion)ByteArrayReader.ReadByte(data, length, ref pos);
        ReadVersion = (FileReadVersion)ByteArrayReader.ReadByte(data, length, ref pos);
        ReservedSpaceAtEndOfPage = ByteArrayReader.ReadByte(data, length, ref pos);
        MaximumEmbeddedPayloadFraction = ByteArrayReader.ReadByte(data, length, ref pos);
        MinimumEmbeddedPayloadFraction = ByteArrayReader.ReadByte(data, length, ref pos);
        LeafPayloadFraction = ByteArrayReader.ReadByte(data, length, ref pos);
        ChangeCounter = ByteArrayReader.ReadUInt32(data, length, ref pos);

        DatabaseSizeInPages = ByteArrayReader.ReadUInt32(data, length, ref pos);
        FirstFreelistTrunkPage = ByteArrayReader.ReadUInt32(data, length, ref pos);
        FreeListPages = ByteArrayReader.ReadUInt32(data, length, ref pos);
        SchemaCookie = ByteArrayReader.ReadUInt32(data, length, ref pos);
        SchemaFormat = ByteArrayReader.ReadUInt32(data, length, ref pos);
        DefaultPageCacheSize = ByteArrayReader.ReadUInt32(data, length, ref pos);
        Value7 = ByteArrayReader.ReadUInt32(data, length, ref pos);
        TextEncoding = (SqliteEncoding)ByteArrayReader.ReadUInt32(data, length, ref pos);
        UserVersion = ByteArrayReader.ReadUInt32(data, length, ref pos);
        IncrementalVacuumMode = ByteArrayReader.ReadUInt32(data, length, ref pos);
        ApplicationId = ByteArrayReader.ReadUInt32(data, length, ref pos);

        pos += 20;

        VersionValidFor = ByteArrayReader.ReadUInt32(data, length, ref pos);
        Version = ByteArrayReader.ReadUInt32(data, length, ref pos);

        // TODO: Warn/err on mismatch with expected values

        Validate(settings);
    }

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

        // HDR-24, SCHEMA-02: the schema format number's low byte, where 0 means 1, must be at most 4. SQLite fails the
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
        Checks.Relaxable((byte)SchemaFormat <= 4, settings, "HDR-24",
            $"unsupported schema format {(byte)SchemaFormat} (1 to 4)", structure: "database header");
    }
}
