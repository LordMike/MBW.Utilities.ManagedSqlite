namespace MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

/// <summary>
/// The two kinds of b-tree: table b-trees (keyed by rowid) and index b-trees (indexes and WITHOUT ROWID tables)
/// </summary>
internal enum BTreeKind : byte
{
    /// <summary>
    /// Never a valid kind, so a caller that passes an uninitialized value is caught
    /// </summary>
    Unknown = 0,
    Table = 1,
    Index = 2
}
