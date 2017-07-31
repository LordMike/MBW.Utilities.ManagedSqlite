using System;
using Sqlite3RoLib.Objects;

namespace Sqlite3RoLib
{
    internal class BTreePage
    {
        private readonly uint _page;
        private readonly ReaderBase _reader;
        private readonly BTreeHeader _header;

        public BTreePage(ReaderBase reader, uint page)
        {
            _reader = reader;
            _page = page;

            // Read header
            reader.SeekPage(page);

            if (page == 1)
            {
                // Skip the first 100 bytes
                reader.Skip(DatabaseHeader.HeaderSize);
            }

            _header = BTreeHeader.Parse(reader);

            // Read cells
            ushort[] cellOffsets = new ushort[_header.CellCount];

            for (ushort i = 0; i < _header.CellCount; i++)
            {
                cellOffsets[i] = reader.ReadUInt16();
            }

            for (ushort i = 0; i < _header.CellCount; i++)
            {
                reader.SeekPage(page);
                reader.Skip(cellOffsets[i]);

                switch (_header.Type)
                {
                    //case BTreeType.InteriorIndexBtreePage:
                    //    break;
                    //case BTreeType.InteriorTableBtreePage:
                    //    break;
                    //case BTreeType.LeafIndexBtreePage:
                    //    break;
                    case BTreeType.LeafTableBtreePage:

                        ReadTableBTreeLeafCell(reader);

                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        private void ReadTableBTreeLeafCell(ReaderBase reader)
        {
            var bytes = reader.ReadVarInt();
            var rowId = reader.ReadVarInt();

        }
    }

    internal struct BTreeHeader
    {
        public BTreeType Type;

        public ushort FirstFreeBlock;

        public ushort CellCount;

        public ushort CellContentBegin;

        public byte CellContentFragmentedFreeBytes;

        public uint RightMostPointer;

        public static BTreeHeader Parse(ReaderBase reader)
        {
            reader.CheckSize(8);

            BTreeHeader res = new BTreeHeader();

            res.Type = (BTreeType)reader.ReadByte();
            res.FirstFreeBlock = reader.ReadUInt16();
            res.CellCount = reader.ReadUInt16();
            res.CellContentBegin = reader.ReadUInt16();
            res.CellContentFragmentedFreeBytes = reader.ReadByte();

            if (res.Type == BTreeType.InteriorIndexBtreePage || res.Type == BTreeType.InteriorTableBtreePage)
            {
                reader.CheckSize(sizeof(uint));

                res.RightMostPointer = reader.ReadUInt32();
            }

            return res;
        }
    }

    internal enum BTreeType : byte
    {
        InteriorIndexBtreePage = 0x02,
        InteriorTableBtreePage = 0x05,
        LeafIndexBtreePage = 0x0a,
        LeafTableBtreePage = 0x0d
    }
}