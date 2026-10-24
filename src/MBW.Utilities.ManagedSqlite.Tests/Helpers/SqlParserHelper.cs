using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Sql;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Tests.Helpers;

public static class SqlParserHelper
{
    /// <summary>
    /// Parses a CREATE TABLE statement in <see cref="SqliteValidation.Strict"/>, false when SQLite would refuse it
    /// </summary>
    internal static bool TryParse(string sql, out SqlTableDefinition tableDefinition)
    {
        try
        {
            tableDefinition = SqlParser.Build(SchemaParser.ParseCreateTable(sql, "", new Sqlite3Settings()));
            return true;
        }
        catch (SqliteCorruptException)
        {
            tableDefinition = null;
            return false;
        }
    }
}
