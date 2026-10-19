using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace MBW.Utilities.ManagedSqlite.Benchmarks;

/// <summary>
/// Reading every column of every row of an open table. Both sides box each value into an object, since that is
/// what the managed reader hands out. The native queries are prepared in setup.
/// </summary>
[BenchmarkCategory("EnumerateRows")]
public class EnumerateRowsBenchmarks
{
    private NativeDatabase _native;
    private SqliteConnection _conn;
    private SqliteCommand _select;
    private sqlite3 _rawDb;
    private sqlite3_stmt _rawSelect;
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

        _rawDb = _native.OpenRaw();
        _rawSelect = NativeDatabase.Prepare(_rawDb, _select.CommandText);

        _db = new Sqlite3Database(new MemoryStream(image, false));
        _table = _db.GetTable(TestDatabases.TableName);

        if (Sqlite() != Rows || AdoNet() != Rows || Managed() != Rows)
            throw new InvalidOperationException($"Expected {Rows} rows from all readers");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _select.Dispose();
        _rawSelect.Dispose();
        _rawDb.Dispose();
        _conn.Dispose();
        _native.Dispose();
        _db.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int Sqlite()
    {
        int count = 0;
        int columns = raw.sqlite3_column_count(_rawSelect);
        while (raw.sqlite3_step(_rawSelect) == raw.SQLITE_ROW)
        {
            for (int i = 0; i < columns; i++)
                _ = NativeDatabase.ReadValue(_rawSelect, i);
            count++;
        }

        raw.sqlite3_reset(_rawSelect);
        return count;
    }

    [Benchmark]
    public int AdoNet()
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
