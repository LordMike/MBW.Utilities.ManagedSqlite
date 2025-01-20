using System.Diagnostics;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Headers;

public class DatabaseHeader
{
    public const int HeaderSize = 100;

    private static readonly byte[] _expectedHeader =
    [
        (byte)'S', (byte)'Q', (byte)'L', (byte)'i', (byte)'t', (byte)'e', (byte)' ', (byte)'f',
        (byte)'o', (byte)'r', (byte)'m', (byte)'a', (byte)'t', (byte)' ', (byte)'3', 0
    ];

    public DatabaseHeader(ReaderBase reader)
    {
        // The header is 100 bytes
        reader.CheckSize(100);

        uint toRead = (uint)_expectedHeader.Length;
        Debug.Assert(toRead >= _expectedHeader.Length);
        reader.CheckSize(toRead);

        byte[] data = reader.ReadFully((int)toRead);

        bool res1 = data.SequenceEqual(_expectedHeader);

        // Read header

        PageSize = reader.ReadUInt16();
        WriteVersion = (FileWriteVersion)reader.ReadByte();
        ReadVersion = (FileReadVersion)reader.ReadByte();
        ReservedSpaceAtEndOfPage = reader.ReadByte();
        MaximumEmbeddedPayloadFraction = reader.ReadByte();
        MinimumEmbeddedPayloadFraction = reader.ReadByte();
        LeafPayloadFraction = reader.ReadByte();
        ChangeCounter = reader.ReadUInt32();

        DatabaseSizeInPages = reader.ReadUInt32();
        FirstFreelistTrunkPage = reader.ReadUInt32();
        FreeListPages = reader.ReadUInt32();
        SchemaCookie = reader.ReadUInt32();
        SchemaFormat = reader.ReadUInt32();
        DefaultPageCacheSize = reader.ReadUInt32();
        Value7 = reader.ReadUInt32();
        TextEncoding = (SqliteEncoding)reader.ReadUInt32();
        UserVersion = reader.ReadUInt32();
        IncrementalVacuumMode = reader.ReadUInt32();
        ApplicationId = reader.ReadUInt32();

        reader.Skip(20);

        VersionValidFor = reader.ReadUInt32();
        Version = reader.ReadUInt32();
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