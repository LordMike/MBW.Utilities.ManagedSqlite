using System;

namespace MBW.Utilities.ManagedSqlite.Sql
{
    public class SqlTableColumn
    {
        public int Ordinal { get; }
        public string Name { get; }
        public string TypeName { get; }
        public Type DetectedType { get; }

        public bool IsPartOfPrimaryKey { get; internal set; }

        public SqlTableColumn(int ordinal, string name, string typeName, Type detectedType)
        {
            Ordinal = ordinal;
            Name = name;
            TypeName = typeName;
            DetectedType = detectedType;
        }
    }
}