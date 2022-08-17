using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MBW.Utilities.ManagedSqlite.Sql;

public class SqlTableDefinition
{
    private readonly List<SqlTableColumn> _columns;
    private readonly Dictionary<string, SqlTableColumn> _columnIndex;

    public string TableName { get; }
    public IReadOnlyCollection<SqlTableColumn> Columns { get; }
    public SqlTableColumn RowIdColumn { get; private set; }

    public SqlTableDefinition(string name)
    {
        TableName = name;
        _columns = new List<SqlTableColumn>();
        _columnIndex = new Dictionary<string, SqlTableColumn>(StringComparer.OrdinalIgnoreCase);

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

    public bool TryGetColumn(string name, out SqlTableColumn column)
    {
        return _columnIndex.TryGetValue(name, out column);
    }
}