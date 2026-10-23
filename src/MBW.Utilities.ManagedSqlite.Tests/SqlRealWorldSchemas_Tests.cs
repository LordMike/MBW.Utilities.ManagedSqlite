using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

public class SqlRealWorldSchemas_Tests
{
    [Theory]
    [InlineData("From_Chrome")]
    [InlineData("From_Firefox")]
    [InlineData("From_WindowsActivity")]
    [InlineData("From_WindowsActivityCache")]
    [InlineData("From_Windows11ActivityDb")]
    [InlineData("From_SqliteSyntax")]
    public void SchemaReadsSameAsSqlite(string source)
    {
        using Stream stream = typeof(SqlRealWorldSchemas_Tests).Assembly.GetManifestResourceStream($"MBW.Utilities.ManagedSqlite.Tests.Data.{source}.sql");
        // Foreign keys are off so the default rows insert
        string script = new StreamReader(stream).ReadToEnd();

        // One row per table, so a wrong rowid alias decision shows. Tables with NOT NULL columns without defaults get none.
        string inserts = string.Concat(Regex.Matches(script, @"CREATE TABLE\s+(?:""((?:[^""]|"""")*)""|\[([^\]]*)\]|`([^`]*)`|(\w+))", RegexOptions.IgnoreCase)
            .Select(m => string.Concat(m.Groups[1].Value.Replace("\"\"", "\""), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value))
            .Select(name => $"INSERT OR IGNORE INTO \"{name.Replace("\"", "\"\"")}\" DEFAULT VALUES;\n"));

        SqliteParity.AssertSameAsSqlite("PRAGMA foreign_keys = OFF;\n" + script + "\n" + inserts);
    }
}
