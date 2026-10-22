using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal;

/// <summary>
/// Loads the whole schema as SQLite does before it reads any table: each sqlite_schema row in rowid order, its CREATE
/// statement parsed and checked against the rows before it (sqlite3InitCallback in prepare.c). A malformed schema
/// makes every table unreadable in SQLite, so in <see cref="SqliteValidation.Strict"/> the first table definition asked
/// for fails, and so does every one after it. <see cref="SqliteValidation.Lenient"/> skips this and checks each table
/// by itself.
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L96 (sqlite3InitCallback)
/// </summary>
internal static class SchemaValidator
{
    private const string CacheKey = "SchemaValidation";

    private sealed class Result
    {
        public SqliteCorruptException? Error;
    }

    /// <exception cref="SqliteCorruptException">SQLite would find the schema malformed</exception>
    public static void Validate(Sqlite3Database database)
    {
        if (database.Settings.Validation != SqliteValidation.Strict)
            return;

        // The schema is loaded once; its error is the database's, so it is thrown again each time
        Result result = database.GetProperty(CacheKey, Load);
        if (result.Error != null)
            throw result.Error;
    }

    private static Result Load(Sqlite3Database database)
    {
        SchemaCatalog catalog = new SchemaCatalog();
        try
        {
            foreach (Sqlite3SchemaRow row in database.GetTables())
            {
                // The open has checked each row by itself (SCHEMA-11 to SCHEMA-26), and failed CREATE TABLE ... AS
                // SELECT; a row without a CREATE statement is the index of a PRIMARY KEY or UNIQUE constraint
                if (row.Kind == SchemaObjectKind.None)
                    SchemaParser.AddIndexRow(row, catalog);
                else
                    SchemaParser.ParseSchemaRow(row, database.Settings, catalog);
            }
        }
        catch (SqliteCorruptException e)
        {
            return new Result { Error = e };
        }

        return new Result();
    }
}
