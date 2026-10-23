using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Sql;

public static class SqlExtensions
{
    private const string SqlCacheKey = "SqlCache";

    /// <exception cref="InvalidOperationException">The row is not a table (a view, index or trigger)</exception>
    /// <exception cref="NotSupportedException">The row is a virtual table</exception>
    /// <exception cref="SqliteCorruptException">In Lenient, the row failed to load</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3SchemaRow schema)
    {
        // Only a table has a table definition, so asking for one elsewhere is the caller's error, not damage. The row's
        // statement decides, as in SQLite; Strict has made sure the type agrees (SCHEMA-26).
        if (schema.Error != null)
            throw schema.Error;

        // A view, index or trigger makes no table, and neither does a CREATE TABLE IF NOT EXISTS of a name a row before
        // it took, in SQLite too
        if (!schema.IsTable)
            throw new InvalidOperationException($"{schema.Type ?? "row"} '{schema.Name}' makes no table, so it has no table definition");

        if (schema.IsVirtual)
            throw new NotSupportedException($"'{schema.Name}' is a virtual table, whose columns its module declares");

        // The table was parsed when the schema was loaded
        Dictionary<Sqlite3SchemaRow, SqlTableDefinition> definitionCache = schema.Database.GetProperty(SqlCacheKey, () => new Dictionary<Sqlite3SchemaRow, SqlTableDefinition>());
        if (!definitionCache.TryGetValue(schema, out SqlTableDefinition? definition))
            definitionCache[schema] = definition = SqlParser.Build(schema.Table!);

        return definition;
    }

    /// <exception cref="SqliteCorruptException">The SQL declares the table WITHOUT ROWID and its root page is a table
    /// b-tree page, or the other way around</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3Table table)
    {
        // A definition that was read without error reads the same again: the schema and the checks below don't change
        if (table.SqlDefinition != null)
            return table.SqlDefinition;

        SqlTableDefinition definition = table.SchemaDefinition.GetTableDefinition();

        // PAGE-18: a table cursor needs a table b-tree root, and a WITHOUT ROWID (index) cursor an index b-tree root,
        // so a record is never decoded with the other kind's column order. moveToRoot in btree.c:
        //   if( pRoot->isInit==0 || (pCur->pKeyInfo==0)!=pRoot->intKey ){
        //     return SQLITE_CORRUPT_PAGE(pCur->pPage);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5609 (moveToRoot)
        Checks.Strict(definition.IsWithoutRowId == table.IsWithoutRowId, "PAGE-18",
            $"the SQL of table {table.SchemaDefinition.Name} declares {(definition.IsWithoutRowId ? "WITHOUT ROWID" : "a rowid table")}, but its root page is {(table.IsWithoutRowId ? "an index" : "a table")} b-tree page",
            table.SchemaDefinition.RootPage, "b-tree page header");

        table.SqlDefinition = definition;
        return definition;
    }

    /// <summary>
    /// The value of the column named <paramref name="columnName"/>, read as
    /// <see cref="Sqlite3Row.TryGetOrdinal(SqlTableColumn, out object?)"/> reads it. False when the table has no such
    /// column, or the column has no value.
    /// </summary>
    /// <exception cref="SqliteCorruptException">The record is malformed up to that column</exception>
    public static bool TryGetValueByName(this Sqlite3Row row, string columnName, out object? value)
    {
        SqlTableDefinition tableDefinition = row.Table.GetTableDefinition();
        if (!tableDefinition.TryGetColumn(columnName, out SqlTableColumn? column))
        {
            value = null;
            return false;
        }

        return row.TryGetColumn(tableDefinition, column, out value);
    }

    /// <summary>
    /// The value of the column named <paramref name="columnName"/>, read as <see cref="TryGetValueByName(Sqlite3Row, string, out object?)"/>
    /// reads it, as <typeparamref name="T"/>. False when the table has no such column, it has no value (a VIRTUAL
    /// generated column, or a DEFAULT this library does not evaluate), or the value does not convert. The conversions
    /// are those of <see cref="Sqlite3Row.TryGetOrdinal{T}(ushort, out T)"/>: NULL is <c>default</c>, INTEGER and REAL
    /// convert into each other as in SQLite (a REAL truncated toward zero) when the result fits, and TEXT and BLOB
    /// convert to nothing else.
    /// </summary>
    /// <exception cref="SqliteCorruptException">The record is malformed up to that column</exception>
    public static bool TryGetValueByName<T>(this Sqlite3Row row, string columnName, out T? value)
    {
        if (!row.TryGetValueByName(columnName, out object? stored))
        {
            value = default;
            return false;
        }

        return ValueConverter.TryConvert(stored, out value);
    }
}