using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ManagedSqlite3.Core.Tables;
using ManagedSqlite3.Core.Tests.Helpers;
using Xunit;

namespace ManagedSqlite3.Core.Tests
{
    public class MediumDb_Tests : IDisposable
    {
        private readonly Stream _stream;

        public MediumDb_Tests()
        {
            _stream = ResourceHelper.OpenResource("Sqlite3RoLib.Tests.Data.MediumDb.db");
        }

        [Fact]
        public void TestTableNames()
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                List<Sqlite3SchemaRow> tables = db.GetTables().ToList();

                Assert.Equal(50, tables.Count);

                List<string> actualNames = tables.Select(s => s.TableName).ToList();

                for (int i = 0; i < 50; i++)
                {
                    string expectedName = $"Table{i:00}";

                    Assert.Contains(expectedName, actualNames);
                }
            }
        }

        [Fact]
        public void TestTableRowCounts()
        {
            using (Sqlite3Database db = new Sqlite3Database(_stream))
            {
                List<Sqlite3SchemaRow> tables = db.GetTables().ToList();

                Assert.Equal(50, tables.Count);

                List<string> actualNames = tables.Select(s => s.TableName).ToList();

                foreach (string name in actualNames)
                {
                    int actualCount = db.GetTable(name).EnumerateRows().Count();

                    Assert.Equal(500, actualCount);
                }
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}