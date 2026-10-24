using System.IO;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

internal static class StreamHelper
{
    public static int ReadFully(this Stream stream, byte[] buffer, int offset, int length)
    {
        int totalRead = 0;
        int numRead = stream.Read(buffer, offset, length);
        while (numRead > 0)
        {
            totalRead += numRead;
            if (totalRead == length)
            {
                break;
            }

            numRead = stream.Read(buffer, offset + totalRead, length - totalRead);
        }

        return totalRead;
    }
}