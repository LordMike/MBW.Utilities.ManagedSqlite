using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests
{
    public class GenericTable_Tests : IDisposable
    {
        private readonly Stream _stream;

        public GenericTable_Tests()
        {
            _stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.AlterTable.db");
        }

        [Fact]
        public void GettingNonExistentTableShouldFail()
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Assert.ThrowsAny<Exception>(() => db.GetTable("BadTable"));
            }
        }

        [Fact]
        public void GettingExistingTableShouldSucceed()
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Assert.NotNull(db.GetTable("MyTable"));
            }
        }

        [Fact]
        public void TryGetTableExistingShouldSucceed()
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Assert.True(db.TryGetTable("MyTable", out var tbl));
                Assert.NotNull(tbl);
            }
        }

        [Fact]
        public void TryGetTableNonExistingShouldFail()
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Assert.False(db.TryGetTable("BadTable", out var tbl));
                Assert.Null(tbl);
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}