using System;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public class Sqlite3SchemaRow
{
    public Sqlite3Database Database { get; internal set; }
    public string Type { get; internal set; }

    public string Name { get; internal set; }

    public string TableName { get; internal set; }

    public uint RootPage { get; internal set; }

    public string Sql { get; internal set; }

    /// <summary>
    /// True if this is a virtual table (<c>CREATE VIRTUAL TABLE ... USING module(...)</c>).
    /// Virtual tables have no b-tree in the database file (rootpage 0), so their contents cannot be read directly.
    /// Any shadow tables the module creates (e.g. <c>name_content</c> for FTS5) are ordinary tables.
    /// </summary>
    public bool IsVirtual => RootPage == 0 &&
                             Sql != null &&
                             Sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);
}
