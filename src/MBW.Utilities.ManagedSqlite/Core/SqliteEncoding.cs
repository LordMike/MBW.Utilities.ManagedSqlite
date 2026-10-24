namespace MBW.Utilities.ManagedSqlite.Core;

/// <summary>
/// A text encoding of a database, with the values SQLite stores in the header's text encoding field (offset 56)
/// </summary>
public enum SqliteEncoding : uint
{
    /// <summary>
    /// UTF-8
    /// </summary>
    UTF8 = 1,
    /// <summary>
    /// UTF-16, little-endian
    /// </summary>
    UTF16LE = 2,
    /// <summary>
    /// UTF-16, big-endian
    /// </summary>
    UTF16BE = 3
}