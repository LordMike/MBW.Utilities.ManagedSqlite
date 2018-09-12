using System.Collections.Generic;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal
{
    internal class SqlCreateTableSchema
    {
        public TableName TableName { get; set; }
        public List<string>[][] Columns { get; set; }
    }
}