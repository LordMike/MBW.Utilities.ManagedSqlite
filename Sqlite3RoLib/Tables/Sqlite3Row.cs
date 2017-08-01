using System;

namespace Sqlite3RoLib.Tables
{
    public class Sqlite3Row
    {
        public long RowId { get; }
        public object[] ColumnData { get; }

        internal Sqlite3Row(long rowId, object[] columnData)
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