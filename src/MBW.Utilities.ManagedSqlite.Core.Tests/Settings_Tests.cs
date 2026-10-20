using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class Settings_Tests
{
    /// <summary>
    /// A valid database whose header text encoding (big-endian uint at offset 56) is patched to 0, which SQLite never writes.
    /// </summary>
    private static Stream InvalidEncodingDb()
    {
        byte[] bytes = SqliteParity.Build("CREATE TABLE t (a INTEGER);");
        bytes[56] = bytes[57] = bytes[58] = bytes[59] = 0;
        return new MemoryStream(bytes);
    }

    [Fact]
    public void TestInvalidEncoding_DefaultFails()
    {
        Assert.Throws<SqliteInvalidEncodingException>(() =>
        {
            using Stream _stream = InvalidEncodingDb();
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
            }
        });
    }

    [Fact]
    public void TestInvalidEncoding_SuccessWhenOverridden()
    {
        Sqlite3Settings sqlite3Settings = new Sqlite3Settings
        {
            FallbackEncoding = SqliteEncoding.UTF8
        };

        using Stream _stream = InvalidEncodingDb();
        using Sqlite3Database db = new Sqlite3Database(_stream, sqlite3Settings);

        Assert.Equal(SqliteEncoding.UTF8, db.TextEncoding);
        Assert.Equal((SqliteEncoding)0, db.Header.TextEncoding);
    }

    [Fact]
    public void TestEncoding_OnlyLowByteIsRead()
    {
        // SQLite 3.45 reads a header encoding of 0x00000101 as UTF-8, because it only uses the field's low byte
        byte[] bytes = SqliteParity.Build("CREATE TABLE t (a INTEGER);");
        bytes[58] = 1;

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(bytes));

        Assert.Equal(SqliteEncoding.UTF8, db.TextEncoding);
    }
}
