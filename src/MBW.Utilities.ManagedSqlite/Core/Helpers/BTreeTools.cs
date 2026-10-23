using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Objects;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

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

        CheckInteriorRoot(node);

        // Depth-first, keeping the interior pages and the index of the next child to visit
        Stack<KeyValuePair<BTreeInteriorTablePage, int>> stack = new Stack<KeyValuePair<BTreeInteriorTablePage, int>>();
        stack.Push(new KeyValuePair<BTreeInteriorTablePage, int>((BTreeInteriorTablePage)node, 0));

        // The arrays of interior pages the walk is done with. No row refers to an interior page of a table b-tree, so
        // the next page read reuses one; a leaf's array is never reused, as its rows read from it. The root's array
        // belongs to the table, which reads it again on the next walk.
        byte[]? spare = null;

        // The child pointers followed so far, for PAGE-40 in ParseChild
        uint followed = 0;

        while (stack.Count > 0)
        {
            KeyValuePair<BTreeInteriorTablePage, int> top = stack.Pop();
            BTreeInteriorTablePage interior = top.Key;
            int child = top.Value;

            // The right-most child comes after the cells. Every interior page has one, so 0 is rejected as corrupt by ParseChild.
            uint childPage;
            string structure;
            if (child < interior.CellCount)
            {
                childPage = interior.GetChildPointer(child);
                structure = "interior cell child pointer";
            }
            else if (child == interior.CellCount)
            {
                childPage = interior.Header.RightMostPointer;
                structure = "right-most pointer";
            }
            else
            {
                if (interior != node)
                    spare = interior.Data;
                continue;
            }

            // Come back for the next child after this one. The stack now holds this page and its ancestors.
            stack.Push(new KeyValuePair<BTreeInteriorTablePage, int>(interior, child + 1));

            BTreePage subPage = ParseChild(interior, childPage, structure, stack.Count - 1, ++followed, BTreeKind.Table, spare);
            spare = null;
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
        if (!root.IsLeaf)
            CheckInteriorRoot(root);

        // Depth-first, keeping the interior pages and the index of the next child to visit
        Stack<KeyValuePair<BTreeIndexPage, int>> stack = new Stack<KeyValuePair<BTreeIndexPage, int>>();
        stack.Push(new KeyValuePair<BTreeIndexPage, int>(root, 0));

        // The child pointers followed so far, for PAGE-40 in ParseChild
        uint followed = 0;

        while (stack.Count > 0)
        {
            KeyValuePair<BTreeIndexPage, int> top = stack.Pop();
            BTreeIndexPage page = top.Key;
            int child = top.Value;

            if (page.IsLeaf)
            {
                for (int i = 0; i < page.CellCount; i++)
                    yield return new KeyValuePair<BTreeIndexPage, int>(page, i);

                continue;
            }

            // The left child of cell child - 1 is done, so that cell is next
            if (child > 0 && child <= page.CellCount)
                yield return new KeyValuePair<BTreeIndexPage, int>(page, child - 1);

            // The right-most child comes after the cells. Every interior page has one, so 0 is rejected as corrupt by ParseChild.
            uint childPage;
            string structure;
            if (child < page.CellCount)
            {
                childPage = page.GetChildPointer(child);
                structure = "interior cell child pointer";
            }
            else if (child == page.CellCount)
            {
                childPage = page.Header.RightMostPointer;
                structure = "right-most pointer";
            }
            else
                continue;

            // Come back for the next child after this one. The stack now holds this page and its ancestors.
            stack.Push(new KeyValuePair<BTreeIndexPage, int>(page, child + 1));

            // ParseChild rejects a table page here, so the child is an index page
            BTreeIndexPage subPage = (BTreeIndexPage)ParseChild(page, childPage, structure, stack.Count - 1, ++followed, BTreeKind.Index);
            stack.Push(new KeyValuePair<BTreeIndexPage, int>(subPage, 0));
        }
    }

    /// <summary>
    /// PAGE-19: an interior root page with no cells is corrupt, except page 1, which SQLite then descends through to its
    /// right-most child. moveToRoot in btree.c:
    ///   }else if( !pRoot->leaf ){
    ///     Pgno subpage;
    ///     if( pRoot->pgno!=1 ) return SQLITE_CORRUPT_BKPT;
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5620 (moveToRoot)
    /// Lenient reports it and walks the right-most child as for page 1, so the subtree's rows are still read.
    /// </summary>
    private static void CheckInteriorRoot(BTreePage root) =>
        Checks.Relaxable(root.CellCount > 0 || root.Page == 1, root.Reader.Settings, "PAGE-19",
            $"interior root page {root.Page} has no cells", root.Page, "b-tree page header");

    private static BTreePage ParseChild(BTreePage parent, uint childPage, string structure, int depth, uint followed, BTreeKind kind, byte[]? buffer = null)
    {
        uint pageCount = parent.Reader.PageCount;

        // PAGE-15: page 0 does not exist. getPageNormal in pager.c:
        //   if( pgno==0 ) return SQLITE_CORRUPT_BKPT;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5597 (getPageNormal)
        Checks.Strict(childPage != 0, "PAGE-15", $"child page {childPage} is outside the database (1..{pageCount})", parent.Page, structure);

        // PAGE-14: a child page must be in the database. getAndInitPage in btree.c:
        //   if( pgno>btreePagecount(pBt) ){
        //     *ppPage = 0;
        //     return SQLITE_CORRUPT_BKPT;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L2390 (getAndInitPage)
        Checks.Strict(pageCount == 0 || childPage <= pageCount, "PAGE-14",
            $"child page {childPage} is outside the database (1..{pageCount})", parent.Page, structure);

        // PAGE-24: at most 20 levels, which is also what stops a cycle (a child pointing back to itself or an ancestor).
        // moveToChild in btree.c:
        //   if( pCur->iPage>=(BTCURSOR_MAX_DEPTH-1) ){
        //     return SQLITE_CORRUPT_BKPT;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5460 (moveToChild)
        Checks.Strict(depth + 1 < MaxDepth, "PAGE-24", $"the b-tree is deeper than {MaxDepth} levels", parent.Page, structure);

        // PAGE-40: a walk follows fewer child pointers than the database has pages. This is the reader's own bound, not
        // SQLite's: in a b-tree each page but the root is the child of one pointer, so a walk that follows pageCount of
        // them has reached some page twice. SQLite only finds that in integrity_check (PAGE-26). On a read it walks a
        // shared subtree once for each pointer to it, which within 20 levels can be exponentially many times, so a scan
        // of such a file never ends. A walk within the bound reads the shared rows as often as SQLite does. A cycle that
        // follows many pointers before it repeats fails here before it reaches PAGE-24; SQLite reports it as corrupt too.
        // Strict in both modes: reading on is what never ends, and stopping early would drop rows.
        Checks.Strict(pageCount == 0 || followed < pageCount, "PAGE-40",
            $"the walk has followed {followed} child pointers, but the database has {pageCount} pages, so it reached a page twice", parent.Page, structure);

        BTreePage child = BTreePage.Parse(parent.Reader, childPage, kind, buffer);

        // PAGE-21: a page reached through a child pointer must hold at least one cell. moveToChild in btree.c:
        //   if( rc==SQLITE_OK
        //    && (pCur->pPage->nCell<1 || pCur->pPage->intKey!=pCur->curIntKey)
        //   ){
        //     releasePage(pCur->pPage);
        //     rc = SQLITE_CORRUPT_PGNO(newPgno);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5471 (moveToChild)
        // Lenient reports it and walks on as before: an empty leaf has no rows, and an empty interior page still has its
        // right-most child, so no row moves or appears.
        Checks.Relaxable(child.CellCount > 0, parent.Reader.Settings, "PAGE-21",
            $"child page {childPage} has no cells", childPage, "b-tree page header");

        return child;
    }
}
