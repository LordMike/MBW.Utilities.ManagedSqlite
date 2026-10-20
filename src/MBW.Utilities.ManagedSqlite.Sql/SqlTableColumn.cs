using System;

namespace MBW.Utilities.ManagedSqlite.Sql;

public sealed class SqlTableColumn
{
    public int Ordinal { get; }
    public string Name { get; }
    public string TypeName { get; }
    public Type DetectedType { get; }

    public bool IsPartOfPrimaryKey { get; internal set; }

    /// <summary>
    /// True for a VIRTUAL generated column (<c>GENERATED ALWAYS AS (...) VIRTUAL</c>, or <c>AS (...)</c> without
    /// STORED). SQLite computes its value when it is read and does not store it, so it has no value here.
    /// </summary>
    public bool IsVirtualGenerated { get; internal set; }

    /// <summary>
    /// The column's position in the stored record: its ordinal, minus the VIRTUAL generated columns before it
    /// </summary>
    internal int RecordIndex { get; set; }

    internal SqlTableColumn(int ordinal, string name, string typeName, Type detectedType)
    {
        Ordinal = ordinal;
        Name = name;
        TypeName = typeName;
        DetectedType = detectedType;
        RecordIndex = ordinal;
    }
}