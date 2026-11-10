namespace MBW.Utilities.ManagedSqlite.Core;

/// <summary>
/// The file format write and read versions, header bytes 18 and 19
/// (https://www.sqlite.org/fileformat.html#file_format_version_numbers)
/// </summary>
public enum SqliteFileFormat : byte
{
    /// <summary>
    /// The legacy rollback journal format
    /// </summary>
    Legacy = 1,
    /// <summary>
    /// Write-ahead log (WAL) mode
    /// </summary>
    Wal = 2
}
