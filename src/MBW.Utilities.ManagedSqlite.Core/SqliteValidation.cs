namespace MBW.Utilities.ManagedSqlite.Core;

/// <summary>
/// How strictly the reader validates the database file. See <see cref="Sqlite3Settings.Validation"/>.
/// </summary>
public enum SqliteValidation : byte
{
    /// <summary>
    /// Fail exactly where SQLite fails, at the same point (database open, table open, or the row). The default.
    /// </summary>
    Strict,

    /// <summary>
    /// Read on past damage that SQLite rejects but that leaves the data intact, such as an unknown file format version
    /// or a header page count larger than the file. Each such issue is reported to
    /// <see cref="Sqlite3Settings.OnValidationIssue"/>. Damage that could yield wrong values still fails.
    /// </summary>
    Lenient
}
