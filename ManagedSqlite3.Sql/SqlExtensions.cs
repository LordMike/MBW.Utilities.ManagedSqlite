using System;
using ManagedSqlite3.Core.Tables;

namespace ManagedSqlite3.Sql
{
    public static class SqlExtensions
    {
        public static SqlTableDefinition GetTableDefinition(this Sqlite3SchemaRow schema)
        {
            if (!SqlParser.TryParse(schema.Sql, out var def))
                throw new Exception();

            return def;
        }

        public static SqlTableDefinition GetTableDefinition(this Sqlite3Table table)
        {
            return table.SchemaDefinition.GetTableDefinition();
        }
    }
}