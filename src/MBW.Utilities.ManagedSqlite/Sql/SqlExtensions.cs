using System;
using System.Threading;
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

        // The table was parsed when the schema was loaded, and its definition is built on first use. Threads that ask at
        // once may each build one; the first stored is the one they all return.
        SqlTableDefinition? definition = Volatile.Read(ref schema.Definition);
        if (definition != null)
            return definition;

        definition = SqlParser.Build(schema.Table!, schema.Database.TextEncoding);
        return Interlocked.CompareExchange(ref schema.Definition, definition, null) ?? definition;
    }

    /// <summary>
    /// The columns of the table, from its CREATE TABLE statement, see
    /// <see cref="GetTableDefinition(Sqlite3SchemaRow)"/>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null</exception>
    public static SqlTableDefinition GetTableDefinition(this Sqlite3Table table)
    {
        ArgumentNullException.ThrowIfNull(table);

        // The table's root page was checked against its SQL (PAGE-18) when the table was opened. A definition is stored
        // only once the row's checks passed, so reading values by name takes the stored one without checking again.
        return Volatile.Read(ref table.SchemaRow.Definition) ?? table.SchemaRow.GetTableDefinition();
    }

    /// <summary>
    /// The value of the column named <paramref name="columnName"/>, read as
    /// <see cref="Sqlite3Row.TryGetOrdinal(SqlTableColumn, out object?)"/> reads it. The name is matched as in SQLite,
    /// ignoring the case of ASCII letters only. False when the table has no such column, or the column has no value.
    /// </summary>
    /// <exception cref="SqliteCorruptException">The record is malformed up to that column</exception>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> or <paramref name="columnName"/> is null</exception>
    public static bool TryGetValueByName(this Sqlite3Row row, string columnName, out object? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(columnName);

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
    /// <exception cref="SqliteCorruptException">As for <see cref="TryGetValueByName(Sqlite3Row, string, out object?)"/></exception>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> or <paramref name="columnName"/> is null</exception>
    public static bool TryGetValueByName<T>(this Sqlite3Row row, string columnName, out T? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(columnName);

        if (!row.TryGetValueByName(columnName, out object? stored))
        {
            value = default;
            return false;
        }

        return ValueConverter.TryConvert(stored, out value);
    }
}