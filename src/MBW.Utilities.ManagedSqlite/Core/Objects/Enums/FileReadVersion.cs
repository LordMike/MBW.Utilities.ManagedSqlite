namespace MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

internal enum FileReadVersion : byte
{
    Legacy = 1,
    // ReSharper disable once InconsistentNaming
    WAL = 2
}