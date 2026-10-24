namespace MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

internal enum BTreeType : byte
{
    InteriorIndexBTreePage = 0x02,
    InteriorTableBTreePage = 0x05,
    LeafIndexBTreePage = 0x0a,
    LeafTableBTreePage = 0x0d
}