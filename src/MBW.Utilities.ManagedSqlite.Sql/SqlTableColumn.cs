using System;

namespace MBW.Utilities.ManagedSqlite.Sql;

public sealed class SqlTableColumn
{
    /// <summary>
    /// The column's position in the declared column list, as in <c>SELECT *</c>
    /// </summary>
    public int Ordinal { get; }

    public string Name { get; }
    /// <summary>
    /// The declared type's words, each unquoted, joined by one space, without a size: "VARCHAR" for VARCHAR(255),
    /// "double precision" as written, and "" for a column with no type
    /// </summary>
    public string TypeName { get; }
    public Type DetectedType { get; }

    public bool IsPartOfPrimaryKey { get; internal set; }

    /// <summary>
    /// True for a VIRTUAL generated column (<c>GENERATED ALWAYS AS (...) VIRTUAL</c>, or <c>AS (...)</c> without
    /// STORED). SQLite computes its value when it is read and does not store it, so it has no value here.
    /// </summary>
    public bool IsVirtualGenerated { get; internal set; }

    /// <summary>
    /// The column's position in the stored record, <see cref="MBW.Utilities.ManagedSqlite.Core.Tables.Sqlite3Row.ColumnData"/>.
    /// In a rowid table it is <see cref="Ordinal"/>, minus the VIRTUAL generated columns before it. A WITHOUT ROWID
    /// table's record holds the primary key columns first, in key order, then the other stored columns in declared order.
    /// </summary>
    internal int RecordIndex { get; set; }

    /// <summary>
    /// The collation from the column's COLLATE constraint, or null for the default, BINARY
    /// </summary>
    internal string? Collation { get; set; }

    internal SqlTableColumn(int ordinal, string name, string typeName, Type detectedType)
    {
        Ordinal = ordinal;
        Name = name;
        TypeName = typeName;
        DetectedType = detectedType;
        RecordIndex = ordinal;
    }
}