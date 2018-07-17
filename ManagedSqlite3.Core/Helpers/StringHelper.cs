using System;

namespace Sqlite3RoLib.Helpers
{
    internal static class StringHelper
    {
        public static string ToHex(this byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", "");
        }
    }
}