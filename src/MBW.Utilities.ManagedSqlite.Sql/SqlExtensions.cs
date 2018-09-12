using System;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Sql
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