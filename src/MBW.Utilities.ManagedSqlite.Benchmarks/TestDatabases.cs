using System;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MBW.Utilities.ManagedSqlite.Benchmarks;

/// <summary>
/// Builds the benchmark databases with real SQLite and keeps their bytes in memory. Each database has the table
/// "tbl" with <c>rows</c> rows of mixed column types, plus a handful of small tables so listing tables has
/// something to list.
/// </summary>
internal static class TestDatabases
{
    public const string TableName = "tbl";
    public const int ExtraTables = 9;

    private static readonly ConcurrentDictionary<int, byte[]> Cache = new();

    public static byte[] Get(int rows) => Cache.GetOrAdd(rows, Create);

    private static byte[] Create(int rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mbw-bench-{Guid.NewGuid():N}.db");
        try
        {
            using (SqliteConnection conn = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                conn.Open();
                Exec(conn, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
                Exec(conn, $"CREATE TABLE {TableName} (id INTEGER PRIMARY KEY, i INTEGER, r REAL, t TEXT, b BLOB);");
                for (int n = 0; n < ExtraTables; n++)
                    Exec(conn, $"CREATE TABLE other_{n} (a INTEGER, b TEXT); INSERT INTO other_{n} VALUES ({n}, 'x');");

                using SqliteTransaction tx = conn.BeginTransaction();
                using SqliteCommand insert = conn.CreateCommand();
                insert.CommandText = $"INSERT INTO {TableName} (id, i, r, t, b) VALUES ($id, $i, $r, $t, $b);";
                SqliteParameter pId = insert.Parameters.Add("$id", SqliteType.Integer);
                SqliteParameter pI = insert.Parameters.Add("$i", SqliteType.Integer);
                SqliteParameter pR = insert.Parameters.Add("$r", SqliteType.Real);
                SqliteParameter pT = insert.Parameters.Add("$t", SqliteType.Text);
                SqliteParameter pB = insert.Parameters.Add("$b", SqliteType.Blob);
                insert.Prepare();

                byte[] blob = new byte[16];
                for (int row = 1; row <= rows; row++)
                {
                    BitConverter.TryWriteBytes(blob, (long)row);
                    pId.Value = row;
                    pI.Value = (long)row * 7919;
                    pR.Value = row * 0.5;
                    pT.Value = "row number " + row;
                    pB.Value = blob;
                    insert.ExecuteNonQuery();
                }

                tx.Commit();
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
