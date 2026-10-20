using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Objects;

/// <summary>
/// SQLite B-Tree datastructure that cells with data
/// </summary>
internal class BTreeLeafTablePage : BTreePage
{
    public Cell[] Cells { get; private set; }

    public BTreeLeafTablePage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets)
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

            long bytes = ByteArrayReader.ReadVarInt(data, length, ref pos);
            int bytesSize = pos - CellOffsets[i];
            long rowId = ByteArrayReader.ReadVarInt(data, length, ref pos);
            int rowIdSize = pos - CellOffsets[i] - bytesSize;

            uint overflowPage = 0;
            uint bytesInCell = PayloadSizeOnPage(bytes, true);

            if (bytes > bytesInCell)
            {
                // We have overflow
                pos += (int)bytesInCell;
                overflowPage = ByteArrayReader.ReadUInt32(data, length, ref pos);
            }

            long cellEnd = (long)CellOffsets[i] + bytesSize + rowIdSize + bytesInCell + (bytes > bytesInCell ? sizeof(uint) : 0);
            if (bytes < 0 || cellEnd > U)
                throw new SqliteCorruptException($"cell with payload size {bytes} at offset {CellOffsets[i]} extends past the end of the page", Page, "b-tree leaf cell");

            Cells[i] = new Cell
            {
                CellHeaderSize = (byte)(bytesSize + rowIdSize),
                DataSize = bytes,
                DataSizeInCell = bytesInCell,
                RowId = rowId,
                FirstOverflowPage = overflowPage
            };
        }
    }

    public struct Cell
    {
        /// <summary>
        /// The size of the data in the cell (including data in overflow), excluding the header and overfow page pointers
        /// </summary>
        public long DataSize;

        /// <summary>
        /// The size of the data in the cell (in the first page only), excluding the header and overfow page pointers
        /// </summary>
        // Maximum cell size is slightly less than one page, and pages can be up to 65536 bytes
        public uint DataSizeInCell;

        public long RowId;
        public uint FirstOverflowPage;

        /// <summary>
        /// The size of the two VarInts in the beginning of the cell
        /// </summary>
        public byte CellHeaderSize;
    }
}