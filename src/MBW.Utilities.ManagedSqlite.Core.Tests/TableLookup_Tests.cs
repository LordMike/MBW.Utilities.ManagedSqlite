using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class TableLookup_Tests
{
    private static Sqlite3Database OpenMixedCase() =>
        new Sqlite3Database(ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.MixedCaseName.db"));

    private static Sqlite3Database OpenVirtualTable() =>
        new Sqlite3Database(ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.VirtualTable.db"));

    [Theory]
    [InlineData("MixedCaseTable")]
    [InlineData("mixedcasetable")]
    [InlineData("MIXEDCASETABLE")]
    [InlineData("mIxEdCaSeTaBlE")]
    public void TableLookupIsCaseInsensitive(string name)
    {
        using Sqlite3Database db = OpenMixedCase();

        Assert.True(db.TryGetTable(name, out Sqlite3Table tbl));
        Assert.Equal("MixedCaseTable", tbl.SchemaDefinition.Name);
        Assert.Equal(3, tbl.EnumerateRows().Count());

        Assert.Equal(3, db.GetTable(name).EnumerateRows().Count());
    }

    [Theory]
    [InlineData("MixedCaseIndex")]
    [InlineData("MixedCaseView")]
    public void NonTableObjectsAreNotReturned(string name)
    {
        using Sqlite3Database db = OpenMixedCase();

        Assert.False(db.TryGetTable(name, out Sqlite3Table tbl));
        Assert.Null(tbl);
    }

    [Fact]
    public void MissingTableThrowsKeyNotFound()
    {
        using Sqlite3Database db = OpenMixedCase();

        KeyNotFoundException ex = Assert.Throws<KeyNotFoundException>(() => db.GetTable("BadTable"));
        Assert.Contains("BadTable", ex.Message);
    }

    [Theory]
    [InlineData("sqlite_master")]
    [InlineData("sqlite_schema")]
    [InlineData("SQLITE_MASTER")]
    [InlineData("Sqlite_Schema")]
    public void MasterTableCanBeOpened(string name)
    {
        using Sqlite3Database db = OpenMixedCase();

        Assert.True(db.TryGetTable(name, out Sqlite3Table tbl));
        Assert.Equal("sqlite_master", tbl.SchemaDefinition.Name);

        List<Sqlite3Row> rows = db.GetTable(name).EnumerateRows().ToList();
        Assert.Equal(3, rows.Count);

        List<string> names = rows.Select(r =>
        {
            Assert.True(r.TryGetOrdinal(1, out string n));
            return n;
        }).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "MixedCaseIndex", "MixedCaseTable", "MixedCaseView" }, names);
    }

    [Fact]
    public void VirtualTableIsListedAndFlagged()
    {
        using Sqlite3Database db = OpenVirtualTable();

        Sqlite3SchemaRow docs = db.GetTables().Single(s => s.Name == "docs");
        Assert.Equal("table", docs.Type);
        Assert.Equal(0u, docs.RootPage);
        Assert.True(docs.IsVirtual);

        Assert.All(db.GetTables().Where(s => s.Name != "docs"), s => Assert.False(s.IsVirtual));
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("DOCS")]
    public void VirtualTableIsNotReturnedByTryGetTable(string name)
    {
        using Sqlite3Database db = OpenVirtualTable();

        Assert.False(db.TryGetTable(name, out Sqlite3Table tbl));
        Assert.Null(tbl);
    }

    [Fact]
    public void VirtualTableGetTableThrowsNotSupported()
    {
        using Sqlite3Database db = OpenVirtualTable();

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => db.GetTable("docs"));
        Assert.Contains("docs", ex.Message);
        Assert.Contains("virtual", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VirtualTableShadowTablesAreReadable()
    {
        using Sqlite3Database db = OpenVirtualTable();

        List<Sqlite3Row> rows = db.GetTable("docs_content").EnumerateRows().OrderBy(r => r.RowId).ToList();
        Assert.Equal(3, rows.Count);

        Assert.Equal(new long[] { 1, 2, 3 }, rows.Select(r => r.RowId));
        Assert.True(rows[0].TryGetOrdinal(1, out string title));
        Assert.Equal("First", title);
        Assert.True(rows[0].TryGetOrdinal(2, out string body));
        Assert.Equal("Hello world", body);

        Assert.Equal(2, db.GetTable("Plain").EnumerateRows().Count());
    }
}
