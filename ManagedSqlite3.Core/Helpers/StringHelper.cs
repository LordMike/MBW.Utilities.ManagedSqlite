using System;

namespace ManagedSqlite3.Core.Helpers
{
    internal static class StringHelper
    {
        public static string ToHex(this byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", "");
        }
    }
}