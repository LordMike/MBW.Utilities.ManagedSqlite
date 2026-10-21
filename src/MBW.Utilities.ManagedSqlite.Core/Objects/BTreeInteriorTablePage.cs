using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Objects;

/// <summary>
/// SQLite B-Tree datastructure that contains other Interior / Leaf pages
/// </summary>
internal class BTreeInteriorTablePage : BTreePage
{
    public BTreeInteriorTablePage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets)
        : base(reader, page, header, cellOffsets)
    {
    }

    /// <summary>
    /// The left child pointer of cell <paramref name="index"/>. PAGE-13, CELL-04: the cell's integer key is never read,
    /// as a scan in SQLite never reads it (moveToLeftmost only does get4byte(findCell(pPage, pCur->ix))).
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5649 (moveToLeftmost)
    /// </summary>
    public uint GetChildPointer(int index) => ReadCellUInt32(GetData(), GetCellOffset(index));
}
