﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class BigBlob_Tests : IDisposable
{
    private readonly Stream _stream;

    private readonly byte[] _expected;

    public BigBlob_Tests()
    {
        _stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.BigBlobDb.db");

        using Stream fsIn = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.BigBlobDb.bin");
        using MemoryStream ms = new MemoryStream();
        fsIn.CopyTo(ms);
        _expected = ms.ToArray();
    }

    [Fact]
    public void TestRealData()
    {
        using Sqlite3Database db = new Sqlite3Database(_stream);
        Sqlite3Table tbl = db.GetTable("DataTable");
        List<Sqlite3Row> rows = tbl.EnumerateRows().ToList();

        Assert.Single(rows);

        Sqlite3Row row = rows.Single();

        Assert.Equal(1, row.RowId);

        Assert.True(row.TryGetOrdinal(1, out byte[] actual));

        Assert.Equal(_expected, actual);
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }
}