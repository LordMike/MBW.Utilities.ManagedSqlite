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
    /// HDR-30: SQLite never rejects the encoding field. A non-zero field is read by its low two bits, where 0 means
    /// UTF-8, and a field of 0 means the default, UTF-8.
    /// </summary>
    [Theory]
    [InlineData("UTF-8", 0u)]
    [InlineData("UTF-8", 0x100u)]
    [InlineData("UTF-16le", 6u)]
    [InlineData("UTF-16be", 0xFFFFFFFFu)]
    public void TextEncoding_ReadLikeSqlite(string written, uint field) =>
        SqliteParity.AssertSameAsSqlite($"PRAGMA encoding = '{written}'; CREATE TABLE t (a TEXT); INSERT INTO t VALUES ('abcd'), ('æøå');", data =>
        {
            data[56] = (byte)(field >> 24);
            data[57] = (byte)(field >> 16);
            data[58] = (byte)(field >> 8);
            data[59] = (byte)field;
            return data;
        });

    [Fact]
    public void TextEncoding_ZeroUsesFallbackEncoding()
    {
        const string script = "PRAGMA encoding = 'UTF-16le'; CREATE TABLE t (a TEXT); INSERT INTO t VALUES ('abcd'), ('æøå');";
        byte[] data = SqliteParity.Build(script);
        data[56] = data[57] = data[58] = data[59] = 0;

        using Sqlite3Database db = new Sqlite3Database(new MemoryStream(data), new Sqlite3Settings { FallbackEncoding = SqliteEncoding.UTF16LE });

        Assert.Equal(SqliteEncoding.UTF16LE, db.TextEncoding);
        SqliteParity.AssertTablesSameAsSqlite(script, db, "t");
    }

    // An invalid FallbackEncoding is a caller error, so it fails even when the database's own field is valid
    [Fact]
    public void TextEncoding_InvalidFallbackFails()
    {
        using Stream _stream = new MemoryStream(SqliteParity.Build("CREATE TABLE t (a INTEGER);"));
        Assert.Throws<SqliteInvalidEncodingException>(() => new Sqlite3Database(_stream, new Sqlite3Settings { FallbackEncoding = (SqliteEncoding)7 }));
    }
}
