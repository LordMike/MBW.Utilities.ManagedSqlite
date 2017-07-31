using System;
using System.Collections.Generic;
using Sqlite3RoLib.Objects;

namespace Sqlite3RoLib.Helpers
{
    internal static class BTreeTools
    {
        public static IEnumerable<BTreeCellData> WalkTableBTree(BTreePage node)
        {
            var asInterior = node as BTreeInteriorTablePage;

            if (asInterior != null)
                return WalkTableBTree(asInterior);

            var asLeaf = node as BTreeLeafTablePage;

            if (asLeaf != null)
                return WalkTableBTree(asLeaf);

            throw new ArgumentException("Did not receive a compatible BTreePage", nameof(node));
        }

        private static IEnumerable<BTreeCellData> WalkTableBTree(BTreeInteriorTablePage interior)
        {
            // Walk sub-pages and yield their data
            foreach (var cell in interior.Cells)
            {
                BTreePage subPage = BTreePage.Parse(interior.Reader, cell.LeftPagePointer);

                foreach (BTreeCellData data in WalkTableBTree(subPage))
                    yield return data;
            }
        }

        private static IEnumerable<BTreeCellData> WalkTableBTree(BTreeLeafTablePage leaf)
        {
            // Walk cells and yield their data
            foreach (var cell in leaf.Cells)
            {
                BTreeCellData res = new BTreeCellData();

                res.DataSize = cell.DataSize;
                res.RowId = cell.RowId;

                yield return res;
            }
        }
    }
}