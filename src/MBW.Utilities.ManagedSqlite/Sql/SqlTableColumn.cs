using System;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Sql;

/// <summary>
/// A column of a table, as its CREATE TABLE statement declares it
/// </summary>
public sealed class SqlTableColumn
{
    /// <summary>
    /// The column's position in the declared column list, as in <c>SELECT *</c>. This is not the index that
    /// <see cref="Sqlite3Row.TryGetOrdinal(ushort, out object?)"/> and <see cref="Sqlite3Row.ColumnData"/> take, which
    /// is the position in the stored record; read the column with
    /// <see cref="Sqlite3Row.TryGetOrdinal(SqlTableColumn, out object?)"/>.
    /// </summary>
    public int Ordinal { get; }

    /// <summary>
    /// The column's name, as declared, without quotes
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The declared type's words, each unquoted, joined by one space, without a size: "VARCHAR" for VARCHAR(255),
    /// "double precision" as written, and "" for a column with no type
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// The CLR type of the column's affinity, which SQLite derives from the declared type: long for INTEGER, string for
    /// TEXT, byte[] for BLOB (and no type), double for REAL, and long for NUMERIC. SQLite stores any type in any column,
    /// so a value can have another type, and a NUMERIC column can hold reals or text. See the README for the full
    /// mapping.
    /// </summary>
    public Type DetectedType { get; }

    /// <summary>
    /// True when the column is part of the table's PRIMARY KEY, declared on the column or in a table constraint
    /// </summary>
    public bool IsPartOfPrimaryKey { get; internal set; }

    /// <summary>
    /// True for a VIRTUAL generated column (<c>GENERATED ALWAYS AS (...) VIRTUAL</c>, or <c>AS (...)</c> without
    /// STORED). SQLite computes its value when it is read and does not store it, so it has no value here.
    /// </summary>
    public bool IsVirtualGenerated { get; internal set; }

    /// <summary>
    /// The column's position in the stored record, <see cref="Sqlite3Row.ColumnData"/>.
    /// In a rowid table it is <see cref="Ordinal"/>, minus the VIRTUAL generated columns before it. A WITHOUT ROWID
    /// table's record holds the primary key columns first, in key order, then the other stored columns in declared order.
    /// </summary>
    internal int RecordIndex { get; set; }

    /// <summary>
    /// The collation from the column's COLLATE constraint, or null for the default, BINARY
    /// </summary>
    internal string? Collation { get; set; }

    /// <summary>
    /// The value a record too short to hold this column reads as: the DEFAULT, with the column's affinity, or null
    /// </summary>
    internal object? DefaultValue { get; set; }

    /// <summary>
    /// False when <see cref="DefaultValue"/> is not known, for a DEFAULT this library does not evaluate
    /// </summary>
    internal bool HasKnownDefault { get; set; } = true;

    internal SqlTableColumn(int ordinal, string name, string typeName, Type detectedType)
    {
        Ordinal = ordinal;
        Name = name;
        TypeName = typeName;
        DetectedType = detectedType;
        RecordIndex = ordinal;
    }
}