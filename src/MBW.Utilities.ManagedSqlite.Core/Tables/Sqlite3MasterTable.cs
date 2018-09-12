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

                other.Type = (string)row.ColumnData[0];
                other.Name = (string)row.ColumnData[1];
                other.TableName = (string)row.ColumnData[2];
                other.RootPage = (uint)(long)row.ColumnData[3];
                other.Sql = (string)row.ColumnData[4];

                Tables.Add(other);
            }
        }
    }
}