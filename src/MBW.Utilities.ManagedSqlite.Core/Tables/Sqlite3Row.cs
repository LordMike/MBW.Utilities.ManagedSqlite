using System;

namespace MBW.Utilities.ManagedSqlite.Core.Tables
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

        public bool TryGetOrdinal<T>(ushort index, out T value)
        {
            value = default(T);

            if (ColumnData.Length > index)
            {
                object tmp = ColumnData[index];

                value = (T)Convert.ChangeType(tmp, typeof(T));
                return true;
            }

            return false;
        }
    }
}