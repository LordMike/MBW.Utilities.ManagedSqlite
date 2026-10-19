using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using Microsoft.Data.Sqlite;

namespace MBW.Utilities.ManagedSqlite.Benchmarks;

/// <summary>
/// Reading every column of every row of an open table. Both sides box each value into an object, since that is
/// what the managed reader hands out. The native query is prepared in setup.
/// </summary>
[BenchmarkCategory("EnumerateRows")]
public class EnumerateRowsBenchmarks
{
    private NativeDatabase _native;
    private SqliteConnection _conn;
    private SqliteCommand _select;
    private Sqlite3Database _db;
    private Sqlite3Table _table;

    [Params(0, 1, 1000, 100_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] image = TestDatabases.Get(Rows);

        _native = new NativeDatabase(image);
        _conn = _native.Open();
        _select = _conn.CreateCommand();
        _select.CommandText = $"SELECT id, i, r, t, b FROM {TestDatabases.TableName}";
        _select.Prepare();

        _db = new Sqlite3Database(new MemoryStream(image, false));
        _table = _db.GetTable(TestDatabases.TableName);

        if (Native() != Rows || Managed() != Rows)
            throw new InvalidOperationException($"Expected {Rows} rows from both readers");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _select.Dispose();
        _conn.Dispose();
        _native.Dispose();
        _db.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int Native()
    {
        int count = 0;
        using SqliteDataReader reader = _select.ExecuteReader();
        int columns = reader.FieldCount;
        while (reader.Read())
        {
            for (int i = 0; i < columns; i++)
                _ = reader.GetValue(i);
            count++;
        }

        return count;
    }

    [Benchmark]
    public int Managed()
    {
        int count = 0;
        foreach (Sqlite3Row row in _table.EnumerateRows())
        {
            _ = row.ColumnData;
            count++;
        }

        return count;
    }
}
