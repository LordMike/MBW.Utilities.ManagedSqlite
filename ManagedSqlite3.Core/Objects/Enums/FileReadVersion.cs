namespace ManagedSqlite3.Core.Objects.Enums
{
    public enum FileReadVersion : byte
    {
        Legacy = 1,
        // ReSharper disable once InconsistentNaming
        WAL = 2
    }
}