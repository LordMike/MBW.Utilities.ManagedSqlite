using System;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Exceptions;

public class SqliteInvalidEncodingException : Exception
{
    public SqliteInvalidEncodingException(SqliteEncoding providedEncoding) : base($"An invalid encoding of '{providedEncoding}' was specified in the database. It is possible to provide a fallback encoding using the Settings.")
    {

    }
}