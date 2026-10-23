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

    /// <summary>
    /// The page's bytes. The page owns them: the cells, and the records of rows read from them, are read in place, so
    /// a row that is kept keeps its page's array alive.
    /// </summary>
    protected internal byte[] Data { get; }

    /// <summary>
    /// Where the cell pointer array starts, right after the page header
    /// </summary>
    private readonly int _cellPointers;

    protected BTreePage(ReaderBase reader, uint page, BTreeHeader header, byte[] data, int cellPointers)
    {
        Reader = reader;
        Page = page;
        Header = header;
        Data = data;
        _cellPointers = cellPointers;
    }

    /// <param name="reader"></param>
    /// <param name="page"></param>
    /// <param name="parent">The kind of b-tree the page is a child in, so a page of the other kind is corruption. Null for a root page.</param>
    /// <param name="buffer">The array to read the page into, which the page then owns; a new one when null</param>
    internal static BTreePage Parse(ReaderBase reader, uint page, BTreeKind? parent = null, byte[]? buffer = null)
    {
        if (parent == BTreeKind.Unknown)
            throw new ArgumentOutOfRangeException(nameof(parent), parent, "Pass Table or Index for a child page, or null for a root page");

        // Read the page once, and parse it from memory
        byte[] data = buffer ?? new byte[reader.PageSize];
        reader.ReadPage(page, data);
        int length = (int)reader.PageSize;

        // Page 1 begins with the 100 byte database header
        int pos = page == 1 ? DatabaseHeader.HeaderSize : 0;

        BTreeHeader header = new BTreeHeader(data, length, ref pos);

        // PAGE-03, INTEG-05: a page holds at most (pageSize-8)/6 cells, the most that fit with the smallest cell and its pointer.
        // btreeInitPage in btree.c:
        //   if( pPage->nCell>MX_CELL(pBt) ){
        //     /* To many cells for a single page.  The page must be corrupt */
        //     return SQLITE_CORRUPT_PAGE(pPage);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L2247 (btreeInitPage)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btreeInt.h#L229 (MX_CELL)
        // Strict in both modes: more pointers than that overlap the cells, which would then read as garbage. MX_CELL also
        // keeps the pointer array inside the usable area of every page, page 1 included.
        uint maxCells = (reader.PageSize - 8) / 6;
        Checks.Strict(header.CellCount <= maxCells, "PAGE-03",
            $"cell count {header.CellCount} is more than a page can hold ({maxCells})", page, "b-tree page header");

        // The cell pointer array follows the header, in key order, and is read in place (GetCellOffset). MX_CELL above
        // keeps it inside the page.
        int cellPointers = pos;

        // PAGE-01: the page type must be one of the four b-tree page types. decodeFlags in btree.c:
        //     }else{
        //       ...
        //       return SQLITE_CORRUPT_PAGE(pPage);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L2081 (decodeFlags)
        // PAGE-22: a child page must be of its parent's kind, table or index. moveToChild in btree.c:
        //   if( rc==SQLITE_OK
        //    && (pCur->pPage->nCell<1 || pCur->pPage->intKey!=pCur->curIntKey)
        //   ){
        //     releasePage(pCur->pPage);
        //     rc = SQLITE_CORRUPT_PGNO(newPgno);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5472 (moveToChild)
        switch (header.Type)
        {
            case BTreeType.InteriorIndexBtreePage:
            case BTreeType.LeafIndexBtreePage:
                Checks.Strict(parent != BTreeKind.Table, "PAGE-22",
                    $"index b-tree page (type {(byte)header.Type}) inside a table b-tree", page, "b-tree page header");
                break;
            case BTreeType.InteriorTableBtreePage:
            case BTreeType.LeafTableBtreePage:
                Checks.Strict(parent != BTreeKind.Index, "PAGE-22",
                    $"table b-tree page (type {(byte)header.Type}) inside an index b-tree", page, "b-tree page header");
                break;
            default:
                Checks.Fail("PAGE-01", $"unknown b-tree page type {(byte)header.Type}", page, "b-tree page header");
                break;
        }

        // PAGE-11, PAGE-12, CELL-08, CELL-09: the cells are not validated when the page loads. SQLite checks that every
        // cell lies in the content area only with PRAGMA cell_size_check=ON, which is off by default, and in
        // integrity_check. Each cell is parsed when it is reached, see GetCellOffset.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L2261 (btreeInitPage)
        switch (header.Type)
        {
            case BTreeType.InteriorIndexBtreePage:
            case BTreeType.LeafIndexBtreePage:
                return new BTreeIndexPage(reader, page, header, data, cellPointers);
            case BTreeType.InteriorTableBtreePage:
                return new BTreeInteriorTablePage(reader, page, header, data, cellPointers);
            default:
                return new BTreeLeafTablePage(reader, page, header, data, cellPointers);
        }
    }

    public int CellCount => Header.CellCount;

    /// <summary>
    /// Where a cell's payload is and how large it is, see <see cref="PayloadSizeOnPage"/>
    /// </summary>
    public struct PayloadCell
    {
        /// <summary>
        /// Where the payload starts on the page, after the cell's header
        /// </summary>
        public uint PayloadOffset;

        /// <summary>
        /// The size of the whole payload, including the part on overflow pages
        /// </summary>
        public uint DataSize;

        /// <summary>
        /// The size of the payload's part on this page. When it is less than <see cref="DataSize"/>, the first overflow
        /// page's number follows it.
        /// </summary>
        // Maximum cell size is slightly less than one page, and pages can be up to 65536 bytes
        public uint DataSizeInCell;
    }

    /// <summary>
    /// Where cell <paramref name="index"/> starts. SQLite masks the cell pointer to the page size and reads whatever is
    /// there, without checking that it points into the cell content area. findCell in btree.c:
    ///   ((P)->aData + ((P)->maskPage &amp; get2byteAligned(&amp;(P)->aCellIdx[2*(I)])))
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L1166 (findCell)
    /// </summary>
    protected int GetCellOffset(int index)
    {
        int pos = _cellPointers + 2 * index;
        return ((Data[pos] << 8) | Data[pos + 1]) & (int)(Reader.PageSize - 1);
    }

    /// <summary>
    /// The left child pointer of cell <paramref name="index"/> of an interior page, read when the walk reaches it, as
    /// SQLite reads it (moveToLeftmost: get4byte(findCell(pPage, pCur->ix))). A pointer that would run past the page
    /// fails then, and not before.
    /// </summary>
    protected uint ReadChildPointer(int index)
    {
        int pos = GetCellOffset(index);
        CheckInPage(pos + 4L, pos);
        return (uint)(Data[pos] << 24 | Data[pos + 1] << 16 | Data[pos + 2] << 8 | Data[pos + 3]);
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
    protected uint ReadPayloadSize(byte[] data, ref int pos, int cellStart)
    {
        uint value = 0;
        for (int i = 0; i < 9; i++)
        {
            CheckInPage(pos + 1L, cellStart);
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
    protected long ReadCellVarInt(byte[] data, ref int pos, int cellStart)
    {
        long value = 0;
        for (int i = 0; i < 8; i++)
        {
            CheckInPage(pos + 1L, cellStart);
            byte b = data[pos++];
            value = (value << 7) | (long)(b & 0x7F);
            if (b < 0x80)
                return value;
        }

        CheckInPage(pos + 1L, cellStart);
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
