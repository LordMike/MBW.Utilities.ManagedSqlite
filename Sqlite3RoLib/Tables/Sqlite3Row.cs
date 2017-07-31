namespace Sqlite3RoLib.Tables
{
    internal class Sqlite3Row
    {
        public object[] ColumnData { get; }

        public Sqlite3Row(object[] columnData)
        {
            ColumnData = columnData;
        }
    }
}