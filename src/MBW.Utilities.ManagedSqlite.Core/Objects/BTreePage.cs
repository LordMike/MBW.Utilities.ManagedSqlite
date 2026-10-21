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

        // PAGE-11, PAGE-12, CELL-08, CELL-09: the cells are not validated when the page loads. SQLite checks that every
        // cell lies in the content area only with PRAGMA cell_size_check=ON, which is off by default, and in
        // integrity_check. Each cell is parsed when it is reached, see GetCellOffset.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L2261 (btreeInitPage)
        switch (header.Type)
        {
            case BTreeType.InteriorIndexBtreePage:
            case BTreeType.LeafIndexBtreePage:
                return new BTreeIndexPage(reader, page, header, cellOffsets);
            case BTreeType.InteriorTableBtreePage:
                return new BTreeInteriorTablePage(reader, page, header, cellOffsets);
            default:
                return new BTreeLeafTablePage(reader, page, header, cellOffsets);
        }
    }

    public int CellCount => CellOffsets.Length;

    /// <summary>
    /// Where cell <paramref name="index"/> starts. SQLite masks the cell pointer to the page size and reads whatever is
    /// there, without checking that it points into the cell content area. findCell in btree.c:
    ///   ((P)->aData + ((P)->maskPage &amp; get2byteAligned(&amp;(P)->aCellIdx[2*(I)])))
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L1166 (findCell)
    /// </summary>
    protected int GetCellOffset(int index) => CellOffsets[index] & (int)(Reader.PageSize - 1);

    /// <summary>
    /// The page's bytes, read through the reader's page buffer
    /// </summary>
    protected byte[] GetData() => Reader.GetPage(Page);

    /// <summary>
    /// A 4-byte big-endian integer of a cell, such as an interior cell's child pointer
    /// </summary>
    protected uint ReadCellUInt32(byte[] data, int pos)
    {
        CheckInPage(pos + 4L, pos);
        return (uint)(data[pos] << 24 | data[pos + 1] << 16 | data[pos + 2] << 8 | data[pos + 3]);
    }

    /// <summary>
    /// A cell's payload size. CELL-01, CELL-02: SQLite reads up to 9 bytes of 7 bits each, the 9th too, and keeps the low
    /// 32 bits, so a size of 2^32 or more wraps instead of failing. btreeParseCellPtr in btree.c:
    ///   do{
    ///     nPayload = (nPayload&lt;&lt;7) | (*++pIter &amp; 0x7f);
    ///   }while( (*pIter)>=0x80 &amp;&amp; pIter&lt;pEnd );
    ///   nPayload &amp;= 0xffffffff;
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L1259 (btreeParseCellPtr)
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L1347 (btreeParseCellPtrIndex)
    /// </summary>
    protected uint ReadPayloadSize(byte[] data, ref int pos)
    {
        int start = pos;
        uint value = 0;
        for (int i = 0; i < 9; i++)
        {
            CheckInPage(pos + 1L, start);
            byte b = data[pos++];
            value = (value << 7) | (uint)(b & 0x7F);
            if (b < 0x80)
                break;
        }

        return value;
    }

    /// <summary>
    /// A standard varint of a cell, such as a rowid
    /// </summary>
    protected long ReadCellVarInt(byte[] data, ref int pos)
    {
        int start = pos;
        long value = 0;
        for (int i = 0; i < 8; i++)
        {
            CheckInPage(pos + 1L, start);
            byte b = data[pos++];
            value = (value << 7) | (long)(b & 0x7F);
            if (b < 0x80)
                return value;
        }

        CheckInPage(pos + 1L, start);
        return (value << 8) | data[pos++];
    }

    /// <summary>
    /// CELL-10: a cell must not run past the end of its page. SQLite reads the memory after a page's bytes for a cell
    /// header that does, so this has no exact equivalent; it fails as SQLite fails when that cell's payload is read.
    /// </summary>
    protected void CheckInPage(long end, int cellStart) =>
        Checks.Strict(end <= Reader.PageSize, "CELL-10", $"the cell at offset {cellStart} runs past the end of the page", Page, "b-tree cell");

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
