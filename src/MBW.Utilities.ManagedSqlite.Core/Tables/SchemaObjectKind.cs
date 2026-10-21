namespace MBW.Utilities.ManagedSqlite.Core.Tables;

/// <summary>
/// The kind of object a <c>sqlite_schema</c> row's CREATE statement creates, by its leading keywords
/// </summary>
internal enum SchemaObjectKind : byte
{
    /// <summary>
    /// No CREATE statement: the SQL is NULL or empty, as for an index a PRIMARY KEY or UNIQUE constraint created
    /// </summary>
    None,

    /// <summary>
    /// SQL that starts with "cr" but is not one of the CREATE statements below, which SQLite fails to parse
    /// </summary>
    Unknown,
    Table,

    /// <summary>
    /// CREATE TABLE ... AS SELECT, which SQLite never stores, and finds corrupt in the schema
    /// </summary>
    TableAsSelect,
    VirtualTable,
    View,
    Index,
    Trigger
}
