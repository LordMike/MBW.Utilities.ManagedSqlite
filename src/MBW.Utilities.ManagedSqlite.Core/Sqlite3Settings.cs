using System;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core;

public sealed class Sqlite3Settings
{
    /// <summary>
    /// Should the sqlite database have an invalid text encoding, it can be overridden here.
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