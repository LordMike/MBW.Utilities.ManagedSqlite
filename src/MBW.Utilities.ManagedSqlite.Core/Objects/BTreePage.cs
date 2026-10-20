using System;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Objects;

internal abstract class BTreePage
{
    public uint Page { get; }

    protected internal ReaderBase Reader { get; }
    protected internal BTreeHeader Header { get; }

    protected internal ushort[] CellOffsets { get; }

    protected BTreePage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets)
    {
        Reader = reader;
        Page = page;
        Header = header;
        CellOffsets = cellOffsets;
    }

    internal static BTreePage Parse(ReaderBase reader, uint page)
    {
        // Read the page once, and parse it from memory
        byte[] data = reader.ReadPage(page);
        int length = (int)reader.PageSize;

        // Page 1 begins with the 100 byte database header
        int pos = page == 1 ? DatabaseHeader.HeaderSize : 0;

        BTreeHeader header = BTreeHeader.Parse(data, length, ref pos);

        // Read cells. The cell pointer array is in key order, so it must not be re-ordered.
        ushort[] cellOffsets = new ushort[header.CellCount];

        for (ushort i = 0; i < header.CellCount; i++)
        {
            cellOffsets[i] = ByteArrayReader.ReadUInt16(data, length, ref pos);
        }

        BTreePage res;
        switch (header.Type)
        {
            case BTreeType.InteriorIndexBtreePage:
            case BTreeType.LeafIndexBtreePage:
                res = new BTreeIndexPage(reader, page, header, cellOffsets);
                break;
            case BTreeType.InteriorTableBtreePage:
                res = new BTreeInteriorTablePage(reader, page, header, cellOffsets);
                break;
            case BTreeType.LeafTableBtreePage:
                res = new BTreeLeafTablePage(reader, page, header, cellOffsets);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        res.ParseInternal(data, length);

        return res;
    }

    protected abstract void ParseInternal(byte[] data, int length);

    /// <summary>
    /// How many of a cell's <paramref name="payloadSize"/> bytes are stored on its b-tree page, the rest being on
    /// overflow pages. See https://www.sqlite.org/fileformat2.html#b_tree_pages
    /// </summary>
    protected uint PayloadSizeOnPage(long payloadSize, bool tableLeaf)
    {
        long P = payloadSize;

        // Let U be the usable size of a database page, the total page size less the reserved space at the end of each page
        int U = (int)Reader.PageSize - Reader.ReservedSpace;

        // X is U-35 for table btree leaf pages or ((U-12)*64/255)-23 for index pages.
        int X = tableLeaf ? U - 35 : (U - 12) * 64 / 255 - 23;

        // M is always ((U-12)*32/255)-23.
        int M = (U - 12) * 32 / 255 - 23;

        // Let K be M+((P-M)%(U-4)).
        int K = (int)(M + ((P - M) % (U - 4)));

        // If P<=X then all P bytes of payload are stored directly on the btree page without overflow.
        // If P>X and K<=X then the first K bytes of P are stored on the btree page and the remaining P-K bytes are stored on overflow pages.
        // If P>X and K>X then the first M bytes of P are stored on the btree page and the remaining P-M bytes are stored on overflow pages.
        if (P <= X)
            return (uint)P;

        return (uint)(K <= X ? K : M);
    }
}