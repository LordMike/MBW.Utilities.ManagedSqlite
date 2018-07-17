using System.Collections.Generic;

namespace ManagedSqlite3.Sql.Internal
{
    internal class SqlCreateTableSchema
    {
        public TableName TableName { get; set; }
        public List<string>[][] Columns { get; set; }
    }
}