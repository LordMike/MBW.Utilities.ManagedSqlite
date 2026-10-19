using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using Microsoft.Data.Sqlite;

namespace MBW.Utilities.ManagedSqlite.Differential.Tests.Helpers;

internal static class DifferentialRunner
{
    /// <summary>
    /// Builds a database from <paramref name="script"/> with real SQLite, reads table <paramref name="tableName"/>
    /// through SQLite and through the library, then hands both to <paramref name="assert"/>.
    /// </summary>
    public static void Run(string script, string tableName, System.Action<ExpectedTable, Sqlite3Database> assert)
    {
        using TempDatabase temp = TempDatabase.Create(script);

        ExpectedTable expected;
        using (SqliteConnection connection = temp.OpenConnection())
            expected = SqliteOracle.ReadTable(connection, tableName);

        using Stream stream = temp.OpenRead();
        using Sqlite3Database db = new Sqlite3Database(stream);

        assert(expected, db);
    }

    /// <summary>
    /// The data check: the library's rows of <paramref name="tableName"/> equal SQLite's, strictly and in order.
    /// </summary>
    public static void AssertTableMatches(string script, string tableName)
    {
        Run(script, tableName, (expected, db) =>
        {
            Sqlite3Table table = db.GetTable(tableName);
            List<Sqlite3Row> rows = table.EnumerateRows().ToList();

            RowAssert.Equal(expected, rows);
        });
    }
}
