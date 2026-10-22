using System;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core;

public sealed class Sqlite3Settings
{
    /// <summary>
    /// The text encoding to use when the database header's encoding field is 0, which SQLite never writes. Like SQLite,
    /// which then uses the connection's default encoding, the default is UTF-8. Other values of the field are read as
    /// SQLite reads them (the low two bits, where 0 means UTF-8), so this setting does not apply to them.
    ///
    /// This is a dangerous setting, as an incorrect encoding will provide garbage data.
    /// </summary>
    public SqliteEncoding? FallbackEncoding { get; set; }

    /// <summary>
    /// How strictly the database file is validated. <see cref="SqliteValidation.Strict"/> (the default) fails exactly
    /// where SQLite fails.
    /// </summary>
    public SqliteValidation Validation { get; set; } = SqliteValidation.Strict;

    /// <summary>
    /// Called for each check that fails in <see cref="SqliteValidation.Lenient"/> mode. The reader then reads on.
    /// </summary>
    public Action<SqliteValidationIssue>? OnValidationIssue { get; set; }
}