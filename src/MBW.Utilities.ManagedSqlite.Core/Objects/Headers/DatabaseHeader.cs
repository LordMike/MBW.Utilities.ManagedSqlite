using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Objects.Headers
{
    public class DatabaseHeader
    {
        public const int HeaderSize = 100;

        private static readonly byte[] _expectedHeader = {
            (byte) 'S', (byte) 'Q', (byte) 'L', (byte) 'i', (byte) 't', (byte) 'e', (byte) ' ', (byte) 'f',
            (byte) 'o',(byte) 'r', (byte) 'm', (byte) 'a', (byte) 't', (byte) ' ', (byte) '3', 0
        };

        public ushort PageSize { get; internal set; }

        public FileWriteVersion WriteVersion { get; private set; }

        public FileReadVersion ReadVersion { get; private set; }

        public byte ReservedSpaceAtEndOfPage { get; internal set; }

        public byte MaximumEmbeddedPayloadFraction { get; private set; }

        public byte MinimumEmbeddedPayloadFraction { get; private set; }

        public byte LeafPayloadFraction { get; private set; }

        public uint ChangeCounter { get; private set; }

        public uint DatabaseSizeInPages { get; private set; }

        public uint FirstFreelistTrunkPage { get; private set; }

        public uint FreeListPages { get; private set; }

        public uint SchemaCookie { get; private set; }

        public uint SchemaFormat { get; private set; }

        public uint DefaultPageCacheSize { get; private set; }

        public uint Value7 { get; private set; }

        public SqliteEncoding TextEncoding { get; internal set; }

        public uint UserVersion { get; private set; }

        public uint IncrementalVacuumMode { get; private set; }

        public uint ApplicationId { get; private set; }

        public uint VersionValidFor { get; private set; }

        public uint Version { get; private set; }

        internal static DatabaseHeader Parse(ReaderBase reader)
        {
            // The header is 100 bytes
            reader.CheckSize(100);

            reader.CheckMagicBytes(_expectedHeader);

            // Read header
            DatabaseHeader res = new DatabaseHeader();

            res.PageSize = reader.ReadUInt16();
            res.WriteVersion = (FileWriteVersion)reader.ReadByte();
            res.ReadVersion = (FileReadVersion)reader.ReadByte();
            res.ReservedSpaceAtEndOfPage = reader.ReadByte();
            res.MaximumEmbeddedPayloadFraction = reader.ReadByte();
            res.MinimumEmbeddedPayloadFraction = reader.ReadByte();
            res.LeafPayloadFraction = reader.ReadByte();
            res.ChangeCounter = reader.ReadUInt32();

            res.DatabaseSizeInPages = reader.ReadUInt32();
            res.FirstFreelistTrunkPage = reader.ReadUInt32();
            res.FreeListPages = reader.ReadUInt32();
            res.SchemaCookie = reader.ReadUInt32();
            res.SchemaFormat = reader.ReadUInt32();
            res.DefaultPageCacheSize = reader.ReadUInt32();
            res.Value7 = reader.ReadUInt32();
            res.TextEncoding = (SqliteEncoding)reader.ReadUInt32();
            res.UserVersion = reader.ReadUInt32();
            res.IncrementalVacuumMode = reader.ReadUInt32();
            res.ApplicationId = reader.ReadUInt32();

            reader.Skip(20);

            res.VersionValidFor = reader.ReadUInt32();
            res.Version = reader.ReadUInt32();

            // TODO: Warn/err on mismatch with expected values

            return res;
        }
    }
}