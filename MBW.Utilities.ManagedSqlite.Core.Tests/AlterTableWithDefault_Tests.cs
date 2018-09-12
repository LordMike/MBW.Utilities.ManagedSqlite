using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests
{
    public class AlterTableWithDefault_Tests : IDisposable
    {
        private readonly Stream _stream;

        public AlterTableWithDefault_Tests()
        {
            _stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.AlterTableWithDefault.db");
        }

        [Fact]
        public void TestTableRowCount()
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Sqlite3Table tbl = db.GetTable("MyTable");
                IEnumerable<Sqlite3Row> rows = tbl.EnumerateRows();

                int actual = rows.Count();

                Assert.Equal(11, actual);
            }
        }

        [Theory]
        [InlineData(0, 0, null)] 
        [InlineData(1, 500, null)]  
        [InlineData(2, 1000, null)] 
        [InlineData(3, 1500, null)] 
        [InlineData(4, 2000, null)] 
        [InlineData(5, 2500, 500)]
        [InlineData(6, 3000, null)]  
        [InlineData(7, 3500, null)] 
        [InlineData(8, 4000, null)] 
        [InlineData(9, 4500, null)] 
        [InlineData(10, 5000, null)]
        public void TestIntegerData(int id, long expectedInteger, long? expectedNewInteger)
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Sqlite3Table tbl = db.GetTable("MyTable");

                Sqlite3Row row = tbl.GetRowById(id);
                Assert.NotNull(row);

                Assert.True(row.TryGetOrdinal(1, out long actual));
                Assert.Equal(expectedInteger, actual);

                if (expectedNewInteger.HasValue)
                {
                    Assert.True(row.TryGetOrdinal(2, out long actualOther));
                    Assert.Equal(expectedNewInteger.Value, actualOther);
                }
                else
                {
                    Assert.False(row.TryGetOrdinal(2, out long _));
                }
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}