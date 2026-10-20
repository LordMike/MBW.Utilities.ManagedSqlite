using System;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public class Sqlite3SchemaRow
{
    // Database, Type, Name and TableName are always assigned by Sqlite3Database and Sqlite3MasterTable, the only code
    // that creates rows; the null! initializers are placeholders until then.
    public Sqlite3Database Database { get; internal set; } = null!;
    public string Type { get; internal set; } = null!;

    public string Name { get; internal set; } = null!;

    public string TableName { get; internal set; } = null!;

    public uint RootPage { get; internal set; }

    public string? Sql { get; internal set; }

    /// <summary>
    /// True if this is a virtual table (<c>CREATE VIRTUAL TABLE ... USING module(...)</c>).
    /// Virtual tables have no b-tree in the database file (rootpage 0), so their contents cannot be read directly.
    /// Any shadow tables the module creates (e.g. <c>name_content</c> for FTS5) are ordinary tables.
    /// </summary>
    public bool IsVirtual => RootPage == 0 &&
                             Sql != null &&
                             Sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);
}
