using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Sql;

/// <summary>
/// A table's columns, from its CREATE TABLE statement
/// </summary>
public sealed class SqlTableDefinition
{
    private readonly List<SqlTableColumn> _columns;
    private readonly Dictionary<string, SqlTableColumn> _columnIndex;

    /// <summary>
    /// The table's name, as its CREATE TABLE statement declares it, without quotes
    /// </summary>
    public string TableName { get; }

    /// <summary>
    /// The table's columns, in declared order, as in <c>SELECT *</c>. This is not the order of the stored record for a
    /// WITHOUT ROWID table, or one with VIRTUAL generated columns; see <see cref="Core.Tables.Sqlite3Row.ColumnData"/>.
    /// </summary>
    public IReadOnlyCollection<SqlTableColumn> Columns { get; }

    /// <summary>
    /// The column that aliases the rowid (an <c>INTEGER PRIMARY KEY</c>), or null if the table has none
    /// </summary>
    public SqlTableColumn? RowIdColumn { get; private set; }

    /// <summary>
    /// True when the SQL declares the table WITHOUT ROWID
    /// </summary>
    internal bool IsWithoutRowId { get; set; }

    internal SqlTableDefinition(string name)
    {
        TableName = name;
        _columns = new List<SqlTableColumn>();
        // Column names match as in SQLite, ignoring the case of ASCII letters only (sqlite3StrICmp in util.c)
        _columnIndex = new Dictionary<string, SqlTableColumn>(AsciiCaseInsensitiveComparer.Instance);

        Columns = new ReadOnlyCollection<SqlTableColumn>(_columns);
    }

    internal void AddColumn(SqlTableColumn column)
    {
        _columnIndex.Add(column.Name, column);
        _columns.Add(column);
    }

    internal void ConfigureRowIdColumn(SqlTableColumn column)
    {
        RowIdColumn = column;
    }

    /// <summary>
    /// Finds a column by name. Like SQLite, the name is matched ignoring the case of ASCII letters only.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null</exception>
    public bool TryGetColumn(string name, [NotNullWhen(true)] out SqlTableColumn? column)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _columnIndex.TryGetValue(name, out column);
    }
}