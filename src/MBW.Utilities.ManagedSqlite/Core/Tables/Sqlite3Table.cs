using System;
using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

/// <summary>
/// A table of the database, opened with <see cref="Sqlite3Database.GetTable"/> or
/// <see cref="Sqlite3Database.TryGetTable"/>. Its rows are read with <see cref="EnumerateRows()"/>, and its columns with
/// <see cref="SqlExtensions.GetTableDefinition(Sqlite3Table)"/>.
/// </summary>
public sealed class Sqlite3Table
{
    private readonly ReaderBase _reader;

    internal ReaderBase Reader => _reader;

    /// <summary>
    /// The table's definition, kept once <see cref="SqlExtensions.GetTableDefinition(Sqlite3Table)"/> read it
    /// without error, so reading values by name does not look it up for each value
    /// </summary>
    internal Sql.SqlTableDefinition? SqlDefinition { get; set; }

    private BTreePage? _rootPage;

    /// <summary>
    /// The table's row in the schema table, <c>sqlite_master</c>
    /// </summary>
    public Sqlite3SchemaRow SchemaDefinition { get; }

    /// <summary>
    /// True when the table's root page is an index b-tree page, which is how SQLite stores a WITHOUT ROWID table. A
    /// rowid table's root page is a table b-tree page. A root page that disagrees with the schema SQL is reported as
    /// corrupt when the table is opened, as SQLite does when it reads the table.
    /// </summary>
    public bool IsWithoutRowId { get; }

    /// <param name="reader"></param>
    /// <param name="rootPage">The root page, parsed when the table is opened, so it can be checked then. When it is in the
    /// reader's page buffer, the table reads it again, into an array of its own, when its rows are first read.</param>
    /// <param name="table"></param>
    internal Sqlite3Table(ReaderBase reader, BTreePage rootPage, Sqlite3SchemaRow table)
    {
        SchemaDefinition = table;
        _reader = reader;
        IsWithoutRowId = rootPage is BTreeIndexPage;

        if (!reader.IsPageBuffer(rootPage.Data))
            _rootPage = rootPage;
    }

    /// <summary>
    /// The root page, read once into an array that the table keeps, so a table that fits on its root page reads its
    /// rows without reading or allocating a page again
    /// </summary>
    private BTreePage RootPage => _rootPage ??= BTreePage.Parse(_reader, SchemaDefinition.RootPage);

    /// <summary>
    /// Enumerates the table's rows in the order SQLite stores them: rowid order, or primary key order for a WITHOUT
    /// ROWID table. Each row's <see cref="Sqlite3Row.ColumnData"/> is its record as stored; for a WITHOUT ROWID table
    /// that is the primary key columns first, then the others, see <see cref="Sqlite3Row"/>.
    /// </summary>
    /// <exception cref="SqliteCorruptException">The table's b-tree or one of its records is malformed</exception>
    public IEnumerable<Sqlite3Row> EnumerateRows() => EnumerateRows(false);

    /// <summary>
    /// Enumerates the table's rows as <see cref="EnumerateRows()"/> does. With <paramref name="shareRowBuffers"/>, the
    /// rows share one field table, which saves an allocation per row.
    /// </summary>
    /// <param name="shareRowBuffers">
    /// True to share one field table among all the rows of this enumeration. This is only safe when each row is read
    /// inside the loop that enumerates it, and is not kept or read after the enumeration moves to the next row.
    /// </param>
    /// <remarks>
    /// <para>
    /// A row finds its values through a table of the record's fields, which it builds on its first read. Normally each
    /// row allocates its own: at least 8 bytes for each column (16 for a long TEXT or BLOB), about 100 bytes per row
    /// for a 10 column table. With <paramref name="shareRowBuffers"/> the rows of one enumeration take turns with a
    /// single table, which each new row takes over. The table grows to the widest record, and lives as long as the
    /// enumeration, or any row that read a field from it.
    /// </para>
    /// <para>
    /// The cost is that a row is only readable while it is the latest one. Once the enumeration moves on, every read of
    /// an earlier row (<see cref="Sqlite3Row.TryGetOrdinal(ushort, out object?)"/>, the typed reads,
    /// <see cref="Sqlite3Row.ColumnData"/>) throws <see cref="InvalidOperationException"/>. Its values are not read
    /// from the wrong row. Don't collect these rows in a list, and don't keep them past the loop body, for example with
    /// <c>ToList()</c>, <c>OrderBy()</c> or a <c>yield return</c>. To keep a row's values, read
    /// <see cref="Sqlite3Row.ColumnData"/> inside the loop: a row that has read it keeps its own values, and stays
    /// readable.
    /// </para>
    /// <para>
    /// This pays off most with the typed reads (<see cref="Sqlite3Row.TryGetInt64"/>,
    /// <see cref="Sqlite3Row.TryGetDouble"/>, <see cref="Sqlite3Row.TryGetString"/>), which box nothing, so the field
    /// table is then a large part of what a row allocates.
    /// </para>
    /// </remarks>
    /// <exception cref="SqliteCorruptException">The table's b-tree or one of its records is malformed</exception>
    public IEnumerable<Sqlite3Row> EnumerateRows(bool shareRowBuffers)
    {
        // A WITHOUT ROWID table is stored in an index b-tree
        return IsWithoutRowId ? EnumerateWithoutRowIdRows(shareRowBuffers) : EnumerateRowIdRows(shareRowBuffers);
    }

    private IEnumerable<Sqlite3Row> EnumerateRowIdRows(bool shareRowBuffers)
    {
        // Created inside the iterator, so each enumeration has its own field table
        Sqlite3Record.SharedFields? shared = shareRowBuffers ? new Sqlite3Record.SharedFields() : null;

        bool first = true;
        long previous = 0;

        foreach (BTreeLeafTablePage leaf in BTreeTools.WalkTableBTreeLeaves(RootPage))
        {
            for (int i = 0; i < leaf.CellCount; i++)
            {
                BTreePage.PayloadCell cell = leaf.GetCell(i, out long rowId);

                // INTEG-14: a scan yields rowids in strictly increasing order. SQLite checks this only in integrity_check,
                // checkTreePage in btree.c:
                //   if( keyCanBeEqual ? (info.nKey > maxKey) : (info.nKey >= maxKey) ){
                //     checkAppendMsg(pCheck, "Rowid %lld out of order", info.nKey);
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L10985 (checkTreePage)
                // A plain read in SQLite returns the rows as they lie. Here a repeated or decreasing rowid fails Strict,
                // which also stops a b-tree whose pages share a subtree at its first repeated row; a scan would otherwise
                // walk that subtree once per pointer to it, exponentially often within 20 levels. Lenient reports each
                // repetition and returns the rows as SQLite does, so it walks such a tree as long as SQLite would.
                Checks.Relaxable(first || rowId > previous, _reader.Settings, "INTEG-14",
                    $"rowid {rowId} follows rowid {previous}, so the table's rows are out of order", leaf.Page, "table b-tree");
                first = false;
                previous = rowId;

                ArraySegment<byte> payload = ReadPayload(leaf, rowId, cell);

                yield return new Sqlite3Row(this, rowId, NewRecord(leaf.Page, payload, shared));
            }
        }
    }

    private static Sqlite3Record NewRecord(uint page, ArraySegment<byte> payload, Sqlite3Record.SharedFields? shared)
    {
        return shared == null ? new Sqlite3Record(page, payload) : new Sqlite3Record(page, payload, shared);
    }

    private IEnumerable<Sqlite3Row> EnumerateWithoutRowIdRows(bool shareRowBuffers)
    {
        // Created inside the iterator, so each enumeration has its own field table
        Sqlite3Record.SharedFields? shared = shareRowBuffers ? new Sqlite3Record.SharedFields() : null;

        // A WITHOUT ROWID table's row is parsed when the schema is read, so its definition is there
        PrimaryKeyOrder? order = PrimaryKeyOrder.Create(SchemaDefinition.Table!, _reader.TextEncoding);

        foreach (KeyValuePair<BTreeIndexPage, int> entry in BTreeTools.WalkIndexBTreeCells((BTreeIndexPage)RootPage))
        {
            BTreeIndexPage page = entry.Key;
            BTreePage.PayloadCell cell = page.GetCell(entry.Value);
            ArraySegment<byte> payload = ReadPayload(page, null, cell);

            // INTEG-33: a scan yields the rows in strictly increasing PRIMARY KEY order, the WITHOUT ROWID form of
            // INTEG-14. SQLite checks this only in integrity_check, see PrimaryKeyOrder.IsInOrder:
            //   zErr = sqlite3MPrintf(db, "row not in PRIMARY KEY order for %s", pTab->zName);
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pragma.c#L1887 (sqlite3Pragma, integrity_check)
            // As for INTEG-14, a repeated or decreasing key fails Strict, which stops a b-tree whose pages share a
            // subtree at its first repeated row. Lenient reports each one and returns the rows as SQLite does. A table
            // whose key uses a collation that is not built into SQLite is not checked.
            if (order != null)
                Checks.Relaxable(order.IsInOrder(payload), _reader.Settings, "INTEG-33",
                    $"row not in PRIMARY KEY order for {SchemaDefinition.Name}: its key does not sort after the key of the row before it",
                    page.Page, "index b-tree");

            // There is no rowid
            yield return new Sqlite3Row(this, 0, NewRecord(page.Page, payload, shared));
        }
    }

    /// <summary>
    /// A record of no fields, see <see cref="ReadPayload"/>
    /// </summary>
    private static readonly byte[] EmptyRecord = new byte[1];

    /// <summary>
    /// A cell's whole payload, so the row can decode it from memory when its values are read. A payload that is all on
    /// its page is a slice of the page's array, which the row then keeps alive; one that overflows is copied into an
    /// array of its own. <paramref name="rowId"/> names the row in error messages, and is null in a WITHOUT ROWID table.
    /// </summary>
    private ArraySegment<byte> ReadPayload(BTreePage page, long? rowId, BTreePage.PayloadCell cell)
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

        // CELL-38: a payload can't be larger than the pages that could hold it, nor larger than the largest array. This is
        // the reader's own bound, not SQLite's: SQLite fails such a payload when its overflow chain ends early (CELL-13),
        // but this reader allocates the payload first, so the bound keeps a damaged size from allocating up to 2 GB.
        long maxPayload = Math.Min(int.MaxValue, cell.DataSizeInCell + _reader.PageCount * (usableSize - 4));
        Checks.Strict(cell.DataSize <= maxPayload, "CELL-38", $"payload size {cell.DataSize} of {RowName(rowId)} is larger than the database",
            page.Page, structure);

        int payloadSize = (int)cell.DataSize;

        // REC-04: SQLite reads the header size of a 0-byte payload from the byte after the cell (aOffset[0] =
        // pC->aRow[0] in OP_Column, vdbe.c), so a 0 there is a record of no fields and anything else is corrupt. That
        // byte is the one at the payload's offset. A damaged page can put that offset at the very end of the page,
        // where SQLite reads whatever memory follows the page; this reads a 0 there. The record reads that byte from
        // the array, right after its 0 bytes.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3048 (OP_Column)
        if (payloadSize == 0)
            return cell.PayloadOffset < _reader.PageSize
                ? new ArraySegment<byte>(page.Data, (int)cell.PayloadOffset, 0)
                : new ArraySegment<byte>(EmptyRecord, 0, 0);

        // All data is on this page, which the check above keeps inside it
        if (!overflows)
            return new ArraySegment<byte>(page.Data, (int)cell.PayloadOffset, payloadSize);

        // The first overflow page's number follows the local bytes. accessPayload reads it with get4byte, past the
        // usable size when the reserved space is under 4 bytes, so only the page end limits it.
        Checks.Strict(localEnd + 4 <= _reader.PageSize, "CELL-10",
            $"the overflow page number of {RowName(rowId)} at offset {localEnd} runs past the end of the page", page.Page, structure);
        int pointerPos = (int)localEnd;
        uint overflowPage = ByteArrayReader.ReadUInt32(page.Data, (int)_reader.PageSize, ref pointerPos);

        byte[] payload = new byte[payloadSize];
        Buffer.BlockCopy(page.Data, (int)cell.PayloadOffset, payload, 0, (int)cell.DataSizeInCell);
        int copied = (int)cell.DataSizeInCell;
        uint previousPage = page.Page;
        int overflowData = (int)usableSize - sizeof(uint);

        while (copied < payloadSize)
        {
            // CELL-13: the overflow chain ended before the payload did. accessPayload in btree.c:
            //   if( rc==SQLITE_OK && amt>0 ){
            //     /* Overflow chain ends prematurely */
            //     return SQLITE_CORRUPT_PAGE(pPage);
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5321 (accessPayload)
            Checks.Strict(overflowPage != 0, "CELL-13",
                $"the overflow chain of {RowName(rowId)} ended with {payloadSize - copied} bytes of the payload left", previousPage, "overflow chain");

            // Overflow pages begin with a 4 byte big-endian pointer to the next page of the chain (0 on the last), and
            // the payload continues right after it (https://www.sqlite.org/fileformat.html#ovflpgs). Each is read once,
            // into the reader's page buffer, as SQLite's accessPayload in btree.c reads each overflow page once through
            // the page cache. The payload is copied out before the next one is read.
            byte[] data = _reader.ReadPage(overflowPage);
            int count = Math.Min(overflowData, payloadSize - copied);
            Buffer.BlockCopy(data, sizeof(uint), payload, copied, count);
            copied += count;

            // CELL-16: the next pointer is followed only while payload remains, so the last overflow page's pointer is
            // never read, whatever it holds. accessPayload in btree.c stops as soon as the payload is copied:
            //   nextPage = get4byte(aPayload);
            //   ...
            //   amt -= a;
            //   if( amt==0 ) return rc;
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5306 (accessPayload)
            previousPage = overflowPage;
            overflowPage = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
        }

        return new ArraySegment<byte>(payload);
    }

    private static string RowName(long? rowId) => rowId.HasValue ? $"row {rowId.Value}" : "a WITHOUT ROWID row";
}
