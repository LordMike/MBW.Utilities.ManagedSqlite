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
}
