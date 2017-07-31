namespace Sqlite3RoLib
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