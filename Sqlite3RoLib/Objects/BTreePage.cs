using System;
using Sqlite3RoLib.Objects.Enums;
using Sqlite3RoLib.Objects.Headers;

namespace Sqlite3RoLib.Objects
{
    internal abstract class BTreePage
    {
        protected internal uint Page { get; }
        protected internal ReaderBase Reader { get; }
        protected BTreeHeader Header { get; }

        protected internal ushort[] CellOffsets { get; }

        protected BTreePage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets)
        {
            Reader = reader;
            Page = page;
            Header = header;
            CellOffsets = cellOffsets;
        }

        public static BTreePage Parse(ReaderBase reader, uint page)
        {
            // Read header
            reader.SeekPage(page);

            if (page == 1)
            {
                // Skip the first 100 bytes
                reader.Skip(DatabaseHeader.HeaderSize);
            }

            BTreeHeader header = BTreeHeader.Parse(reader);

            // Read cells
            ushort[] cellOffsets = new ushort[header.CellCount];

            for (ushort i = 0; i < header.CellCount; i++)
            {
                cellOffsets[i] = reader.ReadUInt16();
            }

            Array.Sort(cellOffsets);

            BTreePage res;
            switch (header.Type)
            {
                case BTreeType.InteriorIndexBtreePage:
                    throw new ArgumentOutOfRangeException();
                    break;
                case BTreeType.InteriorTableBtreePage:
                    res = new BTreeInteriorTablePage(reader, page, header, cellOffsets);
                    break;
                case BTreeType.LeafIndexBtreePage:
                    throw new ArgumentOutOfRangeException();
                    break;
                case BTreeType.LeafTableBtreePage:
                    res = new BTreeLeafTablePage(reader, page, header, cellOffsets);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            res.ParseInternal();

            return res;
        }

        protected abstract void ParseInternal();
    }
}