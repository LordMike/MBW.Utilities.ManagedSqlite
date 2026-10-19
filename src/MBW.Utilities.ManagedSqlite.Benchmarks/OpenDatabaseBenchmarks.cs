using System.IO;
using BenchmarkDotNet.Attributes;
using MBW.Utilities.ManagedSqlite.Core;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace MBW.Utilities.ManagedSqlite.Benchmarks;

/// <summary>
/// Opening a database from memory. The managed reader parses the header and the whole sqlite_master table when
/// it opens; SQLite (raw SQLitePCL, and through ADO.NET) defers reading the schema until the first statement, so its number here is the cost of a
/// connection only (the schema read shows up in <see cref="SchemaBenchmarks"/>).
/// </summary>
[BenchmarkCategory("OpenDatabase")]
public class OpenDatabaseBenchmarks
{
    private byte[] _image;
    private NativeDatabase _native;

    [GlobalSetup]
    public void Setup()
    {
        _image = TestDatabases.Get(1000);
        _native = new NativeDatabase(_image);
    }

    [GlobalCleanup]
    public void Cleanup() => _native.Dispose();

    [Benchmark(Baseline = true)]
    public void Sqlite()
    {
        using sqlite3 db = _native.OpenRaw();
    }

    [Benchmark]
    public void AdoNet()
    {
        using SqliteConnection conn = _native.Open();
    }

    [Benchmark]
    public void Managed()
    {
        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(_image, false));
    }
}
