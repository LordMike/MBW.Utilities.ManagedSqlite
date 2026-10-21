using System;

namespace MBW.Utilities.ManagedSqlite.Core.Internal;

/// <summary>
/// The bytes of the database file as the pager sees them: the main file, or the main file with a hot rollback journal
/// played back over it. Pages are cut from it at the database's page size.
/// </summary>
internal interface IFileView : IDisposable
{
    /// <summary>
    /// The length of the file in bytes
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes from <paramref name="position"/>, and returns how many it read. It
    /// reads fewer only at the end of the file. Errors of the underlying stream, such as a failed seek, are thrown.
    /// </summary>
    int Read(long position, byte[] buffer, int offset, int count);
}
