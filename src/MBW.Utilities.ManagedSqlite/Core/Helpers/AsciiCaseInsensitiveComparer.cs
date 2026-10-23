using System;
using System.Collections.Generic;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

/// <summary>
/// Compares names the way SQLite does: case-insensitive for the ASCII letters A-Z only, so "é" and "É" are different
/// names. This is sqlite3StrICmp in util.c (https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L416), which folds case through
/// sqlite3UpperToLower, a table that maps only ASCII letters.
/// </summary>
internal sealed class AsciiCaseInsensitiveComparer : IEqualityComparer<string>
{
    public static AsciiCaseInsensitiveComparer Instance { get; } = new AsciiCaseInsensitiveComparer();

    private static char Fold(char c) => c >= 'A' && c <= 'Z' ? (char)(c + ('a' - 'A')) : c;

    public bool Equals(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x == null || y == null || x.Length != y.Length)
            return false;

        for (int i = 0; i < x.Length; i++)
        {
            if (Fold(x[i]) != Fold(y[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// <see cref="Equals(string?, string?)"/> of the <paramref name="length"/> characters of <paramref name="x"/> at
    /// <paramref name="start"/> and <paramref name="y"/>, without cutting them out
    /// </summary>
    public static bool Equals(string x, int start, int length, string y)
    {
        if (length != y.Length)
            return false;

        for (int i = 0; i < length; i++)
        {
            if (Fold(x[start + i]) != Fold(y[i]))
                return false;
        }

        return true;
    }

    public int GetHashCode(string obj)
    {
        int hash = 17;
        for (int i = 0; i < obj.Length; i++)
            hash = unchecked(hash * 31 + Fold(obj[i]));
        return hash;
    }
}
