using System;

namespace Sqlite3RoLib.Tables
{
    internal class Sqlite3Row
    {
        public long RowId { get; }
        public object[] ColumnData { get; }

        public Sqlite3Row(long rowId, object[] columnData)
        {
            RowId = rowId;
            ColumnData = columnData;
        }

        public T GetOrdinal<T>(ushort index)
        {
            object value = ColumnData[index];

            return (T)Convert.ChangeType(value, typeof(T));
        }
    }
}