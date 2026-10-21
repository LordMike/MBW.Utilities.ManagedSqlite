using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Objects;

/// <summary>
/// SQLite B-Tree datastructure that cells with data
/// </summary>
internal class BTreeLeafTablePage : BTreePage
{
    public BTreeLeafTablePage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets)
        : base(reader, page, header, cellOffsets)
    {
    }

    /// <summary>
    /// Parses cell <paramref name="index"/> when it is reached, as SQLite does (btreeParseCellPtr in btree.c)
    /// </summary>
    public Cell GetCell(int index)
    {
        byte[] data = GetData();
        int pos = GetCellOffset(index);

        uint bytes = ReadPayloadSize(data, ref pos);
        long rowId = ReadCellVarInt(data, ref pos);

        return new Cell
        {
            PayloadOffset = (uint)pos,
            DataSize = bytes,
            DataSizeInCell = PayloadSizeOnPage(bytes, true),
            RowId = rowId
        };
    }

    public struct Cell
    {
        /// <summary>
        /// Where the payload starts on the page, after the cell's two varints
        /// </summary>
        public uint PayloadOffset;

        /// <summary>
        /// The size of the data in the cell (including data in overflow), excluding the header and overfow page pointers
        /// </summary>
        public uint DataSize;

        /// <summary>
        /// The size of the data in the cell (in the first page only), excluding the header and overfow page pointers.
        /// When it is less than <see cref="DataSize"/>, the first overflow page's number follows it.
        /// </summary>
        // Maximum cell size is slightly less than one page, and pages can be up to 65536 bytes
        public uint DataSizeInCell;

        public long RowId;
    }
}
