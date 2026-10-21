using System;
using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public sealed class Sqlite3Table
{
    private readonly ReaderBase _reader;

    private BTreePage RootPage { get; }
    public Sqlite3SchemaRow SchemaDefinition { get; }

    /// <summary>
    /// True when the table's root page is an index b-tree page, which is how SQLite stores a WITHOUT ROWID table. A
    /// rowid table's root page is a table b-tree page. This comes from the root page alone: the core does not parse
    /// the schema SQL, so it can't tell when the two disagree, which SQLite reports as corrupt. The Sql package checks
    /// that when it pairs a table with its definition.
    /// </summary>
    public bool IsWithoutRowId => RootPage is BTreeIndexPage;

    internal Sqlite3Table(ReaderBase reader, BTreePage rootPage, Sqlite3SchemaRow table)
    {
        SchemaDefinition = table;
        _reader = reader;
        RootPage = rootPage;
    }

    /// <summary>
    /// Enumerates the table's rows in the order SQLite stores them: rowid order, or primary key order for a WITHOUT
    /// ROWID table. Each row's <see cref="Sqlite3Row.ColumnData"/> is its record as stored; for a WITHOUT ROWID table
    /// that is the primary key columns first, then the others, see <see cref="Sqlite3Row"/>.
    /// </summary>
    /// <exception cref="SqliteCorruptException">The table's b-tree or one of its records is malformed</exception>
    public IEnumerable<Sqlite3Row> EnumerateRows()
    {
        using IEnumerator<Sqlite3Row> rows = EnumerateRowsInternal().GetEnumerator();

        while (true)
        {
            try
            {
                if (!rows.MoveNext())
                    yield break;
            }
            catch (Exception e) when (CorruptionHelper.IsCorruptionShaped(e))
            {
                throw new SqliteCorruptException($"unable to read the rows of table {SchemaDefinition.Name}", structure: "table b-tree", innerException: e);
            }

            yield return rows.Current;
        }
    }

    private IEnumerable<Sqlite3Row> EnumerateRowsInternal()
    {
        // A WITHOUT ROWID table is stored in an index b-tree
        if (RootPage is BTreeIndexPage indexRoot)
            return EnumerateWithoutRowIdRows(indexRoot);

        return EnumerateRowIdRows();
    }

    private IEnumerable<Sqlite3Row> EnumerateRowIdRows()
    {
        foreach (BTreeLeafTablePage leaf in BTreeTools.WalkTableBTreeLeaves(RootPage))
        {
            for (int i = 0; i < leaf.CellCount; i++)
            {
                BTreePage.PayloadCell cell = leaf.GetCell(i, out long rowId);
                byte[] payload = ReadPayload(leaf, rowId, cell);

                yield return new Sqlite3Row(this, rowId, new Sqlite3Record(_reader, leaf.Page, rowId, payload, (int)cell.DataSize));
            }
        }
    }

    private IEnumerable<Sqlite3Row> EnumerateWithoutRowIdRows(BTreeIndexPage root)
    {
        foreach (KeyValuePair<BTreeIndexPage, int> entry in BTreeTools.WalkIndexBTreeCells(root))
        {
            BTreeIndexPage page = entry.Key;
            BTreePage.PayloadCell cell = page.GetCell(entry.Value);
            byte[] payload = ReadPayload(page, null, cell);

            // There is no rowid
            yield return new Sqlite3Row(this, 0, new Sqlite3Record(_reader, page.Page, null, payload, (int)cell.DataSize));
        }
    }

    /// <summary>
    /// Reads a cell's whole payload once, so the row can decode it from memory when its values are read.
    /// <paramref name="rowId"/> names the row in error messages, and is null in a WITHOUT ROWID table.
    /// </summary>
    private byte[] ReadPayload(BTreePage page, long? rowId, BTreePage.PayloadCell cell)
    {
        string structure = page is BTreeIndexPage { IsLeaf: false } ? "b-tree interior cell" : "b-tree leaf cell";
        long usableSize = (long)_reader.PageSize - _reader.ReservedSpace;
        bool overflows = cell.DataSize > cell.DataSizeInCell;
        long localEnd = (long)cell.PayloadOffset + cell.DataSizeInCell;

        // CELL-10, CELL-11: the payload's bytes on the page must end inside the usable area when SQLite reads them
        // through accessPayload, which a full row read does when the payload overflows or runs past the page:
        //   if( (uptr)(aPayload - pPage->aData) > (pBt->usableSize - pCur->info.nLocal) ){
        //     ...
        //     return SQLITE_CORRUPT_PAGE(pPage);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5154 (accessPayload)
        // A payload that is all on the page and inside it is read straight from the page instead, even over the reserved
        // bytes: fetchPayload only clamps it to the page end ("amt = MAX(0, (int)(pCur->pPage->aDataEnd - ...))").
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5401 (fetchPayload)
        // SQLite does this per column, for the columns a statement reads past the local bytes; this reader reads the
        // whole payload of a row, so it checks once per row.
        Checks.Strict(localEnd <= usableSize || (!overflows && localEnd <= _reader.PageSize), "CELL-10",
            $"the payload of {RowName(rowId)} at offset {cell.PayloadOffset} runs past the end of the page ({cell.DataSizeInCell} bytes on the page, {usableSize} usable)",
            page.Page, structure);

        // A payload can't be larger than the pages that could hold it, nor larger than SQLite's maximum length
        long maxPayload = Math.Min(int.MaxValue, cell.DataSizeInCell + _reader.PageCount * (usableSize - 4));
        if (cell.DataSize > maxPayload)
            throw new SqliteCorruptException($"payload size {cell.DataSize} of {RowName(rowId)} is larger than the database", page.Page, structure);

        int payloadSize = (int)cell.DataSize;

        // REC-04: SQLite reads the header size of a 0-byte payload from the byte after the cell (aOffset[0] =
        // pC->aRow[0] in OP_Column, vdbe.c), so a 0 there is a record of no fields and anything else is corrupt. That
        // byte is the one at the payload's offset. A damaged page can put that offset at the very end of the page,
        // where SQLite reads whatever memory follows the page; this reads a 0 there.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3048 (OP_Column)
        if (payloadSize == 0)
        {
            byte[] next = new byte[1];
            if (cell.PayloadOffset < _reader.PageSize)
                _reader.ReadFromPage(page.Page, cell.PayloadOffset, next, 0, 1);

            return next;
        }

        byte[] payload = new byte[payloadSize];

        if (!overflows)
        {
            // All data is on this page, which the reader has in memory
            _reader.ReadFromPage(page.Page, cell.PayloadOffset, payload, 0, payloadSize);
            return payload;
        }

        // The first overflow page's number follows the local bytes. accessPayload reads it with get4byte, past the
        // usable size when the reserved space is under 4 bytes, so only the page end limits it.
        Checks.Strict(localEnd + 4 <= _reader.PageSize, "CELL-10",
            $"the overflow page number of {RowName(rowId)} at offset {localEnd} runs past the end of the page", page.Page, structure);
        uint firstOverflowPage = _reader.ReadUInt32FromPage(page.Page, (uint)localEnd);

        // CELL-13: the payload spills past this page, but there is no overflow chain to hold the rest
        Checks.Strict(firstOverflowPage != 0, "CELL-13",
            $"the overflow chain of {RowName(rowId)} ended with {cell.DataSize - cell.DataSizeInCell} bytes of the payload left", page.Page, "overflow chain");

        // The data continues on overflow pages
        using SqliteDataStream dataStream = new SqliteDataStream(_reader, page.Page, cell.PayloadOffset,
            cell.DataSizeInCell, firstOverflowPage, cell.DataSize);

        if (dataStream.ReadFully(payload, 0, payloadSize) != payloadSize)
            throw new EndOfStreamException();

        return payload;
    }

    private static string RowName(long? rowId) => rowId.HasValue ? $"row {rowId.Value}" : "a WITHOUT ROWID row";
}
