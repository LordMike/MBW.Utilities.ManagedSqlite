using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Core.Tests.Helpers
{
    internal static class DbHelpers
    {
        public static Sqlite3Row GetRowById(this Sqlite3Table tbl, long rowId)
        {
            foreach (var row in tbl.EnumerateRows())
            {
                if (row.RowId != rowId)
                    continue;

                return row;
            }

            return null;
        }
    }
}
