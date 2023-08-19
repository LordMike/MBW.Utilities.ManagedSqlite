using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

public class SqlCreateStatements
{
    public static IEnumerable<object[]> CreateStatementsTestsData()
    {
        Assembly assembly = typeof(SqlCreateStatements).Assembly;
        string[] files = {
            $"{assembly.GetName().Name}.Data.From_Chrome.txt",
            $"{assembly.GetName().Name}.Data.From_Firefox.txt",
            $"{assembly.GetName().Name}.Data.From_WindowsActivity.txt",
            $"{assembly.GetName().Name}.Data.From_WindowsActivityCache.txt",
            $"{assembly.GetName().Name}.Data.From_Windows11ActivityDb.txt"
        };

        foreach (string file in files)
        {
            string fileName = string.Join(".", file.Split('.').TakeLast(2));
            SqlTestData data = new SqlTestData(file);

            foreach (SqlTestData.ExpectedTable table in data.Statements)
            {
                yield return new object[] { fileName, table.Name, table };
            }
        }
    }

    [Theory]
    [MemberData(nameof(CreateStatementsTestsData))]
    public void CreateStatementsTests(string fileName, string tableName, SqlTestData.ExpectedTable expectedTable)
    {
        Assert.True(SqlParser.TryParse(expectedTable.Sql, out SqlTableDefinition definition));
        Assert.NotNull(definition);

        SqlTestData.Compare(definition, expectedTable);

        // To create new dumps:
        //using var sw = new StreamWriter(@"N:\Git\Personal\MBW.Utilities.ManagedSqlite\src\MBW.Utilities.ManagedSqlite.Sql.Tests\Data\From_Windows11ActivityDb.new.txt", true);
        //SqlTestData.AppendActual(sw, expectedTable.Sql, definition);
    }
}