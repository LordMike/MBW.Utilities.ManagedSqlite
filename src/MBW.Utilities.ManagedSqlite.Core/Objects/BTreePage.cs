using System;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
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

    /// <param name="reader"></param>
    /// <param name="page"></param>
    /// <param name="parent">The kind of b-tree the page is a child in, so a page of the other kind is corruption. Null for a root page.</param>
    internal static BTreePage Parse(ReaderBase reader, uint page, BTreeKind? parent = null)
    {
        if (parent == BTreeKind.Unknown)
            throw new ArgumentOutOfRangeException(nameof(parent), parent, "Pass Table or Index for a child page, or null for a root page");

        // Read the page once, and parse it from memory
        byte[] data = reader.ReadPage(page);
        int length = (int)reader.PageSize;

        // Page 1 begins with the 100 byte database header
        int pos = page == 1 ? DatabaseHeader.HeaderSize : 0;

        BTreeHeader header = new BTreeHeader(data, length, ref pos);

        // The cell pointer array follows the header, make sure it fits before reading it. ValidateCellOffsets checks the rest.
        if (pos + 2L * header.CellCount > reader.PageSize - reader.ReservedSpace)
            throw new SqliteCorruptException($"cell count {header.CellCount} does not fit in the page", page, "b-tree page header");

        // Read cells. The cell pointer array is in key order, so it must not be re-ordered.
        ushort[] cellOffsets = new ushort[header.CellCount];

        for (ushort i = 0; i < header.CellCount; i++)
        {
            cellOffsets[i] = ByteArrayReader.ReadUInt16(data, length, ref pos);
        }

        switch (header.Type)
        {
            case BTreeType.InteriorIndexBtreePage:
            case BTreeType.LeafIndexBtreePage:
                if (parent == BTreeKind.Table)
                    throw new SqliteCorruptException($"index b-tree page (type {(byte)header.Type}) inside a table b-tree", page, "b-tree page header");
                break;
            case BTreeType.InteriorTableBtreePage:
            case BTreeType.LeafTableBtreePage:
                if (parent == BTreeKind.Index)
                    throw new SqliteCorruptException($"table b-tree page (type {(byte)header.Type}) inside an index b-tree", page, "b-tree page header");
                break;
            default:
                throw new SqliteCorruptException($"unknown b-tree page type {(byte)header.Type}", page, "b-tree page header");
        }

        ValidateCellOffsets(reader, page, header, cellOffsets);

        // The page constructors parse the cells
        try
        {
            switch (header.Type)
            {
                case BTreeType.InteriorIndexBtreePage:
                case BTreeType.LeafIndexBtreePage:
                    return new BTreeIndexPage(reader, page, header, cellOffsets, data, length);
                case BTreeType.InteriorTableBtreePage:
                    return new BTreeInteriorTablePage(reader, page, header, cellOffsets, data, length);
                default:
                    return new BTreeLeafTablePage(reader, page, header, cellOffsets, data, length);
            }
        }
        catch (Exception e) when (CorruptionHelper.IsCorruptionShaped(e))
        {
            throw new SqliteCorruptException("unable to read a cell", page, "b-tree cell", e);
        }
    }

    private static void ValidateCellOffsets(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets)
    {
        // https://www.sqlite.org/fileformat.html#b_tree_pages
        // The cell pointer array follows the page header, and every cell lies after it and inside the usable area
        uint headerStart = page == 1 ? (uint)DatabaseHeader.HeaderSize : 0;
        uint headerSize = header.Type == BTreeType.InteriorTableBtreePage || header.Type == BTreeType.InteriorIndexBtreePage ? 12u : 8u;
        uint cellsStart = headerStart + headerSize + 2u * header.CellCount;
        uint usableSize = (uint)(reader.PageSize - reader.ReservedSpace);

        if (cellsStart > usableSize)
            throw new SqliteCorruptException($"cell count {header.CellCount} does not fit in the page", page, "b-tree page header");

        foreach (ushort offset in cellOffsets)
        {
            // A cell needs at least 4 bytes (an interior cell's child pointer, or a leaf cell's two varints and a record header)
            if (offset < cellsStart || offset + 4u > usableSize)
                throw new SqliteCorruptException($"cell offset {offset} is outside the cell content area ({cellsStart}..{usableSize})", page, "cell pointer array");
        }
    }

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
