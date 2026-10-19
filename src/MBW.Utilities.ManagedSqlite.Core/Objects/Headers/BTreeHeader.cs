using MBW.Utilities.ManagedSqlite.Core.Internal;
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

    public static BTreeHeader Parse(ReaderBase reader)
    {
        reader.CheckSize(8);

        BTreeHeader res = new BTreeHeader();

        res.Type = (BTreeType)reader.ReadByte();
        res.FirstFreeBlock = reader.ReadUInt16();
        res.CellCount = reader.ReadUInt16();
        res.CellContentBegin = reader.ReadUInt16();
        res.CellContentFragmentedFreeBytes = reader.ReadByte();

        if (res.Type == BTreeType.InteriorIndexBtreePage || res.Type == BTreeType.InteriorTableBtreePage)
        {
            reader.CheckSize(sizeof(uint));

            res.RightMostPointer = reader.ReadUInt32();
        }

        // The cell pointer array follows, make sure it can be read. BTreePage checks it fits in the page.
        reader.CheckSize(2u * res.CellCount);

        return res;
    }
}