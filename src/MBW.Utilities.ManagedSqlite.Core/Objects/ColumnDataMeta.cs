using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Objects
{
    internal struct ColumnDataMeta
    {
        public uint Length;
        public SqliteDataType Type;

        public override string ToString()
        {
            return $"{Type} / {Length:N0}";
        }
    }
}