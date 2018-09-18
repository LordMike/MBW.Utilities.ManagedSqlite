using System.Collections.Generic;

namespace MBW.Utilities.ManagedSqlite.Core.Tables
{
    internal class Sqlite3MasterTable
    {
        public List<Sqlite3SchemaRow> Tables { get; }

        public Sqlite3MasterTable(Sqlite3Table table)
        {
            Tables = new List<Sqlite3SchemaRow>();

            IEnumerable<Sqlite3Row> rows = table.EnumerateRows();

            foreach (Sqlite3Row row in rows)
            {
                Sqlite3SchemaRow other = new Sqlite3SchemaRow();

                row.TryGetOrdinal(0, out string str);
                other.Type = str;

                row.TryGetOrdinal(1, out str);
                other.Name = str;

                row.TryGetOrdinal(2, out str);
                other.TableName = str;

                row.TryGetOrdinal(3, out long lng);
                other.RootPage = (uint)lng;

                row.TryGetOrdinal(4, out str);
                other.Sql = str;

                Tables.Add(other);
            }
        }
    }
}