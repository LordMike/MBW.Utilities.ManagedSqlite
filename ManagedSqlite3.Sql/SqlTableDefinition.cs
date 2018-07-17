using System.Collections.Generic;

namespace ManagedSqlite3.Sql
{
    public class SqlTableDefinition
    {
        public string TableName { get; }
        public List<SqlTableColumn> Columns { get; }

        public SqlTableDefinition(string name)
        {
            TableName = name;
            Columns = new List<SqlTableColumn>();
        }
    }
}