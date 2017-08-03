using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sqlite3RoLib.Objects.Enums;
using Sqlite3RoLib.Tables;
using Xunit;

namespace Sqlite3RoLib.Tests
{
    public class BigBlob_Tests : IDisposable
    {
        private readonly Stream _stream;

        private byte[] _expected;

        public BigBlob_Tests()
        {
            _stream = ResourceHelper.OpenResource("Sqlite3RoLib.Tests.Data.BigBlobDb.db");

            using (var fsIn = ResourceHelper.OpenResource("Sqlite3RoLib.Tests.Data.BigBlobDb.bin"))
            using (var ms = new MemoryStream())
            {
                fsIn.CopyTo(ms);
                _expected = ms.ToArray();
            }
        }

        [Fact]
        public void TestRealData()
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                Sqlite3Table tbl = db.GetTable("DataTable");
                List<Sqlite3Row> rows = tbl.EnumerateRows().ToList();

                Assert.Equal(1, rows.Count);

                Sqlite3Row row = rows.Single();

                Assert.Equal(1, row.RowId);

                byte[] actual = row.GetOrdinal<byte[]>(1);

                Assert.Equal(_expected, actual);
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}