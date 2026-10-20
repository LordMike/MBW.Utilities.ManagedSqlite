using System;

namespace MBW.Utilities.ManagedSqlite.Sql;

public sealed class SqlTableColumn
{
    public int Ordinal { get; }
    public string Name { get; }
    public string TypeName { get; }
    public Type DetectedType { get; }

    public bool IsPartOfPrimaryKey { get; internal set; }

    internal SqlTableColumn(int ordinal, string name, string typeName, Type detectedType)
    {
        Ordinal = ordinal;
        Name = name;
        TypeName = typeName;
        DetectedType = detectedType;
    }
}