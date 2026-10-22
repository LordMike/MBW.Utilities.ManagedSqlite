using System.Runtime.CompilerServices;

// The types of this package now live in MBW.Utilities.ManagedSqlite. This package forwards them there, so code built
// against an older version keeps working; new code should reference MBW.Utilities.ManagedSqlite directly.
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Sql.SqlExtensions))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Sql.SqlTableColumn))]
[assembly: TypeForwardedTo(typeof(MBW.Utilities.ManagedSqlite.Sql.SqlTableDefinition))]
