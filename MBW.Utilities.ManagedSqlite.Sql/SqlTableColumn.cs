using System;

namespace MBW.Utilities.ManagedSqlite.Sql
{
    public class SqlTableColumn
    {
        public string Name { get; }
        public string TypeName { get; }
        public Type DetectedType { get; }

        public SqlTableColumn(string name, string typeName, Type detectedType)
        {
            Name = name;
            TypeName = typeName;
            DetectedType = detectedType;
        }
    }
}