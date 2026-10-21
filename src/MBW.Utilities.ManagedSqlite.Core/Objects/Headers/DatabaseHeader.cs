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
    internal DatabaseHeader(byte[] data, int length)
    {
        if (length < HeaderSize)
            throw new SqliteCorruptException("the file is shorter than the structures in it say", structure: "file length")
            {
                Data =
                {
                    { "Position", 0L },
                    { "sizeWanted", (uint)HeaderSize },
                    { "SourceLength", (long)length }
                }
            };

        if (!data.Take(_expectedHeader.Length).SequenceEqual(_expectedHeader))
            throw new SqliteCorruptException("the requested magic bytes did not match", structure: "magic bytes")
            {
                Data =
                {
                    { "comparison", _expectedHeader.ToHex() },
                    { "data", data.Take(_expectedHeader.Length).ToArray().ToHex() }
                }
            };

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

        Validate();
    }

    private void Validate()
    {
        // https://www.sqlite.org/fileformat.html#page_size
        // A power of two between 512 and 65536. The stored value 1 has already been mapped to 65536.
        bool validPageSize = PageSize >= 512 && PageSize <= 65536 && (PageSize & (PageSize - 1)) == 0;

        if (!validPageSize)
            throw new SqliteCorruptException($"invalid page size {PageSize}", structure: "database header");

        // https://www.sqlite.org/fileformat.html#reserved_bytes_per_page
        // The usable size of a page may not be less than 480
        uint usableSize = PageSize - ReservedSpaceAtEndOfPage;
        if (usableSize < 480)
            throw new SqliteCorruptException($"usable page size {usableSize} is less than 480 (page size {PageSize}, reserved space {ReservedSpaceAtEndOfPage})", structure: "database header");

        // https://www.sqlite.org/fileformat.html#file_format_version_numbers
        // A read version above 2 means a format this reader (and SQLite) does not know, so SQLite treats the file as not a database
        if ((byte)ReadVersion > 2)
            throw new SqliteCorruptException($"file format read version {(byte)ReadVersion} is not supported (1 or 2)", structure: "database header");

        // https://www.sqlite.org/fileformat.html#payload_fractions
        // The payload fractions must be 64, 32 and 32, and SQLite rejects a file with other values
        if (MaximumEmbeddedPayloadFraction != 64 || MinimumEmbeddedPayloadFraction != 32 || LeafPayloadFraction != 32)
            throw new SqliteCorruptException($"payload fractions {MaximumEmbeddedPayloadFraction}, {MinimumEmbeddedPayloadFraction}, {LeafPayloadFraction} must be 64, 32, 32", structure: "database header");
    }
}
