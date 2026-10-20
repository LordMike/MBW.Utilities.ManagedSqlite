using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

internal readonly struct BTreeHeader
{
    public BTreeType Type { get; }

    public ushort FirstFreeBlock { get; }

    public ushort CellCount { get; }

    public ushort CellContentBegin { get; }

    public byte CellContentFragmentedFreeBytes { get; }

    public uint RightMostPointer { get; }

    /// <summary>
    /// Parses the b-tree page header at <paramref name="pos"/>, and advances it past the header
    /// </summary>
    public BTreeHeader(byte[] page, int length, ref int pos)
    {
        Type = (BTreeType)ByteArrayReader.ReadByte(page, length, ref pos);
        FirstFreeBlock = ByteArrayReader.ReadUInt16(page, length, ref pos);
        CellCount = ByteArrayReader.ReadUInt16(page, length, ref pos);
        CellContentBegin = ByteArrayReader.ReadUInt16(page, length, ref pos);
        CellContentFragmentedFreeBytes = ByteArrayReader.ReadByte(page, length, ref pos);

        if (Type == BTreeType.InteriorIndexBtreePage || Type == BTreeType.InteriorTableBtreePage)
            RightMostPointer = ByteArrayReader.ReadUInt32(page, length, ref pos);
        else
            RightMostPointer = 0;
    }
}
