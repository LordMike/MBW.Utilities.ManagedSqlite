using System;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

/// <summary>
/// Opening a database and looking up its tables
/// </summary>
public class Database_Tests
{
    private static Sqlite3Database Open() => new Sqlite3Database(new MemoryStream(SqliteParity.Build(@"
CREATE TABLE MixedCaseTable (Id INTEGER PRIMARY KEY, Value TEXT);
CREATE INDEX MixedCaseIndex ON MixedCaseTable (Value);
CREATE VIEW MixedCaseView AS SELECT Value FROM MixedCaseTable;
CREATE VIRTUAL TABLE docs USING fts5(title, body);")));

    [Theory]
    [InlineData("MIXEDCASETABLE", "MixedCaseTable")]
    [InlineData("sqlite_master", "sqlite_master")]
    [InlineData("SQLITE_SCHEMA", "sqlite_master")]
    public void GetTableIsCaseInsensitiveAndResolvesMasterAliases(string lookup, string storedName)
    {
        using Sqlite3Database db = Open();

        Assert.Equal(storedName, db.GetTable(lookup).SchemaDefinition.Name);
    }

    [Theory]
    [InlineData("MixedCaseView")]
    [InlineData("docs")]
    public void TryGetTableIsFalseForNonTables(string name)
    {
        using Sqlite3Database db = Open();

        Assert.False(db.TryGetTable(name, out Sqlite3Table tbl));
        Assert.Null(tbl);
    }

    [Fact]
    public void GetTableThrowsKeyNotFoundForMissingTable()
    {
        using Sqlite3Database db = Open();

        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => db.GetTable("Missing"));
    }

    [Fact]
    public void GetTableThrowsNotSupportedForVirtualTable()
    {
        using Sqlite3Database db = Open();

        Assert.Throws<NotSupportedException>(() => db.GetTable("docs"));
    }

    [Fact]
    public void VirtualTableIsListedAndFlagged()
    {
        using Sqlite3Database db = Open();

        Sqlite3SchemaRow docs = db.GetTables().Single(s => s.Name == "docs");
        Assert.True(docs.IsVirtual);
        Assert.Equal(0u, docs.RootPage);
    }

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
}
