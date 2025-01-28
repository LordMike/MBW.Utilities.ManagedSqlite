namespace MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;

internal enum BTreeType : byte
{
    InteriorIndexBtreePage = 0x02,
    InteriorTableBtreePage = 0x05,
    LeafIndexBtreePage = 0x0a,
    LeafTableBtreePage = 0x0d
}