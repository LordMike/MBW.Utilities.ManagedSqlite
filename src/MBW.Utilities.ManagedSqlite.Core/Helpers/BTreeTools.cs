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

    /// <summary>
    /// Yields the leaf pages of a table b-tree, left to right. Callers iterate each leaf's cells themselves, which
    /// avoids passing every cell up through one iterator per tree level.
    /// </summary>
    public static IEnumerable<BTreeLeafTablePage> WalkTableBTreeLeaves(BTreePage node)
    {
        if (node is BTreeLeafTablePage asLeaf)
        {
            yield return asLeaf;
            yield break;
        }

        if (node is not BTreeInteriorTablePage)
            throw new ArgumentException("Did not receive a compatible BTreePage", nameof(node));

        // Depth-first, keeping the interior pages and the index of the next child to visit
        Stack<KeyValuePair<BTreeInteriorTablePage, int>> stack = new Stack<KeyValuePair<BTreeInteriorTablePage, int>>();
        stack.Push(new KeyValuePair<BTreeInteriorTablePage, int>((BTreeInteriorTablePage)node, 0));

        while (stack.Count > 0)
        {
            KeyValuePair<BTreeInteriorTablePage, int> top = stack.Pop();
            BTreeInteriorTablePage interior = top.Key;
            int child = top.Value;

            // The right-most child comes after the cells. Every interior page has one, so 0 is rejected as corrupt by ParseChild.
            uint childPage;
            string structure;
            if (child < interior.Cells.Length)
            {
                childPage = interior.Cells[child].LeftPagePointer;
                structure = "interior cell child pointer";
            }
            else if (child == interior.Cells.Length)
            {
                childPage = interior.Header.RightMostPointer;
                structure = "right-most pointer";
            }
            else
                continue;

            // Come back for the next child after this one. The stack now holds this page and its ancestors.
            stack.Push(new KeyValuePair<BTreeInteriorTablePage, int>(interior, child + 1));

            BTreePage subPage = ParseChild(interior, childPage, structure, stack.Count - 1);
            if (subPage is BTreeLeafTablePage leaf)
                yield return leaf;
            else if (subPage is BTreeInteriorTablePage subInterior)
                stack.Push(new KeyValuePair<BTreeInteriorTablePage, int>(subInterior, 0));
            else
                throw new ArgumentException("Did not receive a compatible BTreePage", nameof(node));
        }
    }

    private static BTreePage ParseChild(BTreeInteriorTablePage parent, uint childPage, string structure, int depth)
    {
        uint pageCount = parent.Reader.PageCount;

        if (childPage == 0 || (pageCount > 0 && childPage > pageCount))
            throw new SqliteCorruptException($"child page {childPage} is outside the database (1..{pageCount})", parent.Page, structure);

        // Like SQLite, this is also what stops a cycle (a child pointing back to itself or an ancestor)
        if (depth + 1 >= MaxDepth)
            throw new SqliteCorruptException($"the b-tree is deeper than {MaxDepth} levels", parent.Page, structure);

        return BTreePage.Parse(parent.Reader, childPage, true);
    }
}
