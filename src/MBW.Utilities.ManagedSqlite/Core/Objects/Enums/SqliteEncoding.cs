namespace MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

/// <summary>
/// A text encoding of a database, with the values SQLite stores in the header's text encoding field (offset 56)
/// </summary>
public enum SqliteEncoding : uint
{
    /// <summary>
    /// UTF-8
    /// </summary>
    // ReSharper disable once InconsistentNaming
    UTF8 = 1,
    /// <summary>
    /// UTF-16, little-endian
    /// </summary>
    // ReSharper disable once InconsistentNaming
    UTF16LE = 2,
    /// <summary>
    /// UTF-16, big-endian
    /// </summary>
    // ReSharper disable once InconsistentNaming
    UTF16BE = 3
}