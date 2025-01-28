using System.Diagnostics.CodeAnalysis;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public class Sqlite3Row
{
    internal Sqlite3Row(Sqlite3Table table, long rowId, object[] columnData)
    {
        Table = table;
        RowId = rowId;
        ColumnData = columnData;
    }

    public Sqlite3Table Table { get; }
    public long RowId { get; }
    public object[] ColumnData { get; }

    public bool TryGetOrdinal(ushort index, [NotNullWhen(true)]out object? value)
    {
        if (ColumnData.Length <= index)
        {
            value = null;
            return false;
        }

        value = ColumnData[index];
        return true;
    }
}