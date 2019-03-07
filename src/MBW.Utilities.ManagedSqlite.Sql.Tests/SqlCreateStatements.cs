using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests
{
    public class SqlCreateStatements
    {
        public static IEnumerable<object[]> CreateStatementsTestsData()
        {
            Assembly assembly = typeof(SqlCreateStatements).Assembly;
            string[] files = {
                $"{assembly.GetName().Name}.Data.From_Chrome.txt",
                $"{assembly.GetName().Name}.Data.From_Firefox.txt",
                $"{assembly.GetName().Name}.Data.From_WindowsActivity.txt",
                $"{assembly.GetName().Name}.Data.From_WindowsActivityCache.txt"
            };

            foreach (string file in files)
            {
                SqlTestData data = new SqlTestData(file);

                foreach (SqlTestData.ExpectedTable table in data.Statements)
                {
                    yield return new object[] { table };
                }
            }
        }

        [Theory]
        [MemberData(nameof(CreateStatementsTestsData))]
        public void CreateStatementsTests(SqlTestData.ExpectedTable expectedTable)
        {
            Assert.True(SqlParser.TryParse(expectedTable.Sql, out SqlTableDefinition definition));
            Assert.NotNull(definition);

            SqlTestData.Compare(definition, expectedTable);
        }
    }
}
