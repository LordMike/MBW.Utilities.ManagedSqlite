using System;
using System.Collections.Generic;

namespace ManagedSqlite3.Sql
{
    public class SqlTableDefinition
    {
        public string TableName { get; }
        public List<(string, Type)> Columns { get; }

        public SqlTableDefinition(string name)
        {
            TableName = name;
            Columns = new List<(string, Type)>();
        }
    }
}