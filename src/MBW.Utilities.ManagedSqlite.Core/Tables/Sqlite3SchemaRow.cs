using System;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public sealed class Sqlite3SchemaRow
{
    internal Sqlite3SchemaRow(Sqlite3Database database, string type, string name, string tableName, uint rootPage, string? sql)
    {
        Database = database;
        Type = type;
        Name = name;
        TableName = tableName;
        RootPage = rootPage;
        Sql = sql;
    }

    public Sqlite3Database Database { get; }
    public string Type { get; }

    public string Name { get; }

    public string TableName { get; }

    public uint RootPage { get; }

    public string? Sql { get; }

    /// <summary>
    /// True if this is a virtual table (<c>CREATE VIRTUAL TABLE ... USING module(...)</c>).
    /// Virtual tables have no b-tree in the database file (rootpage 0), so their contents cannot be read directly.
    /// Any shadow tables the module creates (e.g. <c>name_content</c> for FTS5) are ordinary tables.
    /// </summary>
    public bool IsVirtual => RootPage == 0 &&
                             Sql != null &&
                             Sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);
}
