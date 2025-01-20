using System;

namespace MBW.Utilities.ManagedSqlite.Core.Internal.Helpers;

internal static class StringHelper
{
    public static string ToHex(this byte[] data)
    {
        return BitConverter.ToString(data).Replace("-", "");
    }
}