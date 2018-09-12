using System.Collections.Generic;

namespace MBW.Utilities.ManagedSqlite.Sql
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