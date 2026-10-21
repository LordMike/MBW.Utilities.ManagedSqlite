namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public sealed class Sqlite3SchemaRow
{
    internal Sqlite3SchemaRow(Sqlite3Database database, string? type, string name, string? tableName, uint rootPage, string? sql, SchemaObjectKind kind)
    {
        Database = database;
        Type = type;
        Name = name;
        TableName = tableName;
        RootPage = rootPage;
        Sql = sql;
        Kind = kind;
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
    /// True if this is a virtual table (<c>CREATE VIRTUAL TABLE ... USING module(...)</c>).
    /// Virtual tables have no b-tree in the database file, so their contents cannot be read directly; a rootpage other
    /// than 0, which SQLite tolerates, is not read either. Any shadow tables the module creates (e.g.
    /// <c>name_content</c> for FTS5) are ordinary tables.
    /// </summary>
    public bool IsVirtual => Kind == SchemaObjectKind.VirtualTable;

    /// <summary>
    /// The kind of object the row's CREATE statement creates
    /// </summary>
    internal SchemaObjectKind Kind { get; }
}
