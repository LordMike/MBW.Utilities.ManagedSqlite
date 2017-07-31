using System;
using Sqlite3RoLib.Objects.Enums;

namespace Sqlite3RoLib.Objects
{
    internal class DatabaseHeader
    {
        private static byte[] _expectedHeader = new byte[]
        {
            (byte) 'S', (byte) 'Q', (byte) 'L', (byte) 'i', (byte) 't', (byte) 'e', (byte) ' ', (byte) 'f',
            (byte) 'o',(byte) 'r', (byte) 'm', (byte) 'a', (byte) 't', (byte) ' ', (byte) '3', 0
        };

        public ushort PageSize { get; set; }

        public FileWriteVersion WriteVersion { get; set; }

        public FileReadVersion ReadVersion { get; set; }

        public byte ReservedSpaceAtEndOfPage { get; set; }

        public byte MaximumEmbeddedPayloadFraction { get; set; }

        public byte MinimumEmbeddedPayloadFraction { get; set; }

        public byte LeafPayloadFraction { get; set; }

        public uint ChangeCounter { get; set; }

        public uint DatabaseSizeInPages { get; set; }

        public uint FirstFreelistTrunkPage { get; set; }

        public uint FreeListPages { get; set; }

        public uint SchemaCookie { get; set; }

        public uint SchemaFormat { get; set; }

        public uint DefaultPageCacheSize { get; set; }

        public uint Value7 { get; set; }

        public SqliteEncoding TextEncoding { get; set; }

        public uint UserVersion { get; set; }

        public uint IncrementalVacuumMode { get; set; }

        public uint ApplicationId { get; set; }

        public uint VersionValidFor { get; set; }

        public uint Version { get; set; }

        public static DatabaseHeader Parse(ReaderBase reader)
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