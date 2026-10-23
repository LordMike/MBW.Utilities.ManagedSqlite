using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Objects;

/// <summary>
/// SQLite index b-tree page, interior or leaf. A WITHOUT ROWID table is stored as an index b-tree keyed by its primary
/// key, and every cell's payload is a whole row, in interior cells too. There is no rowid.
/// </summary>
internal class BTreeIndexPage : BTreePage
{
    public bool IsLeaf => Header.Type == BTreeType.LeafIndexBtreePage;

    private readonly uint[]? _childPointers;

    public BTreeIndexPage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets, byte[] data)
        : base(reader, page, header, cellOffsets)
    {
        if (!IsLeaf)
            _childPointers = ReadChildPointers(data);
    }

    /// <summary>
    /// The left child pointer of cell <paramref name="index"/> of an interior page: the child with the keys that sort
    /// before this cell's
    /// </summary>
    public uint GetChildPointer(int index) => GetChildPointer(_childPointers!, index);

    /// <summary>
    /// Parses cell <paramref name="index"/> when it is reached, as SQLite does (btreeParseCellPtrIndex in btree.c)
    /// </summary>
    public PayloadCell GetCell(int index)
    {
        byte[] data = GetData();

        // Interior cells begin with the left child pointer, leaf cells with the payload size
        int start = GetCellOffset(index);
        int pos = start + (IsLeaf ? 0 : sizeof(uint));
        uint bytes = ReadPayloadSize(data, ref pos, start);

        return new PayloadCell
        {
            PayloadOffset = (uint)pos,
            DataSize = bytes,
            DataSizeInCell = PayloadSizeOnPage(bytes, false)
        };
    }
}
