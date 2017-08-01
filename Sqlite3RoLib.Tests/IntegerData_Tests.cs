using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sqlite3RoLib.Tables;
using Xunit;

namespace Sqlite3RoLib.Tests
{
    public class IntegerData_Tests
    {
        private readonly Stream _stream;

        private Dictionary<int, long> _expectedData = new Dictionary<int, long>
        {
            {0,0L},
            {1,1L},
            {2,255L},
            {3,256L},
            {4,65535L},
            {5,65536L},
            {6,16777215L},
            {7,16777216L},
            {8,4294967295L},
            {9,4294967296L},
            {10,1099511627775L},
            {11,1099511627776L},
            {12,281474976710655L},
            {13,281474976710656L},
            {14,72057594037927935L},
            {15,72057594037927936L},
            {16,-1L},
            {17,-9223372036854775808L},
            {18,-9223372036854775807L},
            {19,-9223372036854775553L},
            {20,-9223372036854775552L},
            {21,-9223372036854710273L},
            {22,-9223372036854710272L},
            {23,-9223372036837998593L},
            {24,-9223372036837998592L},
            {25,-9223372032559808513L},
            {26,-9223372032559808512L},
            {27,-9223370937343148033L},
            {28,-9223370937343148032L},
            {29,-9223090561878065153L},
            {30,-9223090561878065152L},
            {31,-9151314442816847873L},
            {32,-9151314442816847872L},
            {33,-8070450532247928833L},
            {34,-1L},
            {35,-255L},
            {36,-256L},
            {37,-65535L},
            {38,-65536L},
            {39,-16777215L},
            {40,-16777216L},
            {41,-4294967295L},
            {42,-4294967296L},
            {43,-1099511627775L},
            {44,-1099511627776L},
            {45,-281474976710655L},
            {46,-281474976710656L},
            {47,-72057594037927935L},
            {48,-72057594037927936L},
            {49,-72057594037927935L},
        };

        public IntegerData_Tests()
        {
            _stream = ResourceHelper.OpenResource("Sqlite3RoLib.Tests.Data.IntegerData.db");
        }

        [Fact]
        public void TestDataContent()
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Sqlite3Table tbl = db.GetTable("IntegerTable");
                IEnumerable<Sqlite3Row> rows = tbl.EnumerateRows();

                List<IntegerRow> data = rows.Select(s => new IntegerRow
                {
                    RowId = s.RowId,
                    Integer = s.GetOrdinal<long>(1)
                }).ToList();

                // Test data count
                Assert.Equal(_expectedData.Count, data.Count);

                Dictionary<int, long> expectedCopy = _expectedData.ToDictionary(s => s.Key, s => s.Value);

                // Test data content
                foreach (IntegerRow actual in data)
                {
                    Assert.True(expectedCopy.TryGetValue((int)actual.RowId, out long expectedValue));
                    Assert.Equal(expectedValue, actual.Integer);

                    expectedCopy.Remove((int)actual.RowId);
                }

                // Ensure all has been found
                Assert.Empty(expectedCopy);
            }
        }

        private class IntegerRow
        {
            public long RowId { get; set; }

            public long Integer { get; set; }
        }
    }
}