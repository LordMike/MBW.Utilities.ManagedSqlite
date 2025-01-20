using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Internal.Objects;

internal struct ColumnDataMeta
{
    public uint Length;
    public SqliteDataType Type;

    public override string ToString() => $"{Type} / {Length:N0}";
}