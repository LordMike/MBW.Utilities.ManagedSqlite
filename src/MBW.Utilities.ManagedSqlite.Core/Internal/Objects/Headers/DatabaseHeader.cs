using System;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Headers;

internal class DatabaseHeader
{
    public const int HeaderSize = 100;
    private static readonly byte[] _expectedHeader = "SQLite format 3\0"u8.ToArray();

    public DatabaseHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[_expectedHeader.Length];
        stream.ReadExactly(header);

        if (!header.SequenceEqual(_expectedHeader))
            throw new InvalidOperationException("Database does not have a valid signature");

        // Read header
        PageSize = stream.ReadUInt16();
        WriteVersion = (FileWriteVersion)stream.ReadByte();
        ReadVersion = (FileReadVersion)stream.ReadByte();
        ReservedSpaceAtEndOfPage = (byte)stream.ReadByte();
        MaximumEmbeddedPayloadFraction = (byte)stream.ReadByte();
        MinimumEmbeddedPayloadFraction = (byte)stream.ReadByte();
        LeafPayloadFraction = (byte)stream.ReadByte();
        ChangeCounter = stream.ReadUInt32();
        DatabaseSizeInPages = stream.ReadUInt32();
        FirstFreelistTrunkPage = stream.ReadUInt32();
        FreeListPages = stream.ReadUInt32();
        SchemaCookie = stream.ReadUInt32();
        SchemaFormat = stream.ReadUInt32();
        DefaultPageCacheSize = stream.ReadUInt32();
        Value7 = stream.ReadUInt32();
        TextEncoding = (SqliteEncoding)stream.ReadUInt32();
        UserVersion = stream.ReadUInt32();
        IncrementalVacuumMode = stream.ReadUInt32();
        ApplicationId = stream.ReadUInt32();

        stream.Position += 20;

        VersionValidFor = stream.ReadUInt32();
        Version = stream.ReadUInt32();
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