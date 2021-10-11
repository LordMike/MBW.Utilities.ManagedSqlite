using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests
{
    public class SqlStatementsFromDatabase
    {
        public static IEnumerable<object[]> CreateStatementsTestsData()
        {
            Assembly assembly = typeof(SqlCreateStatements).Assembly;
            string[] files = {
                $"{assembly.GetName().Name}.Data.From_Chrome.txt",
                //$"{assembly.GetName().Name}.Data.From_Firefox.txt",
                //$"{assembly.GetName().Name}.Data.From_WindowsActivity.txt",
                //$"{assembly.GetName().Name}.Data.From_WindowsActivityCache.txt"
            };

            foreach (string file in files)
            {
                string dbFile = Path.ChangeExtension(file, ".db.gz");

                SqlTestData data = new SqlTestData(file);

                yield return new object[]
                {
                    dbFile, data
                };
            }
        }

        [Theory]
        [MemberData(nameof(CreateStatementsTestsData))]
        public void ReadCreateStatementsFromDbTests(string dbFile, SqlTestData data)
        {
            Assembly assembly = typeof(SqlCreateStatements).Assembly;

            using (MemoryStream ms = new MemoryStream())
            {
                using (Stream fs = assembly.GetManifestResourceStream(dbFile))
                using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
                {
                    gz.CopyTo(ms);
                    ms.Seek(0, SeekOrigin.Begin);
                }

                // Ensure that the DB matches the expectations
                using (Sqlite3Database db = new Sqlite3Database(ms))
                {
                    List<Sqlite3SchemaRow> dbTables = db.GetTables().Where(s => !s.TableName.StartsWith("sqlite_", StringComparison.Ordinal)).Where(s => s.Type == "table").ToList();

                    Assert.Equal(data.Statements.Count, dbTables.Count);

                    List<string> dbTableNames = dbTables.Select(s => s.TableName).ToList();
                    List<string> expectedTabledNames = data.Statements.Select(s => s.Name).ToList();

                    Assert.All(dbTableNames, x => Assert.Contains(x, expectedTabledNames));
                    Assert.All(expectedTabledNames, x => Assert.Contains(x, dbTableNames));

                    Assert.All(dbTables, tbl =>
                    {
                        SqlTestData.ExpectedTable expected = data.Statements.First(s => s.Name == tbl.TableName);
                        SqlTableDefinition definition = tbl.GetTableDefinition();

                        SqlTestData.Compare(definition, expected);
                    });
                }

            }
        }
    }
}