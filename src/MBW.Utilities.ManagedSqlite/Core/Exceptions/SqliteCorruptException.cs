using System;

namespace MBW.Utilities.ManagedSqlite.Core.Exceptions;

/// <summary>
/// The database file is malformed: a structure in it is inconsistent with the SQLite file format.
/// </summary>
public sealed class SqliteCorruptException : SqliteException
{
    /// <summary>
    /// The page the problem was found on, when known.
    /// </summary>
    public uint? Page { get; }

    /// <summary>
    /// A short description of the malformed structure, such as "b-tree page header" or "record header", when known.
    /// </summary>
    public string? Structure { get; }

    /// <summary>
    /// The ID of the consistency check that failed, such as "PAGE-21", from the SQLite check survey of issue #58, when
    /// known.
    /// </summary>
    public string? Check { get; }

    /// <summary>
    /// The check's own message, without the prefix and the structure and page that <see cref="Exception.Message"/>
    /// adds, as a <see cref="SqliteValidationIssue"/> carries it
    /// </summary>
    internal string Detail { get; }

    internal SqliteCorruptException(string message, uint? page = null, string? structure = null, Exception? innerException = null, string? check = null)
        : base(FormatMessage(message, page, structure), innerException)
    {
        Page = page;
        Structure = structure;
        Check = check;
        Detail = message;
    }

    private static string FormatMessage(string message, uint? page, string? structure)
    {
        string res = "The database is corrupt: " + message;

        if (structure != null && page.HasValue)
            res += $" ({structure}, page {page.Value})";
        else if (structure != null)
            res += $" ({structure})";
        else if (page.HasValue)
            res += $" (page {page.Value})";

        return res;
    }
}
