namespace MBW.Utilities.ManagedSqlite.Core.Tables;

/// <summary>
/// What a schema row's statement made, as SQLite found when it loaded the schema. It is not read from the row's type
/// column, which <see cref="SqliteValidation.Strict"/> makes sure agrees (SCHEMA-26).
/// </summary>
public enum Sqlite3SchemaKind : byte
{
    /// <summary>
    /// The row made nothing: a CREATE ... IF NOT EXISTS of a name a row before it took, or, in
    /// <see cref="SqliteValidation.Lenient"/>, a row that failed to load (see <see cref="Sqlite3SchemaRow.Error"/>)
    /// </summary>
    None = 0,

    /// <summary>
    /// An ordinary table (<c>CREATE TABLE</c>), including the shadow tables of a virtual table
    /// </summary>
    Table = 1,

    /// <summary>
    /// A virtual table (<c>CREATE VIRTUAL TABLE ... USING module(...)</c>). It has no b-tree in the database file, so
    /// its rows cannot be read.
    /// </summary>
    VirtualTable = 2,

    /// <summary>
    /// An index, made by <c>CREATE INDEX</c> or, for a row without SQL, by a PRIMARY KEY or UNIQUE constraint
    /// </summary>
    Index = 3,

    /// <summary>
    /// A view (<c>CREATE VIEW</c>), which has no rows in the database file
    /// </summary>
    View = 4,

    /// <summary>
    /// A trigger (<c>CREATE TRIGGER</c>)
    /// </summary>
    Trigger = 5
}
