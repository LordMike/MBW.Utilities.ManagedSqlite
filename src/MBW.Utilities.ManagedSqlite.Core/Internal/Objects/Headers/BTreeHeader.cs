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

    public BTreeHeader(ReadOnlySpan<byte> span)
    {
        Type = (BTreeType)reader.ReadByte();
        FirstFreeBlock = reader.ReadUInt16();
        CellCount = reader.ReadUInt16();
        CellContentBegin = reader.ReadUInt16();
        CellContentFragmentedFreeBytes = reader.ReadByte();
        
        if (Type == BTreeType.InteriorIndexBtreePage || Type == BTreeType.InteriorTableBtreePage)
        {
            RightMostPointer = reader.ReadUInt32();
        }
    }

    public ushort[] GetCellOffsets()
    {
        // Read cells
        var cellOffsets = new ushort[CellCount];

        for (ushort i = 0; i < CellCount; i++)
            cellOffsets[i] = _reader.ReadUInt16();

        //TODO: needed?
        Array.Sort(cellOffsets);
    }
}