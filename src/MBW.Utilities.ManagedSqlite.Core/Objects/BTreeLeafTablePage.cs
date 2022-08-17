using System;
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

    protected override void ParseInternal()
    {
        Cells = new Cell[CellOffsets.Length];

        for (int i = 0; i < Cells.Length; i++)
        {
            Reader.SeekPage(Page, CellOffsets[i]);

            long bytes = Reader.ReadVarInt(out byte bytesSize);
            long rowId = Reader.ReadVarInt(out byte rowIdSize);

            uint overflowPage = 0;

            // Calculate overflow size
            long P = bytes;

            // let U be the usable size of a database page, the total page size less the reserved space at the end of each page
            int U = Reader.PageSize - Reader.ReservedSpace;

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

            ushort bytesInCell;

            if (P <= X)
            {
                // All data is in cell
                bytesInCell = (ushort)P;
            }
            else if (P > X && K <= X)
            {
                bytesInCell = (ushort)K;
            }
            else if (P > X && K > X)
            {
                bytesInCell = (ushort)M;
            }
            else
            {
                throw new InvalidOperationException("We're not supposed to be here");
            }

            if (bytes > bytesInCell)
            {
                // We have overflow
                Reader.Skip(bytesInCell);
                overflowPage = Reader.ReadUInt32();
            }

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
        // Maximum cell size slightly less than one page, which is 65K
        public ushort DataSizeInCell;

        public long RowId;
        public uint FirstOverflowPage;

        /// <summary>
        /// The size of the two VarInts in the beginning of the cell
        /// </summary>
        public byte CellHeaderSize;
    }
}