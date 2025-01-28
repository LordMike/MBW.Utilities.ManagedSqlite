using System;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Headers;

internal ref struct BTreeHeader
{
    public BTreeType Type;
    public ushort FirstFreeBlock;
    public ushort CellCount;
    public ushort CellContentBegin;
    public byte CellContentFragmentedFreeBytes;
    public uint RightMostPointer;

    public BTreeHeader(PagedStream stream)
    {
        Type = (BTreeType)stream.ReadByte();
        FirstFreeBlock = stream.ReadUInt16();
        CellCount = stream.ReadUInt16();
        CellContentBegin = stream.ReadUInt16();
        CellContentFragmentedFreeBytes = (byte)stream.ReadByte();

        if (Type is BTreeType.InteriorIndexBtreePage or BTreeType.InteriorTableBtreePage)
            RightMostPointer = stream.ReadUInt32();
    }

    public ushort[] GetCellOffsets(PagedStream stream)
    {
        // Read cells
        var cellOffsets = new ushort[CellCount];

        for (ushort i = 0; i < CellCount; i++)
            cellOffsets[i] = stream.ReadUInt16();

        //TODO: needed?
        Array.Sort(cellOffsets);

        return cellOffsets;
    }
}