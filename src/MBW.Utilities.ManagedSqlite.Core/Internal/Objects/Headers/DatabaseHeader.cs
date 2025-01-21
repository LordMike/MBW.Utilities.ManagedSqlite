using System;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Headers;

internal class DatabaseHeader
{
    public const int HeaderSize = 100;
    private static readonly byte[] _expectedHeader = "SQLite format 3\0"u8.ToArray();

    public DatabaseHeader(ReadOnlySpan<byte> page)
    {
        SpanReader rdr = new SpanReader(page);

        if (!rdr.ReadBytes(_expectedHeader.Length).SequenceEqual(_expectedHeader))
            throw new InvalidOperationException("Database does not have a valid signature");

        // Read header
        PageSize = rdr.ReadUInt16();
        WriteVersion = rdr.ReadEnum<FileWriteVersion>();
        ReadVersion = rdr.ReadEnum<FileReadVersion>();
        ReservedSpaceAtEndOfPage = rdr.ReadByte();
        MaximumEmbeddedPayloadFraction = rdr.ReadByte();
        MinimumEmbeddedPayloadFraction = rdr.ReadByte();
        LeafPayloadFraction = rdr.ReadByte();
        ChangeCounter = rdr.ReadUInt32();
        DatabaseSizeInPages = rdr.ReadUInt32();
        FirstFreelistTrunkPage = rdr.ReadUInt32();
        FreeListPages = rdr.ReadUInt32();
        SchemaCookie = rdr.ReadUInt32();
        SchemaFormat = rdr.ReadUInt32();
        DefaultPageCacheSize = rdr.ReadUInt32();
        Value7 = rdr.ReadUInt32();
        TextEncoding = (SqliteEncoding)rdr.ReadUInt32();
        UserVersion = rdr.ReadUInt32();
        IncrementalVacuumMode = rdr.ReadUInt32();
        ApplicationId = rdr.ReadUInt32();

        rdr.SkipForwards(20);

        VersionValidFor = rdr.ReadUInt32();
        Version = rdr.ReadUInt32();
    }

    public ushort PageSize { get; }
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
}