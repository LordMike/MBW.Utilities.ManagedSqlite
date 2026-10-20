namespace MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

internal enum FileWriteVersion : byte
{
    Legacy = 1,
    // ReSharper disable once InconsistentNaming
    WAL = 2
}