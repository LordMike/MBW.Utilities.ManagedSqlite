using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

internal struct BTreeHeader
{
    public BTreeType Type;

    public ushort FirstFreeBlock;

    public ushort CellCount;

    public ushort CellContentBegin;

    public byte CellContentFragmentedFreeBytes;

    public uint RightMostPointer;

    public static BTreeHeader Parse(byte[] page, int length, ref int pos)
    {
        BTreeHeader res = new BTreeHeader();

        res.Type = (BTreeType)ByteArrayReader.ReadByte(page, length, ref pos);
        res.FirstFreeBlock = ByteArrayReader.ReadUInt16(page, length, ref pos);
        res.CellCount = ByteArrayReader.ReadUInt16(page, length, ref pos);
        res.CellContentBegin = ByteArrayReader.ReadUInt16(page, length, ref pos);
        res.CellContentFragmentedFreeBytes = ByteArrayReader.ReadByte(page, length, ref pos);

        if (res.Type == BTreeType.InteriorIndexBtreePage || res.Type == BTreeType.InteriorTableBtreePage)
            res.RightMostPointer = ByteArrayReader.ReadUInt32(page, length, ref pos);

        return res;
    }
}
