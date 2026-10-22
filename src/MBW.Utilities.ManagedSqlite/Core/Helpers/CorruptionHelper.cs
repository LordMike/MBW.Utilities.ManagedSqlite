using System;
using System.IO;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

internal static class CorruptionHelper
{
    /// <summary>
    /// True for the exceptions that parsing malformed bytes produces (reading past the end, values out of range,
    /// casts of mistyped values). These are converted into <see cref="Exceptions.SqliteCorruptException"/> at the
    /// public entry points. <see cref="ArgumentNullException"/> is a caller bug and is never converted.
    /// </summary>
    public static bool IsCorruptionShaped(Exception ex)
    {
        if (ex is ArgumentNullException)
            return false;

        return ex is ArgumentException ||
               ex is EndOfStreamException ||
               ex is InvalidCastException ||
               ex is FormatException ||
               ex is OverflowException ||
               ex is IndexOutOfRangeException ||
               ex is DivideByZeroException;
    }
}
