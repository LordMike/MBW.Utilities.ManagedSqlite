using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sqlite3RoLib.Tables;
using Xunit;

namespace Sqlite3RoLib.Tests
{
    public class RealData_Tests : IDisposable
    {
        private readonly Stream _stream;

        private static Dictionary<int, long> _testData = new Dictionary<int, long>
        {
            { 0, 0L},
            { 1, -4616189618054758400L},
            { 2, 4607182418800017408L},
            { 3, -4503599627370497L},
            { 4, 9218868437227405311L},
            { 5, -4571364728013586432L},
            { 6, 4652007308841189376L},
            { 7, 1L},
            { 8, 2L},
            { 9, 4L},
            { 10, 8L},
            { 11, 16L},
            { 12, 32L},
            { 13, 64L},
            { 14, 128L},
            { 15, 256L},
            { 16, 512L},
            { 17, 1024L},
            { 18, 2048L},
            { 19, 4096L},
            { 20, 8192L},
            { 21, 16384L},
            { 22, 32768L},
            { 23, 65536L},
            { 24, 131072L},
            { 25, 262144L},
            { 26, 524288L},
            { 27, 1048576L},
            { 28, 2097152L},
            { 29, 4194304L},
            { 30, 8388608L},
            { 31, 16777216L},
            { 32, 33554432L},
            { 33, 67108864L},
            { 34, 134217728L},
            { 35, 268435456L},
            { 36, 536870912L},
            { 37, 1073741824L},
            { 38, 2147483648L},
            { 39, 4294967296L},
            { 40, 8589934592L},
            { 41, 17179869184L},
            { 42, 34359738368L},
            { 43, 68719476736L},
            { 44, 137438953472L},
            { 45, 274877906944L},
            { 46, 549755813888L},
            { 47, 1099511627776L},
            { 48, 2199023255552L},
            { 49, 4398046511104L},
            { 50, 8796093022208L},
            { 51, 17592186044416L},
            { 52, 35184372088832L},
            { 53, 70368744177664L},
            { 54, 140737488355328L},
            { 55, 281474976710656L},
            { 56, 562949953421312L},
            { 57, 1125899906842624L},
            { 58, 2251799813685248L},
            { 59, 4503599627370496L},
            { 60, 9007199254740992L},
            { 61, 18014398509481984L},
            { 62, 36028797018963968L},
            { 63, 72057594037927936L},
            { 64, 144115188075855872L},
            { 65, 288230376151711744L},
            { 66, 576460752303423488L},
            { 67, 1152921504606846976L},
            { 68, 2305843009213693952L},
            { 69, 4611686018427387904L},
            { 70, 255L},
            { 71, 510L},
            { 72, 1020L},
            { 73, 2040L},
            { 74, 4080L},
            { 75, 8160L},
            { 76, 16320L},
            { 77, 32640L},
            { 78, 65280L},
            { 79, 130560L},
            { 80, 261120L},
            { 81, 522240L},
            { 82, 1044480L},
            { 83, 2088960L},
            { 84, 4177920L},
            { 85, 8355840L},
            { 86, 16711680L},
            { 87, 33423360L},
            { 88, 66846720L},
            { 89, 133693440L},
            { 90, 267386880L},
            { 91, 534773760L},
            { 92, 1069547520L},
            { 93, 2139095040L},
            { 94, 255L},
            { 95, 510L},
            { 96, 1020L},
            { 97, 2040L},
            { 98, 4080L},
            { 99, 8160L},
            { 100, 16320L},
            { 101, 32640L},
            { 102, 65280L},
            { 103, 130560L},
            { 104, 261120L},
            { 105, 522240L},
            { 106, 1044480L},
            { 107, 2088960L},
            { 108, 4177920L},
            { 109, 8355840L},
            { 110, 16711680L},
            { 111, 33423360L},
            { 112, 66846720L},
            { 113, 133693440L},
            { 114, 267386880L},
            { 115, 534773760L},
            { 116, 1069547520L},
            { 117, 2139095040L}
        };

        public RealData_Tests()
        {
            _stream = ResourceHelper.OpenResource("Sqlite3RoLib.Tests.Data.RealData.db");
        }

        public static IEnumerable<object[]> TestDataEmitter()
        {
            foreach (KeyValuePair<int, long> row in _testData)
            {
                yield return new object[] { row.Key, row.Value };
            }
        }

        [Fact]
        public void TestRealRowCount()
        {
            int expected = _testData.Count;

            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Sqlite3Table tbl = db.GetTable("RealTable");
                IEnumerable<Sqlite3Row> rows = tbl.EnumerateRows();

                int actual = rows.Count();

                Assert.Equal(expected, actual);
            }
        }

        [Theory]
        [MemberData(nameof(TestDataEmitter))]
        public void TestRealData(int id, long expectedLong)
        {
            double expected = BitConverter.Int64BitsToDouble(expectedLong);

            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Sqlite3Table tbl = db.GetTable("RealTable");
                IEnumerable<Sqlite3Row> rows = tbl.EnumerateRows();

                foreach (Sqlite3Row row in rows)
                {
                    double actual = row.GetOrdinal<double>(1);

                    if (row.RowId == id)
                    {
                        Assert.Equal(expected, actual);
                        return;
                    }
                }

                Assert.True(false, "Number is missing");
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}