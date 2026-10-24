using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Sql;

// The extensions live in the namespace of the types they extend, so a using for Sqlite3Row brings them along. Keep
// them here, not in the namespace of their folder.
#pragma warning disable IDE0130 // Namespace does not match folder structure
// ReSharper disable once CheckNamespace
namespace MBW.Utilities.ManagedSqlite.Core.Tables;
#pragma warning restore IDE0130

public static class SqlExtensions
{
    private const string SqlCacheKey = "SqlCache";

    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null</exception>
    /// <exception cref="InvalidOperationException">The row is not a table (a view, index or trigger)</exception>
    /// <exception cref="NotSupportedException">The row is a virtual table</exception>
    /// <exception cref="SqliteCorruptException">In Lenient, the row failed to load</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3SchemaRow schema)
    {
        if (schema == null)
            throw new ArgumentNullException(nameof(schema));

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

    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3Table table)
    {
        if (table == null)
            throw new ArgumentNullException(nameof(table));

        // The table's root page was checked against its SQL (PAGE-18) when the table was opened
        if (table.SqlDefinition != null)
            return table.SqlDefinition;

        SqlTableDefinition definition = table.SchemaDefinition.GetTableDefinition();

        table.SqlDefinition = definition;
        return definition;
    }

    /// <exception cref="ArgumentNullException"><paramref name="row"/> or <paramref name="columnName"/> is null</exception>
    public static bool TryGetValueByName(this Sqlite3Row row, string columnName, out object? value)
    {
        if (row == null)
            throw new ArgumentNullException(nameof(row));
        if (columnName == null)
            throw new ArgumentNullException(nameof(columnName));

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

        // Like SQLite, the record is decoded only up to this column. A record too short to hold it, such as one written
        // before ALTER TABLE ADD COLUMN, reads the column's default (REC-11). OP_Column in vdbe.c:
        //       if( pOp->p4type==P4_MEM ){
        //         sqlite3VdbeMemShallowCopy(pDest, pOp->p4.pMem, MEM_Static);
        //       }else{
        //         sqlite3VdbeMemSetNull(pDest);
        //       }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3165 (OP_Column)
        if (!row.TryGetField(column.RecordIndex, out value))
        {
            if (!column.HasKnownDefault)
                return false;

            value = column.DefaultValue is byte[] blob ? blob.Clone() : column.DefaultValue;
        }

        // SQLite writes REAL values without a fractional part as integers, and turns them back into REAL when read
        // through a column with REAL affinity
        if (value is long integer && column.DetectedType == typeof(double))
            value = (double)integer;

        return true;
    }

    /// <exception cref="ArgumentNullException"><paramref name="row"/> or <paramref name="columnName"/> is null</exception>
    public static bool TryGetValueByName<T>(this Sqlite3Row row, string columnName, [MaybeNullWhen(false)] out T value)
    {
        if (row == null)
            throw new ArgumentNullException(nameof(row));
        if (columnName == null)
            throw new ArgumentNullException(nameof(columnName));

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