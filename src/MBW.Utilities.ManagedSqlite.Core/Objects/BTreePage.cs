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
    /// <param name="isChild">The page is a child in a table b-tree, so an index page here is corruption rather than an unsupported table</param>
    internal static BTreePage Parse(ReaderBase reader, uint page, bool isChild = false)
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
            case BTreeType.LeafIndexBtreePage:
                if (isChild)
                    throw new SqliteCorruptException($"index b-tree page (type {(byte)header.Type}) inside a table b-tree", page, "b-tree page header");

                throw new SqliteException($"Page {page} is an index b-tree page. Index b-trees (indexes and WITHOUT ROWID tables) are not supported");
            case BTreeType.InteriorTableBtreePage:
                res = new BTreeInteriorTablePage(reader, page, header, cellOffsets);
                break;
            case BTreeType.LeafTableBtreePage:
                res = new BTreeLeafTablePage(reader, page, header, cellOffsets);
                break;
            default:
                throw new SqliteCorruptException($"unknown b-tree page type {(byte)header.Type}", page, "b-tree page header");
        }

        res.ValidateCellOffsets();

        try
        {
            res.ParseInternal();
        }
        catch (Exception e) when (CorruptionHelper.IsCorruptionShaped(e))
        {
            throw new SqliteCorruptException("unable to read a cell", page, "b-tree cell", e);
        }

        return res;
    }

    private void ValidateCellOffsets()
    {
        // https://www.sqlite.org/fileformat.html#b_tree_pages
        // The cell pointer array follows the page header, and every cell lies after it and inside the usable area
        uint headerStart = Page == 1 ? (uint)DatabaseHeader.HeaderSize : 0;
        uint headerSize = Header.Type == BTreeType.InteriorTableBtreePage ? 12u : 8u;
        uint cellsStart = headerStart + headerSize + 2u * Header.CellCount;
        uint usableSize = (uint)(Reader.PageSize - Reader.ReservedSpace);

        if (cellsStart > usableSize)
            throw new SqliteCorruptException($"cell count {Header.CellCount} does not fit in the page", Page, "b-tree page header");

        foreach (ushort offset in CellOffsets)
        {
            // A cell needs at least 4 bytes (an interior cell's child pointer, or a leaf cell's two varints and a record header)
            if (offset < cellsStart || offset + 4u > usableSize)
                throw new SqliteCorruptException($"cell offset {offset} is outside the cell content area ({cellsStart}..{usableSize})", Page, "cell pointer array");
        }
    }

    protected abstract void ParseInternal();
}