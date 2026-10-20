using System.Collections.Generic;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

internal class Sqlite3MasterTable
{
    public List<Sqlite3SchemaRow> Tables { get; }

    public Sqlite3MasterTable(Sqlite3Database db, Sqlite3Table table)
    {
        Tables = new List<Sqlite3SchemaRow>();

        IEnumerable<Sqlite3Row> rows = table.EnumerateRows();

        foreach (Sqlite3Row row in rows)
        {
            row.TryGetOrdinal(0, out string? type);
            row.TryGetOrdinal(1, out string? name);
            row.TryGetOrdinal(2, out string? tableName);
            row.TryGetOrdinal(3, out long rootPage);
            row.TryGetOrdinal(4, out string? sql);

            Tables.Add(new Sqlite3SchemaRow(db, type, name, tableName, (uint)rootPage, sql));
        }
    }
}