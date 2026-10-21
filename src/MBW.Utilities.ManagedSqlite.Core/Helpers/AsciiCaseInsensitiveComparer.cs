using System;
using System.Collections.Generic;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

/// <summary>
/// Compares names the way SQLite does: case-insensitive for the ASCII letters A-Z only, so "é" and "É" are different
/// names. This is sqlite3StrICmp in util.c (https://sqlite.org/src/file?name=src/util.c), which folds case through
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

    public int GetHashCode(string obj)
    {
        int hash = 17;
        for (int i = 0; i < obj.Length; i++)
            hash = unchecked(hash * 31 + Fold(obj[i]));
        return hash;
    }
}
