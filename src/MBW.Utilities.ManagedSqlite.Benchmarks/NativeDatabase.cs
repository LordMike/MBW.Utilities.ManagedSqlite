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

    public void Dispose() => NativeMemory.Free((void*)_data);
}
