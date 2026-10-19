using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Objects;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

internal static class BTreeTools
{
    /// <summary>
    /// The maximum number of b-tree levels, the same limit SQLite puts on its cursors (BTCURSOR_MAX_DEPTH).
    /// A real tree this deep would need far more rows than a database can hold, so deeper means corrupt.
    /// </summary>
    internal const int MaxDepth = 20;

    public static IEnumerable<BTreeCellData> WalkTableBTree(BTreePage node)
    {
        // Every page may appear only once in a b-tree. Tracking them all stops cycles (a child pointing back to
        // itself or an ancestor) and pages shared between parents, both of which would otherwise never end.
        HashSet<uint> visited = new HashSet<uint> { node.Page };

        foreach (BTreeCellData data in WalkTableBTree(node, visited, 0))
            yield return data;
    }

    private static IEnumerable<BTreeCellData> WalkTableBTree(BTreePage node, HashSet<uint> visited, int depth)
    {
        if (node is BTreeInteriorTablePage asInterior)
            return WalkTableBTree(asInterior, visited, depth);

        if (node is BTreeLeafTablePage asLeaf)
            return WalkTableBTree(asLeaf);

        throw new ArgumentException("Did not receive a compatible BTreePage", nameof(node));
    }

    private static IEnumerable<BTreeCellData> WalkTableBTree(BTreeInteriorTablePage interior, HashSet<uint> visited, int depth)
    {
        // Walk sub-pages and yield their data
        foreach (BTreeInteriorTablePage.Cell cell in interior.Cells)
        {
            BTreePage subPage = ParseChild(interior, cell.LeftPagePointer, "interior cell child pointer", visited, depth);

            foreach (BTreeCellData data in WalkTableBTree(subPage, visited, depth + 1))
                yield return data;
        }

        // Process the right-most child. Every interior page has one, so 0 is rejected as corrupt by ParseChild.
        BTreePage rightPage = ParseChild(interior, interior.Header.RightMostPointer, "right-most pointer", visited, depth);

        foreach (BTreeCellData data in WalkTableBTree(rightPage, visited, depth + 1))
            yield return data;
    }

    private static BTreePage ParseChild(BTreeInteriorTablePage parent, uint childPage, string structure, HashSet<uint> visited, int depth)
    {
        uint pageCount = parent.Reader.PageCount;

        // Page 1 is always the root of sqlite_master, so it is never a child
        if (childPage <= 1 || (pageCount > 0 && childPage > pageCount))
            throw new SqliteCorruptException($"child page {childPage} is outside the database (2..{pageCount})", parent.Page, structure);

        if (depth + 1 >= MaxDepth)
            throw new SqliteCorruptException($"the b-tree is deeper than {MaxDepth} levels", parent.Page, structure);

        if (!visited.Add(childPage))
            throw new SqliteCorruptException($"child page {childPage} was already visited in this b-tree (cycle or shared page)", parent.Page, structure);

        return BTreePage.Parse(parent.Reader, childPage, true);
    }

    private static IEnumerable<BTreeCellData> WalkTableBTree(BTreeLeafTablePage leaf)
    {
        // Walk cells and yield their data
        for (var i = 0; i < leaf.Cells.Length; i++)
        {
            BTreeLeafTablePage.Cell cell = leaf.Cells[i];

            BTreeCellData res = new BTreeCellData();

            res.Cell = cell;
            res.CellOffset = leaf.CellOffsets[i];
            res.Page = leaf.Page;

            yield return res;
        }
    }
}
