using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace MBW.Utilities.ManagedSqlite.Benchmarks;

/// <summary>
/// Listing tables and opening a named table. SQLite has no API for either, so the native side runs the nearest SQL.
/// </summary>
/// <remarks>
/// The managed reader parses sqlite_master once when the database opens and keeps the list, so listing tables on an
/// open database would only count a list. ListTables therefore opens a fresh database on both sides and lists its
/// tables, which makes both read sqlite_master. The native query has to be compiled on each new connection, so that
/// is included there. OpenTable runs on an already-open database, with the native query prepared in setup.
/// </remarks>
public class SchemaBenchmarks
{
    private const string ListTablesSql = "SELECT type, name, tbl_name, rootpage, sql FROM sqlite_master";

    private NativeDatabase _native;
    private SqliteConnection _conn;
    private byte[] _image;
    private SqliteCommand _openTable;
    private sqlite3 _rawDb;
    private sqlite3_stmt _rawOpenTable;
    private Sqlite3Database _db;

    [GlobalSetup]
    public void Setup()
    {
        byte[] image = TestDatabases.Get(1000);
        _image = image;

        _native = new NativeDatabase(image);
        _conn = _native.Open();

        _openTable = _conn.CreateCommand();
        _openTable.CommandText = $"SELECT id, i, r, t, b FROM {TestDatabases.TableName}";
        _openTable.Prepare();

        _rawDb = _native.OpenRaw();
        _rawOpenTable = NativeDatabase.Prepare(_rawDb, _openTable.CommandText);

        _db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(image, false));

        if (SqliteListTables() != ManagedListTables() || AdoNetListTables() != ManagedListTables())
            throw new InvalidOperationException("Native and managed disagree on the number of tables");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _openTable.Dispose();
        _rawOpenTable.Dispose();
        _rawDb.Dispose();
        _conn.Dispose();
        _native.Dispose();
        _db.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ListTables")]
    public int SqliteListTables()
    {
        int count = 0;
        using sqlite3 db = _native.OpenRaw();
        using sqlite3_stmt stmt = NativeDatabase.Prepare(db, ListTablesSql);
        while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
        {
            _ = raw.sqlite3_column_text(stmt, 0).utf8_to_string();
            _ = raw.sqlite3_column_text(stmt, 1).utf8_to_string();
            _ = raw.sqlite3_column_text(stmt, 2).utf8_to_string();
            _ = raw.sqlite3_column_int64(stmt, 3);
            _ = raw.sqlite3_column_text(stmt, 4).utf8_to_string();
            count++;
        }

        return count;
    }

    [Benchmark, BenchmarkCategory("ListTables")]
    public int AdoNetListTables()
    {
        int count = 0;
        using SqliteConnection conn = _native.Open();
        using SqliteCommand listTables = conn.CreateCommand();
        listTables.CommandText = ListTablesSql;

        using SqliteDataReader reader = listTables.ExecuteReader();
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
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(_image, false));
        foreach (Sqlite3SchemaRow _ in db.GetSchema())
            count++;

        return count;
    }

    /// <summary>Starts a query on the table, which positions SQLite on the first row, then stops.</summary>
    [Benchmark(Baseline = true), BenchmarkCategory("OpenTable")]
    public bool SqliteOpenTable()
    {
        bool hasRow = raw.sqlite3_step(_rawOpenTable) == raw.SQLITE_ROW;
        raw.sqlite3_reset(_rawOpenTable);
        return hasRow;
    }

    /// <summary>Starts a query on the table, which positions SQLite on the first row, then stops.</summary>
    [Benchmark, BenchmarkCategory("OpenTable")]
    public bool AdoNetOpenTable()
    {
        using SqliteDataReader reader = _openTable.ExecuteReader();
        return reader.HasRows;
    }

    [Benchmark, BenchmarkCategory("OpenTable")]
    public Sqlite3Table ManagedOpenTable() => _db.GetTable(TestDatabases.TableName);

    /// <summary>
    /// Opens a fresh database and parses a table's definition, which in Strict loads and checks the whole schema first
    /// </summary>
    [Benchmark, BenchmarkCategory("TableDefinition")]
    public int ManagedTableDefinition()
    {
        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(_image, false));
        return db.GetTable(TestDatabases.TableName).GetTableDefinition().Columns.Count;
    }
}
