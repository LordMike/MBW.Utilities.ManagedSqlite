using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Sql;

public static class SqlExtensions
{
    private const string SqlCacheKey = "SqlCache";

    public static SqlTableDefinition GetTableDefinition(this Sqlite3SchemaRow schema)
    {
        // A null value caches that the SQL could not be parsed
        Dictionary<Sqlite3SchemaRow, SqlTableDefinition?> definitionCache = schema.Database.GetProperty(SqlCacheKey, () => new Dictionary<Sqlite3SchemaRow, SqlTableDefinition?>());

        if (!definitionCache.TryGetValue(schema, out SqlTableDefinition? definition))
        {
            if (!SqlParser.TryParse(schema.Sql, out definition))
                definitionCache[schema] = null;
            else
                definitionCache[schema] = definition;
        }

        if (definition == null)
            throw new InvalidOperationException($"Unable to parse a table definition from the SQL of {schema.Type} '{schema.Name}'");

        SqlTableDefinition def = definition;

        return def;
    }

    /// <exception cref="SqliteCorruptException">The SQL declares the table WITHOUT ROWID and its root page is a table
    /// b-tree page, or the other way around</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3Table table)
    {
        SqlTableDefinition definition = table.SchemaDefinition.GetTableDefinition();

        // SQLite reports a root page of the other b-tree kind as corrupt, so a record is never decoded with the other
        // kind's column order
        if (definition.IsWithoutRowId != table.IsWithoutRowId)
            throw new SqliteCorruptException($"the SQL of table {table.SchemaDefinition.Name} declares {(definition.IsWithoutRowId ? "WITHOUT ROWID" : "a rowid table")}, but its root page is {(table.IsWithoutRowId ? "an index" : "a table")} b-tree page",
                table.SchemaDefinition.RootPage, "b-tree page header");

        return definition;
    }

    public static bool TryGetValueByName(this Sqlite3Row row, string columnName, out object? value)
    {
        value = null;

        SqlTableDefinition tableDefinition = row.Table.GetTableDefinition();

        if (!tableDefinition.TryGetColumn(columnName, out SqlTableColumn? column))
            return false;

        // SQLite computes a VIRTUAL generated column when it is read, nothing is stored for it
        if (column.IsVirtualGenerated)
            return false;

        if (row.ColumnData.Length <= column.RecordIndex)
            return false;

        // Special case for row-id
        if (column == tableDefinition.RowIdColumn)
        {
            // Return the row-id
            value = row.RowId;
            return true;
        }

        value = row.ColumnData[column.RecordIndex];

        // SQLite writes REAL values without a fractional part as integers, and turns them back into REAL when read
        // through a column with REAL affinity
        if (value is long integer && column.DetectedType == typeof(double))
            value = (double)integer;

        return true;
    }

    public static bool TryGetValueByName<T>(this Sqlite3Row row, string columnName, [MaybeNullWhen(false)] out T value)
    {
        value = default;

        if (!row.TryGetValueByName(columnName, out object? tmp))
            return false;

        // TODO: Is null a success case?
        if (tmp == null)
            return false;

        value = (T)Convert.ChangeType(tmp, typeof(T));
        return true;
    }
}