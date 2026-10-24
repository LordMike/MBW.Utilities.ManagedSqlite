using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

/// <summary>
/// A row of the schema table, <c>sqlite_master</c>: a table, index, view or trigger, and the statement that created it.
/// A table's columns are read from its statement with <see cref="SqlExtensions.GetTableDefinition(Sqlite3SchemaRow)"/>.
/// </summary>
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

    /// <summary>
    /// The database the row was read from
    /// </summary>
    public Sqlite3Database Database { get; }

    /// <summary>
    /// The row's type, as stored: "table", "index", "view" or "trigger". It can be NULL for an index that a PRIMARY KEY
    /// or UNIQUE constraint created, as SQLite does not read it there.
    /// </summary>
    public string? Type { get; }

    /// <summary>
    /// The name of the table, index, view or trigger, as stored
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The table the row belongs to, as stored. It can be NULL for an index that a PRIMARY KEY or UNIQUE constraint
    /// created, as SQLite does not read it there.
    /// </summary>
    public string? TableName { get; }

    /// <summary>
    /// The page number of the root page of the row's b-tree, as stored. It is 0 for a view, a trigger and, usually, a
    /// virtual table, which have no b-tree.
    /// </summary>
    public uint RootPage { get; }

    /// <summary>
    /// The CREATE statement, as stored. It is NULL for an index that a PRIMARY KEY or UNIQUE constraint created.
    /// </summary>
    public string? Sql { get; }

    /// <summary>
    /// True if this is a virtual table (<c>CREATE VIRTUAL TABLE ... USING module(...)</c>).
    /// Virtual tables have no b-tree in the database file, so their contents cannot be read directly; a rootpage other
    /// than 0, which SQLite tolerates, is not read either. Any shadow tables the module creates (e.g.
    /// <c>name_content</c> for FTS5) are ordinary tables.
    /// </summary>
    public bool IsVirtual { get; internal set; }

    /// <summary>
    /// The row's statement made a table or a virtual table, as the parser found when the schema was loaded
    /// </summary>
    internal bool IsTable { get; set; }

    /// <summary>
    /// The table the row's CREATE TABLE made; null for every other row
    /// </summary>
    internal ParsedTable? Table { get; set; }

    /// <summary>
    /// In <see cref="SqliteValidation.Lenient"/>, the error SQLite would have failed the schema with at this row. The
    /// other rows load on, and this row's table cannot be opened.
    /// </summary>
    internal SqliteCorruptException? Error { get; set; }
}
