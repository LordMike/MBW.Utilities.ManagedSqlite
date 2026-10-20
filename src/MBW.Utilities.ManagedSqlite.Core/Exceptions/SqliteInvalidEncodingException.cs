using System;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Exceptions;

public sealed class SqliteInvalidEncodingException : SqliteException
{
    internal SqliteInvalidEncodingException(SqliteEncoding providedEncoding) : base($"An invalid encoding of '{providedEncoding}' was specified in the database. It is possible to provide a fallback encoding using the Settings.")
    {

    }
}