using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

public class SqlRealWorldSchemas_Tests
{
    [Theory]
    [InlineData("From_Chrome")]
    [InlineData("From_Firefox")]
    [InlineData("From_WindowsActivity")]
    [InlineData("From_WindowsActivityCache")]
    [InlineData("From_Windows11ActivityDb")]
    public void SchemaReadsSameAsSqlite(string source)
    {
        using Stream stream = typeof(SqlRealWorldSchemas_Tests).Assembly.GetManifestResourceStream($"MBW.Utilities.ManagedSqlite.Sql.Tests.Data.{source}.sql");
        // WITHOUT ROWID tables are not supported by the Core reader. Foreign keys are off so the default rows insert.
        string script = string.Join("\n", new StreamReader(stream).ReadToEnd().Split('\n')
            .Where(s => !s.Contains("WITHOUT ROWID", System.StringComparison.OrdinalIgnoreCase)));

        // One row per table, so a wrong rowid alias decision shows. Tables with NOT NULL columns without defaults get none.
        string inserts = string.Concat(Regex.Matches(script, @"CREATE TABLE\s+[""\[]?(\w+)", RegexOptions.IgnoreCase)
            .Select(m => $"INSERT OR IGNORE INTO \"{m.Groups[1].Value}\" DEFAULT VALUES;\n"));

        SqliteParity.AssertSameAsSqlite("PRAGMA foreign_keys = OFF;\n" + script + "\n" + inserts);
    }
}
