using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class VeryLongText_Tests : IDisposable
{
    private readonly Stream _stream;
    private readonly string _expected;

    public VeryLongText_Tests()
    {
        _stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.VeryLongText.db");

        using Stream fsIn = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.VeryLongText.txt");
        using StreamReader sr = new StreamReader(fsIn);

        _expected = sr.ReadToEnd();
    }

    [Fact]
    public void TestLongText()
    {
        using Sqlite3Database db = new Sqlite3Database(_stream);

        Sqlite3Table tbl = db.GetTable("urls");
        List<Sqlite3Row> rows = tbl.EnumerateRows().ToList();

        Assert.Single(rows);

        Sqlite3Row row = rows.Single();

        Assert.Equal(6014, row.RowId);

        Assert.True(row.TryGetOrdinal(1, out string actual));

        Assert.Equal(_expected, actual);
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }
}