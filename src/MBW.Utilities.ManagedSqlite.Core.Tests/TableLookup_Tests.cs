using System;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class TableLookup_Tests
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
}
