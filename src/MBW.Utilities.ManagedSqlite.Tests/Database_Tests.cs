using System;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

/// <summary>
/// Opening a database and looking up its tables
/// </summary>
public class Database_Tests
{
    private static Sqlite3Database Open() => Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(SqliteParity.Build(@"
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
    public void TableLookupNullNameThrows()
    {
        using Sqlite3Database db = Open();

        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => db.GetTable(null!)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => db.TryGetTable(null!, out _)).ParamName);
    }

    [Fact]
    public void VirtualTableIsListedButNotRead()
    {
        using Sqlite3Database db = Open();

        Assert.True(db.GetTables().Single(s => s.Name == "docs").IsVirtual);
        Assert.Throws<NotSupportedException>(() => db.GetTable("docs"));
    }

    /// <summary>
    /// A schema row's kind is what its statement made as SQLite loads it, and GetTables lists only the tables. The
    /// last row repeats a name with IF NOT EXISTS, which makes nothing, in SQLite too.
    /// </summary>
    [Theory]
    [InlineData("table", "t", "t", "CREATE TABLE IF NOT EXISTS t (b)")]
    [InlineData("table", "docs", "docs", "CREATE VIRTUAL TABLE IF NOT EXISTS docs USING fts5(b)")]
    [InlineData("view", "v", "v", "CREATE VIEW IF NOT EXISTS v AS SELECT 1")]
    [InlineData("index", "i", "t", "CREATE INDEX IF NOT EXISTS i ON t (a)")]
    [InlineData("trigger", "tr", "t", "CREATE TRIGGER IF NOT EXISTS tr AFTER INSERT ON t BEGIN SELECT 1; END")]
    public void SchemaKindIsWhatTheStatementMade(string type, string name, string tableName, string sql)
    {
        byte[] data = SqliteParity.Build(@"
CREATE TABLE t (a UNIQUE);
CREATE INDEX i ON t (a);
CREATE VIEW v AS SELECT a FROM t;
CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;
CREATE VIRTUAL TABLE docs USING fts5(a);",
            $"INSERT INTO sqlite_schema VALUES ('{type}', '{name}', '{tableName}', 0, '{sql}')");
        SqliteParity.AssertSchemaKindsSameAsSqlite(data);

        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(data));
        Assert.Equal(Sqlite3SchemaKind.None, db.GetSchema().Last().Kind);
    }

    /// <summary>
    /// HDR-30: SQLite never rejects the encoding field. A non-zero field is read by its low two bits, where 0 means
    /// UTF-8, and a field of 0 means the default, UTF-8.
    /// </summary>
    [Theory]
    [InlineData("UTF-8", 1, 0u)]
    [InlineData("UTF-8", 1, 0x100u)]
    [InlineData("UTF-16le", 2, 6u)]
    [InlineData("UTF-16be", 3, 0xFFFFFFFFu)]
    public void TextEncoding_ReadLikeSqlite(string written, byte stored, uint field) =>
        SqliteParity.AssertSameAsSqlite($"PRAGMA encoding = '{written}'; CREATE TABLE t (a TEXT); INSERT INTO t VALUES ('abcd'), ('æøå');", data =>
        {
            Assert.Equal(new byte[] { 0, 0, 0, stored }, data.AsSpan(56, 4).ToArray());
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
        Assert.Equal(new byte[] { 0, 0, 0, 2 }, data.AsSpan(56, 4).ToArray());
        data[56] = data[57] = data[58] = data[59] = 0;

        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(data), new Sqlite3Settings { FallbackEncoding = SqliteEncoding.UTF16LE });

        Assert.Equal(SqliteEncoding.UTF16LE, db.TextEncoding);
        SqliteParity.AssertTablesSameAsSqlite(script, db, "t");
    }

    // An invalid FallbackEncoding is a caller error, so it fails even when the database's own field is valid
    [Fact]
    public void TextEncoding_InvalidFallbackFails()
    {
        using Stream _stream = new MemoryStream(SqliteParity.Build("CREATE TABLE t (a INTEGER);"));
        Assert.Throws<SqliteInvalidEncodingException>(() => Sqlite3Database.FromStreamWithNoJournal(_stream, new Sqlite3Settings { FallbackEncoding = (SqliteEncoding)7 }));
    }

    // So is a Validation that is neither Strict nor Lenient
    [Fact]
    public void Validation_UndefinedValueFails()
    {
        using Stream stream = new MemoryStream(SqliteParity.Build("CREATE TABLE t (a INTEGER);"));
        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => Sqlite3Database.FromStreamWithNoJournal(stream, new Sqlite3Settings { Validation = (SqliteValidation)7 }));
        Assert.Equal("settings", e.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeClosesTheStreamUnlessLeftOpen(bool leaveOpen)
    {
        MemoryStream stream = new MemoryStream(SqliteParity.Build("CREATE TABLE t (a INTEGER);"));

        Sqlite3Database.FromStreamWithNoJournal(stream, leaveOpen: leaveOpen).Dispose();

        Assert.Equal(leaveOpen, stream.CanRead);
    }

    /// <summary>
    /// A stream the reader rejects, and a file too short for its header, are both closed unless left open
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedOpenClosesTheStreamUnlessLeftOpen(bool seekable, bool leaveOpen)
    {
        byte[] bytes = SqliteParity.Build("CREATE TABLE t (a INTEGER);");
        MemoryStream stream = seekable ? new MemoryStream(bytes.Take(50).ToArray()) : new UnseekableStream(bytes);

        Exception e = Assert.ThrowsAny<Exception>(() => Sqlite3Database.FromStreamWithNoJournal(stream, leaveOpen: leaveOpen));

        Assert.IsType(seekable ? typeof(SqliteCorruptException) : typeof(ArgumentException), e);
        Assert.Equal(leaveOpen, stream.CanRead);
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>
    /// The WAL flag is the file format read version, header byte 19; SQLite sets byte 18 (the write version) along with it
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void IsWalModeFollowsTheReadVersion(byte version, bool expected)
    {
        byte[] bytes = SqliteParity.Build("CREATE TABLE t (a INTEGER);");
        bytes[18] = bytes[19] = version;

        using Sqlite3Database db = Sqlite3Database.FromStreamWithNoJournal(new MemoryStream(bytes));

        Assert.Equal(expected, db.IsWalMode);
    }
}
