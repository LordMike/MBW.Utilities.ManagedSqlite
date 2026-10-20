using System;

namespace MBW.Utilities.ManagedSqlite.Sql;

public class SqlTableColumn
{
    /// <summary>
    /// The column's position in the declared column list, as in <c>SELECT *</c>
    /// </summary>
    public int Ordinal { get; }

    /// <summary>
    /// The column's position in the stored record, <see cref="MBW.Utilities.ManagedSqlite.Core.Tables.Sqlite3Row.ColumnData"/>.
    /// It is <see cref="Ordinal"/>, except in a WITHOUT ROWID table, whose record holds the primary key columns first.
    /// </summary>
    internal int RecordOrdinal { get; set; }

    public string Name { get; }
    public string TypeName { get; }
    public Type DetectedType { get; }

    public bool IsPartOfPrimaryKey { get; internal set; }

    public SqlTableColumn(int ordinal, string name, string typeName, Type detectedType)
    {
        Ordinal = ordinal;
        RecordOrdinal = ordinal;
        Name = name;
        TypeName = typeName;
        DetectedType = detectedType;
    }
}