using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using Microsoft.Data.Sqlite;

namespace MBW.Utilities.ManagedSqlite.Benchmarks;

/// <summary>
/// Listing tables and opening a named table on an already-open database. SQLite has no API for either, so the
/// native side runs the nearest SQL with commands prepared in setup, outside the measured region.
/// </summary>
public class SchemaBenchmarks
{
    private NativeDatabase _native;
    private SqliteConnection _conn;
    private SqliteCommand _listTables;
    private SqliteCommand _openTable;
    private Sqlite3Database _db;

    [GlobalSetup]
    public void Setup()
    {
        byte[] image = TestDatabases.Get(1000);

        _native = new NativeDatabase(image);
        _conn = _native.Open();

        _listTables = _conn.CreateCommand();
        _listTables.CommandText = "SELECT type, name, tbl_name, rootpage, sql FROM sqlite_master";
        _listTables.Prepare();

        _openTable = _conn.CreateCommand();
        _openTable.CommandText = $"SELECT id, i, r, t, b FROM {TestDatabases.TableName}";
        _openTable.Prepare();

        _db = new Sqlite3Database(new MemoryStream(image, false));

        if (NativeListTables() != ManagedListTables())
            throw new InvalidOperationException("Native and managed disagree on the number of tables");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listTables.Dispose();
        _openTable.Dispose();
        _conn.Dispose();
        _native.Dispose();
        _db.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ListTables")]
    public int NativeListTables()
    {
        int count = 0;
        using SqliteDataReader reader = _listTables.ExecuteReader();
        while (reader.Read())
        {
            _ = reader.GetString(0);
            _ = reader.GetString(1);
            _ = reader.GetString(2);
            _ = reader.GetInt64(3);
            _ = reader.GetString(4);
            count++;
        }

        return count;
    }

    [Benchmark, BenchmarkCategory("ListTables")]
    public int ManagedListTables()
    {
        int count = 0;
        foreach (Sqlite3SchemaRow _ in _db.GetTables())
            count++;

        return count;
    }

    /// <summary>Starts a query on the table, which positions SQLite on the first row, then stops.</summary>
    [Benchmark(Baseline = true), BenchmarkCategory("OpenTable")]
    public bool NativeOpenTable()
    {
        using SqliteDataReader reader = _openTable.ExecuteReader();
        return reader.HasRows;
    }

    [Benchmark, BenchmarkCategory("OpenTable")]
    public Sqlite3Table ManagedOpenTable() => _db.GetTable(TestDatabases.TableName);
}
