namespace MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

internal enum SqliteDataType : byte
{
    Null = 0,
    Integer = 1,
    Float = 7,
    // Serial types 8 and 9: integer constants 0 and 1, no payload bytes
    IntegerZero = 8,
    IntegerOne = 9,
    Blob = 12,
    Text = 13
}