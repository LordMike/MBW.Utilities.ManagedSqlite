using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Sql;

public static class SqlExtensions
{
    private const string SqlCacheKey = "SqlCache";

    public static SqlTableDefinition GetTableDefinition(this Sqlite3SchemaRow schema)
    {
        Dictionary<Sqlite3SchemaRow, SqlTableDefinition> definitionCache = schema.Database.GetProperty(SqlCacheKey, () => new Dictionary<Sqlite3SchemaRow, SqlTableDefinition>());

        if (!definitionCache.TryGetValue(schema, out SqlTableDefinition definition))
        {
            if (!SqlParser.TryParse(schema.Sql, out definition))
                definitionCache[schema] = null;
            else
                definitionCache[schema] = definition;
        }

        if (definition == null)
            throw new Exception();

        SqlTableDefinition def = definition;

        return def;
    }

    public static SqlTableDefinition GetTableDefinition(this Sqlite3Table table)
    {
        return table.SchemaDefinition.GetTableDefinition();
    }

    public static bool TryGetValueByName(this Sqlite3Row row, string columnName, out object value)
    {
        value = null;

        SqlTableDefinition tableDefinition = row.Table.GetTableDefinition();

        if (!tableDefinition.TryGetColumn(columnName, out SqlTableColumn column))
            return false;

        if (row.ColumnData.Length <= column.Ordinal)
            return false;

        // Special case for row-id
        if (tableDefinition.RowIdColumn != null && columnName.Equals(tableDefinition.RowIdColumn.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Return the row-id
            value = row.RowId;
            return true;
        }

        value = row.ColumnData[column.Ordinal];
        return true;
    }

    public static bool TryGetValueByName<T>(this Sqlite3Row row, string columnName, out T value)
    {
        value = default;

        if (!row.TryGetValueByName(columnName, out object tmp))
            return false;

        // TODO: Is null a success case?
        if (tmp == null)
            return false;

        value = (T)Convert.ChangeType(tmp, typeof(T));
        return true;
    }
}