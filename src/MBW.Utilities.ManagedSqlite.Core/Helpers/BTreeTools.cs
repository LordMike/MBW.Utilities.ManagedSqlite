using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Objects;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

internal static class BTreeTools
{
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

            uint childPage;
            if (child < interior.Cells.Length)
                childPage = interior.Cells[child].LeftPagePointer;
            else if (child == interior.Cells.Length && interior.Header.RightMostPointer > 0)
                childPage = interior.Header.RightMostPointer;
            else
                continue;

            // Come back for the next child after this one
            stack.Push(new KeyValuePair<BTreeInteriorTablePage, int>(interior, child + 1));

            BTreePage subPage = BTreePage.Parse(interior.Reader, childPage);
            if (subPage is BTreeLeafTablePage leaf)
                yield return leaf;
            else if (subPage is BTreeInteriorTablePage subInterior)
                stack.Push(new KeyValuePair<BTreeInteriorTablePage, int>(subInterior, 0));
            else
                throw new ArgumentException("Did not receive a compatible BTreePage", nameof(node));
        }
    }

    /// <summary>
    /// Yields the cells of an index b-tree in key order, as the page and the index of the cell on it. Interior cells
    /// carry entries too: each one sorts after its left child's entries and before the next child's, and the right-most
    /// child comes last.
    /// </summary>
    public static IEnumerable<KeyValuePair<BTreeIndexPage, int>> WalkIndexBTreeCells(BTreeIndexPage root)
    {
        // Depth-first, keeping the interior pages and the index of the next child to visit
        Stack<KeyValuePair<BTreeIndexPage, int>> stack = new Stack<KeyValuePair<BTreeIndexPage, int>>();
        stack.Push(new KeyValuePair<BTreeIndexPage, int>(root, 0));

        while (stack.Count > 0)
        {
            KeyValuePair<BTreeIndexPage, int> top = stack.Pop();
            BTreeIndexPage page = top.Key;
            int child = top.Value;

            if (page.IsLeaf)
            {
                for (int i = 0; i < page.Cells.Length; i++)
                    yield return new KeyValuePair<BTreeIndexPage, int>(page, i);

                continue;
            }

            // The left child of cell child - 1 is done, so that cell is next
            if (child > 0 && child <= page.Cells.Length)
                yield return new KeyValuePair<BTreeIndexPage, int>(page, child - 1);

            uint childPage;
            if (child < page.Cells.Length)
                childPage = page.Cells[child].LeftPagePointer;
            else if (child == page.Cells.Length && page.Header.RightMostPointer > 0)
                childPage = page.Header.RightMostPointer;
            else
                continue;

            // Come back for the next child after this one
            stack.Push(new KeyValuePair<BTreeIndexPage, int>(page, child + 1));

            if (BTreePage.Parse(page.Reader, childPage) is not BTreeIndexPage subPage)
                throw new ArgumentException("Did not receive a compatible BTreePage", nameof(root));

            stack.Push(new KeyValuePair<BTreeIndexPage, int>(subPage, 0));
        }
    }
}
