using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests
{
    public class IntegerData_Tests : IDisposable
    {
        private readonly Stream _stream;

        public IntegerData_Tests()
        {
            _stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.IntegerData.db");
        }

        [Theory]
        [InlineData(50)]
        public void TestIntegerRowCount(int expected)
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Sqlite3Table tbl = db.GetTable("IntegerTable");
                IEnumerable<Sqlite3Row> rows = tbl.EnumerateRows();

                int actual = rows.Count();

                Assert.Equal(expected, actual);
            }
        }

        [Theory]
        [InlineData(0, 0L)]
        [InlineData(1, 1L)]
        [InlineData(2, 255L)]
        [InlineData(3, 256L)]
        [InlineData(4, 65535L)]
        [InlineData(5, 65536L)]
        [InlineData(6, 16777215L)]
        [InlineData(7, 16777216L)]
        [InlineData(8, 4294967295L)]
        [InlineData(9, 4294967296L)]
        [InlineData(10, 1099511627775L)]
        [InlineData(11, 1099511627776L)]
        [InlineData(12, 281474976710655L)]
        [InlineData(13, 281474976710656L)]
        [InlineData(14, 72057594037927935L)]
        [InlineData(15, 72057594037927936L)]
        [InlineData(16, -1L)]
        [InlineData(17, -9223372036854775808L)]
        [InlineData(18, -9223372036854775807L)]
        [InlineData(19, -9223372036854775553L)]
        [InlineData(20, -9223372036854775552L)]
        [InlineData(21, -9223372036854710273L)]
        [InlineData(22, -9223372036854710272L)]
        [InlineData(23, -9223372036837998593L)]
        [InlineData(24, -9223372036837998592L)]
        [InlineData(25, -9223372032559808513L)]
        [InlineData(26, -9223372032559808512L)]
        [InlineData(27, -9223370937343148033L)]
        [InlineData(28, -9223370937343148032L)]
        [InlineData(29, -9223090561878065153L)]
        [InlineData(30, -9223090561878065152L)]
        [InlineData(31, -9151314442816847873L)]
        [InlineData(32, -9151314442816847872L)]
        [InlineData(33, -8070450532247928833L)]
        [InlineData(34, -1L)]
        [InlineData(35, -255L)]
        [InlineData(36, -256L)]
        [InlineData(37, -65535L)]
        [InlineData(38, -65536L)]
        [InlineData(39, -16777215L)]
        [InlineData(40, -16777216L)]
        [InlineData(41, -4294967295L)]
        [InlineData(42, -4294967296L)]
        [InlineData(43, -1099511627775L)]
        [InlineData(44, -1099511627776L)]
        [InlineData(45, -281474976710655L)]
        [InlineData(46, -281474976710656L)]
        [InlineData(47, -72057594037927935L)]
        [InlineData(48, -72057594037927936L)]
        [InlineData(49, -72057594037927935L)]
        public void TestIntegerData(int id, long expected)
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Sqlite3Table tbl = db.GetTable("IntegerTable");
                Sqlite3Row row = tbl.GetRowById(id);

                Assert.NotNull(row);

                Assert.True(row.TryGetOrdinal(1, out long actual));
                Assert.Equal(expected, actual);
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}