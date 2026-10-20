namespace MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

/// <summary>
/// The two kinds of b-tree: table b-trees (keyed by rowid) and index b-trees (indexes and WITHOUT ROWID tables)
/// </summary>
internal enum BTreeKind
{
    Table,
    Index
}
