using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Sql;

public static class SqlExtensions
{
    private const string SqlCacheKey = "SqlCache";

    /// <exception cref="InvalidOperationException">The row is not a table (a view, index or trigger)</exception>
    /// <exception cref="NotSupportedException">The row is a virtual table</exception>
    /// <exception cref="SqliteCorruptException">The table's SQL does not parse</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3SchemaRow schema)
    {
        // Only a table has a table definition, so asking for one elsewhere is the caller's error, not damage
        if (!AsciiCaseInsensitiveComparer.Instance.Equals(schema.Type, "table"))
            throw new InvalidOperationException($"{schema.Type} '{schema.Name}' is not a table, so it has no table definition");

        if (schema.IsVirtual)
            throw new NotSupportedException($"'{schema.Name}' is a virtual table, whose columns its module declares");

        // A null value caches that the SQL could not be parsed
        Dictionary<Sqlite3SchemaRow, SqlTableDefinition?> definitionCache = schema.Database.GetProperty(SqlCacheKey, () => new Dictionary<Sqlite3SchemaRow, SqlTableDefinition?>());

        if (!definitionCache.TryGetValue(schema, out SqlTableDefinition? definition))
        {
            if (!SqlParser.TryParse(schema.Sql, out definition))
                definitionCache[schema] = null;
            else
                definitionCache[schema] = definition;
        }

        // SCHEMA-22: SQLite parses each CREATE statement in sqlite_schema when it loads the schema, and an error there
        // makes the database corrupt, "malformed database schema (name) - error". This reader parses a table's SQL when
        // its definition is first asked for, which only moves the failure later. sqlite3InitCallback in prepare.c:
        //   if( SQLITE_OK!=rc ){
        //     ...
        //       corruptSchema(pData, argv, sqlite3_errmsg(db));
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L150 (sqlite3InitCallback)
        Checks.Strict(definition != null, "SCHEMA-22", $"malformed database schema ({schema.Name}): its SQL does not parse as CREATE TABLE",
            structure: "table definition");

        return definition!;
    }

    /// <exception cref="SqliteCorruptException">The SQL declares the table WITHOUT ROWID and its root page is a table
    /// b-tree page, or the other way around</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3Table table)
    {
        SqlTableDefinition definition = table.SchemaDefinition.GetTableDefinition();

        // PAGE-18: a table cursor needs a table b-tree root, and a WITHOUT ROWID (index) cursor an index b-tree root,
        // so a record is never decoded with the other kind's column order. moveToRoot in btree.c:
        //   if( pRoot->isInit==0 || (pCur->pKeyInfo==0)!=pRoot->intKey ){
        //     return SQLITE_CORRUPT_PAGE(pCur->pPage);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5609 (moveToRoot)
        Checks.Strict(definition.IsWithoutRowId == table.IsWithoutRowId, "PAGE-18",
            $"the SQL of table {table.SchemaDefinition.Name} declares {(definition.IsWithoutRowId ? "WITHOUT ROWID" : "a rowid table")}, but its root page is {(table.IsWithoutRowId ? "an index" : "a table")} b-tree page",
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

        // A rowid alias column reads the rowid, which SQLite takes from the cell, not the record, so it reads even when
        // the record is shorter (REC-21)
        if (column == tableDefinition.RowIdColumn)
        {
            value = row.RowId;
            return true;
        }

        // Like SQLite, the record is decoded only up to this column
        if (!row.TryGetField(column.RecordIndex, out value))
            return false;

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