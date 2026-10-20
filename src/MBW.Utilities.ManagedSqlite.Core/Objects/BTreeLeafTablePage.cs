using System;
using System.Diagnostics.CodeAnalysis;
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

    public BTreeLeafTablePage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets, byte[] data, int length)
        : base(reader, page, header, cellOffsets)
    {
        ParseCells(data, length);
    }

    [MemberNotNull(nameof(Cells))]
    private void ParseCells(byte[] data, int length)
    {
        Cells = new Cell[CellOffsets.Length];

        for (int i = 0; i < Cells.Length; i++)
        {
            int pos = CellOffsets[i];

            long bytes = ByteArrayReader.ReadVarInt(data, length, ref pos);
            int bytesSize = pos - CellOffsets[i];
            long rowId = ByteArrayReader.ReadVarInt(data, length, ref pos);
            int rowIdSize = pos - CellOffsets[i] - bytesSize;

            uint overflowPage = 0;

            // Calculate overflow size
            long P = bytes;

            // let U be the usable size of a database page, the total page size less the reserved space at the end of each page
            int U = (int)Reader.PageSize - Reader.ReservedSpace;

            // X is U-35 for table btree leaf pages or ((U-12)*64/255)-23 for index pages.
            int X = U - 35;

            // M is always ((U-12)*32/255)-23.
            int M = (U - 12) * 32 / 255 - 23;

            // Let K be M+((P-M)%(U-4)).
            int K = (int)(M + ((P - M) % (U - 4)));

            // If P<=X then all P bytes of payload are stored directly on the btree page without overflow.
            // If P>X and K<=X then the first K bytes of P are stored on the btree page and the remaining P-K bytes are stored on overflow pages.
            // If P>X and K>X then the first M bytes of P are stored on the btree page and the remaining P-M bytes are stored on overflow pages.
            // The number of bytes stored on the leaf page is never less than M.

            uint bytesInCell;

            if (P <= X)
            {
                // All data is in cell
                bytesInCell = (uint)P;
            }
            else if (P > X && K <= X)
            {
                bytesInCell = (uint)K;
            }
            else if (P > X && K > X)
            {
                bytesInCell = (uint)M;
            }
            else
            {
                throw new InvalidOperationException("We're not supposed to be here");
            }

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