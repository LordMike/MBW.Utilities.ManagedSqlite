using System;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace MBW.Utilities.ManagedSqlite.Benchmarks;

/// <summary>
/// Holds a database image in unmanaged memory so real SQLite can open it as an in-memory database without
/// copying, which mirrors handing the managed reader a <see cref="System.IO.MemoryStream"/>.
/// </summary>
internal sealed unsafe class NativeDatabase : IDisposable
{
    private readonly IntPtr _data;
    private readonly long _length;

    public NativeDatabase(byte[] image)
    {
        _length = image.Length;
        _data = (IntPtr)NativeMemory.Alloc((nuint)image.Length);
        image.CopyTo(new Span<byte>((void*)_data, image.Length));
    }

    /// <summary>Opens a new, unpooled connection on the image. SQLite reads the schema lazily, on the first statement.</summary>
    public SqliteConnection Open()
    {
        SqliteConnection conn = new SqliteConnection("Data Source=:memory:;Pooling=False");
        conn.Open();

        int rc = raw.sqlite3_deserialize(conn.Handle, "main", _data, _length, _length, raw.SQLITE_DESERIALIZE_READONLY);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"sqlite3_deserialize failed: {rc}");

        return conn;
    }

    /// <summary>
    /// Opens the image through SQLitePCL directly, skipping ADO.NET. The caller closes the handle.
    /// </summary>
    public sqlite3 OpenRaw()
    {
        int rc = raw.sqlite3_open_v2(":memory:", out sqlite3 db, raw.SQLITE_OPEN_READWRITE, null);
        Check(db, rc);

        rc = raw.sqlite3_deserialize(db, "main", _data, _length, _length, raw.SQLITE_DESERIALIZE_READONLY);
        Check(db, rc);

        return db;
    }

    public static sqlite3_stmt Prepare(sqlite3 db, string sql)
    {
        Check(db, raw.sqlite3_prepare_v2(db, sql, out sqlite3_stmt stmt));
        return stmt;
    }

    /// <summary>Reads a column the way SqliteDataReader.GetValue does, as a boxed long, double, string or byte[].</summary>
    public static object ReadValue(sqlite3_stmt stmt, int column)
    {
        switch (raw.sqlite3_column_type(stmt, column))
        {
            case raw.SQLITE_INTEGER:
                return raw.sqlite3_column_int64(stmt, column);
            case raw.SQLITE_FLOAT:
                return raw.sqlite3_column_double(stmt, column);
            case raw.SQLITE_TEXT:
                return raw.sqlite3_column_text(stmt, column).utf8_to_string();
            case raw.SQLITE_BLOB:
                return raw.sqlite3_column_blob(stmt, column).ToArray();
            default:
                return null;
        }
    }

    private static void Check(sqlite3 db, int rc)
    {
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"SQLite error {rc}: {raw.sqlite3_errmsg(db).utf8_to_string()}");
    }

    public void Dispose() => NativeMemory.Free((void*)_data);
}
