using System.Runtime.CompilerServices;

// The types of this package now live in MBW.Utilities.ManagedSqlite. This package forwards them there, so code built
// against an older version keeps working; new code should reference MBW.Utilities.ManagedSqlite directly.
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.Exceptions.SqliteCorruptException))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.Exceptions.SqliteException))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.Exceptions.SqliteInvalidEncodingException))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.Objects.Enums.SqliteEncoding))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.Sqlite3Database))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.Sqlite3Settings))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.SqliteValidation))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.SqliteValidationIssue))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.Tables.Sqlite3Row))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.Tables.Sqlite3SchemaRow))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Core.Tables.Sqlite3Table))]
