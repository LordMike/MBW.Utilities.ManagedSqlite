using System;

namespace MBW.Utilities.ManagedSqlite.Core.Exceptions;

/// <summary>
/// <see cref="Sqlite3Settings.FallbackEncoding"/> is not a valid encoding. The database's own encoding field is never
/// rejected, as in SQLite.
/// </summary>
public sealed class SqliteInvalidEncodingException : SqliteException
{
    internal SqliteInvalidEncodingException(SqliteEncoding providedEncoding) : base($"An invalid fallback encoding of '{providedEncoding}' was specified in the settings.")
    {

    }
}