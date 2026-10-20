using System;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public class Sqlite3Row
{
    public Sqlite3Table Table { get; }

    /// <summary>
    /// The row's rowid. A row of a WITHOUT ROWID table has none, and reads 0 here.
    /// </summary>
    public long RowId { get; }

    /// <summary>
    /// The values of the row's record, in the order they are stored. For a rowid table that is the declared column
    /// order. For a WITHOUT ROWID table it is the primary key columns first, in primary key order, then the other
    /// columns in declared order; the Sql package's <c>TryGetValueByName</c> resolves names in both cases.
    /// </summary>
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