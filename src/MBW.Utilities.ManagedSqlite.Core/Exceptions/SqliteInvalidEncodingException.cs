using System;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Exceptions;

public class SqliteInvalidEncodingException(SqliteEncoding providedEncoding) : Exception($"An invalid encoding of '{providedEncoding}' was specified in the database. It is possible to provide a fallback encoding using the Settings.");