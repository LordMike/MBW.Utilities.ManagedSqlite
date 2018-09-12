using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Sql
{
    internal static class SqlCache
    {
        private static readonly Dictionary<Sqlite3SchemaRow, SqlTableDefinition> DefinitionCache;

        static SqlCache()
        {
            DefinitionCache = new Dictionary<Sqlite3SchemaRow, SqlTableDefinition>();
        }

        public static SqlTableDefinition GetOrAdd(Sqlite3SchemaRow schema)
        {
            if (!DefinitionCache.TryGetValue(schema, out SqlTableDefinition definition))
            {
                if (!SqlParser.TryParse(schema.Sql, out definition))
                    DefinitionCache[schema] = null;
                else
                    DefinitionCache[schema] = definition;
            }

            if (definition == null)
                throw new Exception();

            return definition;
        }
    }
}