using System.Runtime.CompilerServices;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

/// <summary>
/// The consistency checks the reader makes on the database file, each named by its ID in the
/// <see href="https://github.com/LordMike/MBW.Utilities.ManagedSqlite/blob/master/docs/sqlite-checks/README.md">SQLite check survey</see>, such as "PAGE-21".
/// Every check fails where SQLite fails; see <see cref="SqliteValidation"/>.
/// </summary>
internal static class Checks
{
    /// <summary>
    /// A check that holds in every mode: the reader cannot go on past this damage. Throws
    /// <see cref="SqliteCorruptException"/> when <paramref name="ok"/> is false.
    /// </summary>
    public static void Strict(bool ok, string check, [InterpolatedStringHandlerArgument("ok")] ref CheckMessage message,
        uint? page = null, string? structure = null)
    {
        if (!ok)
            Fail(check, message.ToString(), page, structure);
    }

    /// <summary>
    /// A check that SQLite makes, but past which the data is still intact. In <see cref="SqliteValidation.Strict"/> it
    /// throws like <see cref="Strict"/>. In <see cref="SqliteValidation.Lenient"/> it reports the issue to
    /// <see cref="Sqlite3Settings.OnValidationIssue"/> and returns false, so the caller can take its fallback.
    /// </summary>
    public static bool Relaxable(bool ok, Sqlite3Settings settings, string check,
        [InterpolatedStringHandlerArgument("ok")] ref CheckMessage message, uint? page = null, string? structure = null)
    {
        if (ok)
            return true;

        string text = message.ToString();

        if (settings.Validation == SqliteValidation.Strict)
            Fail(check, text, page, structure);

        settings.OnValidationIssue?.Invoke(new SqliteValidationIssue(check, text, page, structure));
        return false;
    }

    /// <summary>
    /// Throws the <see cref="SqliteCorruptException"/> of a failed check. For call sites that need the condition in an
    /// <c>if</c>, e.g. to stop a loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Fail(string check, string message, uint? page = null, string? structure = null)
    {
        throw new SqliteCorruptException(message, page, structure, check: check);
    }
}
