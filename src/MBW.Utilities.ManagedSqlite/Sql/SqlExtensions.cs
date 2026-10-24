using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Sql;

// The extensions live in the namespace of the types they extend, so a using for Sqlite3Row brings them along. Keep
// them here, not in the namespace of their folder.
#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace MBW.Utilities.ManagedSqlite.Core.Tables;
#pragma warning restore IDE0130

/// <summary>
/// Reads a table's columns from its CREATE TABLE statement, and a row's values by column name
/// </summary>
public static class SqlExtensions
{
    private const string SqlCacheKey = "SqlCache";

    /// <summary>
    /// The columns of the table that the row's CREATE TABLE statement made. The statement was parsed when the database
    /// was opened.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null</exception>
    /// <exception cref="InvalidOperationException">The row is not a table (a view, index or trigger)</exception>
    /// <exception cref="NotSupportedException">The row is a virtual table</exception>
    /// <exception cref="SqliteCorruptException">In Lenient, the row failed to load</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3SchemaRow schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

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
            definitionCache[schema] = definition = SqlParser.Build(schema.Table!, schema.Database.TextEncoding);

        return definition;
    }

    /// <summary>
    /// The columns of the table, from its CREATE TABLE statement, see
    /// <see cref="GetTableDefinition(Sqlite3SchemaRow)"/>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3Table table)
    {
        ArgumentNullException.ThrowIfNull(table);

        // The table's root page was checked against its SQL (PAGE-18) when the table was opened
        if (table.SqlDefinition != null)
            return table.SqlDefinition;

        SqlTableDefinition definition = table.SchemaRow.GetTableDefinition();

        table.SqlDefinition = definition;
        return definition;
    }

    /// <summary>
    /// The value of the row's column named <paramref name="columnName"/>, read as SQLite reads it: a long, double,
    /// string, byte[], or null for NULL. The name is matched as in SQLite, ignoring the case of ASCII letters only.
    /// False when the table has no such column, the column is a VIRTUAL generated column, which has no stored value, or
    /// the record is too short to hold the column and its DEFAULT is one this library does not evaluate.
    /// </summary>
    /// <remarks>
    /// Like SQLite, the record is decoded only up to this column. A column that aliases the rowid (an <c>INTEGER
    /// PRIMARY KEY</c>) reads <see cref="Sqlite3Row.RowId"/>, as the record holds NULL for it. A record too short to hold
    /// the column, such as one written before <c>ALTER TABLE ADD COLUMN</c>, reads the column's DEFAULT, or NULL when
    /// it has none. A whole number in a column with REAL affinity reads as a double, as SQLite may store such values as
    /// integers to save space. See the README for how declared types map to affinities.
    /// </remarks>
    /// <exception cref="SqliteCorruptException">The record is malformed up to the column</exception>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> or <paramref name="columnName"/> is null</exception>
    public static bool TryGetValueByName(this Sqlite3Row row, string columnName, out object? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(columnName);

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

    /// <summary>
    /// The value of the row's column named <paramref name="columnName"/>, as
    /// <see cref="TryGetValueByName(Sqlite3Row, string, out object?)"/> reads it, converted to
    /// <typeparamref name="T"/> with <see cref="Convert.ChangeType(object, Type)"/>. False also when the value is NULL.
    /// </summary>
    /// <exception cref="SqliteCorruptException">As for <see cref="TryGetValueByName(Sqlite3Row, string, out object?)"/></exception>
    /// <exception cref="InvalidCastException">The value cannot be converted to <typeparamref name="T"/></exception>
    /// <exception cref="FormatException">The value is text that does not parse as <typeparamref name="T"/></exception>
    /// <exception cref="OverflowException">The value is out of the range of <typeparamref name="T"/></exception>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> or <paramref name="columnName"/> is null</exception>
    public static bool TryGetValueByName<T>(this Sqlite3Row row, string columnName, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(columnName);

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