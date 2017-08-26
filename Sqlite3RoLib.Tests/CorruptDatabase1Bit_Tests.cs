using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sqlite3RoLib.Tables;
using Xunit;

namespace Sqlite3RoLib.Tests
{
    public class CorruptDatabase1Bit_Tests
    {
        private readonly byte[] _initialDb;

        public CorruptDatabase1Bit_Tests()
        {
            using (Stream fs = ResourceHelper.OpenResource("Sqlite3RoLib.Tests.Data.CorruptInitial.db"))
            {
                _initialDb = new byte[fs.Length];
                fs.Read(_initialDb, 0, _initialDb.Length);
            }
        }

        public static IEnumerable<object[]> GetTestData(bool valid)
        {
            using (Stream fs = ResourceHelper.OpenResource("Sqlite3RoLib.Tests.Data.CorruptResults.txt"))
            using (StreamReader sr = new StreamReader(fs, Encoding.ASCII))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    string[] splits = line.Split(new[] { ',' }, 3);

                    string dbName = splits[0].Trim();
                    int returnCode = int.Parse(splits[1].Trim());
                    string expectedResult = splits[2].Trim();

                    object[] res = new object[3];
                    res[0] = dbName;
                    res[1] = returnCode;
                    res[2] = expectedResult;

                    if (valid && returnCode == 0)
                        yield return res;
                    else if (!valid && returnCode != 0)
                        yield return res;
                }
            }
        }

        private void FlipBit(byte[] data, int bit)
        {
            // Change 1 bit
            int byteIdx = bit / 8;
            byte mask = (byte)(0x80 >> (bit % 8));

            byte origByte = data[byteIdx];
            data[byteIdx] = (byte)(origByte ^ mask);
        }

        [Theory]
        [MemberData(nameof(GetTestData), true, DisableDiscoveryEnumeration = true)]
        public void TestCorrupt1Bit_Valids(string name, int returnCode, string result)
        {
            Assert.Equal(0, returnCode);

            Match bitMatch = Regex.Match(name, @"db-([\d]+)\.db");
            int bitIdx = int.Parse(bitMatch.Groups[1].Value);

            // Corrupt database
            FlipBit(_initialDb, bitIdx);

            long actualId;
            string actualValue;

            using (MemoryStream ms = new MemoryStream(_initialDb))
            using (Sqlite3Database db = new Sqlite3Database(ms))
            {
                Sqlite3Table tbl = db.GetTable("MyTable");
                Sqlite3Row row = tbl.EnumerateRows().First();

                // Get fields
                Assert.True(row.TryGetOrdinal(0, out actualId));
                Assert.True(row.TryGetOrdinal(1, out actualValue));
            }

            string actual = actualId + "|" + actualValue;
            Assert.Equal(result, actual);
        }

        [Theory]
        [MemberData(nameof(GetTestData), false, DisableDiscoveryEnumeration = true)]
        public void TestCorrupt1Bit_Invalids(string name, int returnCode, string result)
        {
            Assert.NotEqual(0, returnCode);

            Match bitMatch = Regex.Match(name, @"db-([\d]+)\.db");
            int bitIdx = int.Parse(bitMatch.Groups[1].Value);

            // Corrupt database
            FlipBit(_initialDb, bitIdx);

            Assert.ThrowsAny<Exception>(() =>
            {
                using (MemoryStream ms = new MemoryStream(_initialDb))
                using (Sqlite3Database db = new Sqlite3Database(ms))
                {
                    Sqlite3Table tbl = db.GetTable("MyTable");
                    Sqlite3Row row = tbl.EnumerateRows().First();

                    // Get fields
                    row.TryGetOrdinal(0, out int _);
                    row.TryGetOrdinal(1, out string _);
                }
            });
        }
    }
}