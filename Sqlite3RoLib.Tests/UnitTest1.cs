using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Sqlite3RoLib.Tests
{
    public static class ResourceHelper
    {
        public static Stream OpenResource(string name)
        {
            return typeof(ResourceHelper).GetTypeInfo().Assembly.GetManifestResourceStream(name);
        }
    }

    public class IntegerData_Tests
    {
        private Stream _stream;

        public IntegerData_Tests()
        {
            _stream = ResourceHelper.OpenResource("Sqlite3RoLib.Tests.Data.IntegerData.db");
        }

        [Fact]
        public void A()
        {
            using (var db = new Sqlite3Database(_stream))
            {
                var tbl = db.GetTable("IntegerTable");
                var rows = tbl.EnumerateRows();

                var data = rows.Select(s => new IntegerRow { RowId = s.RowId, Integer = s.GetOrdinal<long>(1) });


            }
        }

        private class IntegerRow
        {
            public long RowId { get; set; }

            public long Integer { get; set; }
        }
    }
}
