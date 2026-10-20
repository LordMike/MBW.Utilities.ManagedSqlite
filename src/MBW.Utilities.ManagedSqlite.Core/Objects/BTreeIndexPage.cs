using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Objects;

/// <summary>
/// SQLite index b-tree page, interior or leaf. A WITHOUT ROWID table is stored as an index b-tree keyed by its primary
/// key, and every cell's payload is a whole row, in interior cells too. There is no rowid.
/// </summary>
internal class BTreeIndexPage : BTreePage
{
    public Cell[] Cells { get; private set; }

    public bool IsLeaf => Header.Type == BTreeType.LeafIndexBtreePage;

    public BTreeIndexPage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets)
        : base(reader, page, header, cellOffsets)
    {
    }

    protected override void ParseInternal(byte[] data, int length)
    {
        Cells = new Cell[CellOffsets.Length];

        // The usable size of a page, the page size less the reserved space at the end of each page
        int U = (int)Reader.PageSize - Reader.ReservedSpace;

        for (int i = 0; i < Cells.Length; i++)
        {
            int pos = CellOffsets[i];

            // Interior cells begin with the left child pointer, leaf cells with the payload size
            uint leftPagePointer = IsLeaf ? 0 : ByteArrayReader.ReadUInt32(data, length, ref pos);
            long bytes = ByteArrayReader.ReadVarInt(data, length, ref pos);
            int headerSize = pos - CellOffsets[i];

            uint bytesInCell = PayloadSizeOnPage(bytes, false);

            uint overflowPage = 0;
            if (bytes > bytesInCell)
            {
                pos += (int)bytesInCell;
                overflowPage = ByteArrayReader.ReadUInt32(data, length, ref pos);
            }

            long cellEnd = (long)CellOffsets[i] + headerSize + bytesInCell + (bytes > bytesInCell ? sizeof(uint) : 0);
            if (bytes < 0 || cellEnd > U)
                throw new SqliteCorruptException($"cell with payload size {bytes} at offset {CellOffsets[i]} extends past the end of the page", Page, IsLeaf ? "b-tree leaf cell" : "b-tree interior cell");

            Cells[i] = new Cell
            {
                LeftPagePointer = leftPagePointer,
                CellHeaderSize = (byte)headerSize,
                DataSize = bytes,
                DataSizeInCell = bytesInCell,
                FirstOverflowPage = overflowPage
            };
        }
    }

    public struct Cell
    {
        /// <summary>
        /// The child page with the keys that sort before this cell's, 0 on a leaf page
        /// </summary>
        public uint LeftPagePointer;

        /// <inheritdoc cref="BTreeLeafTablePage.Cell.DataSize"/>
        public long DataSize;

        /// <inheritdoc cref="BTreeLeafTablePage.Cell.DataSizeInCell"/>
        public uint DataSizeInCell;

        public uint FirstOverflowPage;

        /// <summary>
        /// The size of the left child pointer (interior cells only) and payload size varint before the payload
        /// </summary>
        public byte CellHeaderSize;
    }
}
