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
    }
}