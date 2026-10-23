namespace MBW.Utilities.ManagedSqlite.Core;

/// <summary>
/// A consistency check that failed while reading in <see cref="SqliteValidation.Lenient"/> mode, where SQLite would
/// have reported the database as corrupt.
/// </summary>
public sealed class SqliteValidationIssue
{
    /// <summary>
    /// The ID of the check, such as "HDR-14", from the SQLite check survey of issue #58
    /// </summary>
    public string Check { get; }

    public string Message { get; }

    /// <summary>
    /// The page the problem was found on, when known
    /// </summary>
    public uint? Page { get; }

    /// <summary>
    /// A short description of the malformed structure, such as "database header", when known
    /// </summary>
    public string? Structure { get; }

    internal SqliteValidationIssue(string check, string message, uint? page, string? structure)
    {
        Check = check;
        Message = message;
        Page = page;
        Structure = structure;
    }

    public override string ToString() => $"{Check}: {Message}";
}
