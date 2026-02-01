using System;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public class Sqlite3Row
{
    public Sqlite3Table Table { get; }
    public long RowId { get; }
    public object[] ColumnData { get; }

    internal Sqlite3Row(Sqlite3Table table, long rowId, object[] columnData)
    {
        Table = table;
        RowId = rowId;
        ColumnData = columnData;
    }

    public bool TryGetOrdinal(ushort index, out object value)
    {
        value = default;

        if (ColumnData.Length > index)
        {
            value = ColumnData[index];
            return true;
        }

        return false;
    }

    public bool TryGetOrdinal<T>(ushort index, out T value)
    {
        value = default;

        if (!TryGetOrdinal(index, out object tmp))
            return false;

        // TODO: Is null a success case?
        if (tmp == null)
            return false;

        value = (T)Convert.ChangeType(tmp, typeof(T));
        return true;
    }
}