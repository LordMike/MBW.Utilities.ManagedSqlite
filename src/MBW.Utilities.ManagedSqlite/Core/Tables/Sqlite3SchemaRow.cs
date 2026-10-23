using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public sealed class Sqlite3SchemaRow
{
    internal Sqlite3SchemaRow(Sqlite3Database database, string? type, string name, string? tableName, uint rootPage, string? sql)
    {
        Database = database;
        Type = type;
        Name = name;
        TableName = tableName;
        RootPage = rootPage;
        Sql = sql;
    }

    public Sqlite3Database Database { get; }

    /// <summary>
    /// The row's type, as stored: "table", "index", "view" or "trigger". It can be NULL for an index that a PRIMARY KEY
    /// or UNIQUE constraint created, as SQLite does not read it there.
    /// </summary>
    public string? Type { get; }

    public string Name { get; }

    /// <summary>
    /// The table the row belongs to, as stored. It can be NULL for an index that a PRIMARY KEY or UNIQUE constraint
    /// created, as SQLite does not read it there.
    /// </summary>
    public string? TableName { get; }

    public uint RootPage { get; }

    public string? Sql { get; }

    /// <summary>
    /// What the row's statement made, as SQLite found when the schema was loaded. Unlike <see cref="Type"/>, this
    /// tells a virtual table from an ordinary one, and a CREATE ... IF NOT EXISTS that made nothing from one that did.
    /// </summary>
    public Sqlite3SchemaKind Kind { get; internal set; }

    /// <summary>
    /// True if this is a virtual table (<c>CREATE VIRTUAL TABLE ... USING module(...)</c>).
    /// Virtual tables have no b-tree in the database file, so their contents cannot be read directly; a rootpage other
    /// than 0, which SQLite tolerates, is not read either. Any shadow tables the module creates (e.g.
    /// <c>name_content</c> for FTS5) are ordinary tables.
    /// </summary>
    public bool IsVirtual => Kind == Sqlite3SchemaKind.VirtualTable;

    /// <summary>
    /// The row's statement made a table or a virtual table
    /// </summary>
    internal bool IsTable => Kind is Sqlite3SchemaKind.Table or Sqlite3SchemaKind.VirtualTable;

    /// <summary>
    /// The table the row's CREATE TABLE made; null for every other row
    /// </summary>
    internal ParsedTable? Table { get; set; }

    /// <summary>
    /// In <see cref="SqliteValidation.Lenient"/>, the error SQLite would have failed the schema with at this row; null
    /// when the row loaded. The other rows load on, and this row made nothing (<see cref="Sqlite3SchemaKind.None"/>),
    /// so its table cannot be opened.
    /// </summary>
    public SqliteCorruptException? Error { get; internal set; }
}
