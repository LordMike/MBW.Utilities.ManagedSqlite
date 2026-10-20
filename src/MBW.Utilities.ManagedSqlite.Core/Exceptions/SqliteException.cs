using System;

namespace MBW.Utilities.ManagedSqlite.Core.Exceptions;

/// <summary>
/// Base type for the errors this library raises about the database it is reading.
/// </summary>
public class SqliteException : Exception
{
    internal SqliteException(string message) : base(message)
    {
    }

    internal SqliteException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
