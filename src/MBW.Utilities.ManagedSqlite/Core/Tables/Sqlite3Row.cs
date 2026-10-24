using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Sql;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

/// <summary>
/// A row of a table, from <see cref="Sqlite3Table.EnumerateRows()"/>. Values are read by their position in the stored
/// record with <see cref="TryGetOrdinal(ushort, out object?)"/> and <see cref="ColumnData"/>, or by column name with
/// <see cref="SqlExtensions.TryGetValueByName(Sqlite3Row, string, out object?)"/>.
/// </summary>
public sealed class Sqlite3Row
{
    /// <summary>
    /// The table the row was read from
    /// </summary>
    public Sqlite3Table Table { get; }

    /// <summary>
    /// The row's rowid. A row of a WITHOUT ROWID table has none, and reads 0 here.
    /// </summary>
    public long RowId { get; }

    /// <summary>
    /// Not readonly: reading a value moves the record's parse state on, in place. Cleared once
    /// <see cref="ColumnData"/> holds every value, which lets go of the page.
    /// </summary>
    private Sqlite3Record _record;

    private object?[]? _columnData;

    /// <summary>
    /// The values of the row's record, in the order they are stored. For a rowid table that is the declared column
    /// order. For a WITHOUT ROWID table it is the primary key columns first, in primary key order, then the other
    /// columns in declared order; <c>TryGetValueByName</c> resolves names in both cases.
    ///
    /// This decodes the whole record, so damage anywhere in it throws. <see cref="TryGetOrdinal(ushort, out object?)"/>
    /// decodes only up to the field asked for, as SQLite does. Once this is read, <c>TryGetOrdinal</c> returns these
    /// same values.
    ///
    /// The row reads its record from its b-tree page's bytes, so a row that is kept keeps its page in memory until
    /// this is read. Then the row has every value, and lets go of the page.
    ///
    /// The list is the row's own, which its other accessors read from, so it is read-only.
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed</exception>
    public IReadOnlyList<object?> ColumnData
    {
        get
        {
            if (_columnData == null)
            {
                _columnData = _record.ReadAll(this);

                // Every read now goes through the values, so the record and the page it refers to can be collected
                _record = default;
            }

            return _columnData;
        }
    }

    internal Sqlite3Row(Sqlite3Table table, long rowId, Sqlite3Record record)
    {
        Table = table;
        RowId = rowId;
        _record = record;
    }

    /// <summary>
    /// The value of field <paramref name="index"/> of the record, the position in <see cref="ColumnData"/>. That is not
    /// <see cref="SqlTableColumn.Ordinal"/> in a WITHOUT ROWID table, or after a VIRTUAL generated column; to read a
    /// column, use <see cref="TryGetOrdinal(SqlTableColumn, out object?)"/>. Like SQLite, this decodes the record only
    /// up to that field, so damage after it, or fields past the table's columns, do not fail the read. False when the
    /// record has fewer fields.
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed up to that field</exception>
    public bool TryGetOrdinal(ushort index, out object? value) => TryGetField(index, out value);

    /// <summary>
    /// The value of <paramref name="column"/>, a column of this row's table definition, read as SQLite reads it: a
    /// long, double, string, byte[], or null for NULL. A rowid alias reads <see cref="RowId"/>, a record too short to
    /// hold the column (a row written before ALTER TABLE ADD COLUMN) reads its DEFAULT, and an INTEGER in a column with
    /// REAL affinity reads as a REAL. The column's field is found whatever the table's record order. False for a
    /// VIRTUAL generated column, which SQLite computes and does not store, and for a DEFAULT this library does not
    /// evaluate. See the README for how declared types map to affinities.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null</exception>
    /// <exception cref="ArgumentException"><paramref name="column"/> is not a column of this row's table definition,
    /// from <c>row.Table.GetTableDefinition()</c> (a column read from another database object of the same file is
    /// not)</exception>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed up to that column</exception>
    public bool TryGetOrdinal(SqlTableColumn column, out object? value)
    {
        ArgumentNullException.ThrowIfNull(column);

        SqlTableDefinition tableDefinition = Table.GetTableDefinition();
        if (!tableDefinition.TryGetColumn(column.Name, out SqlTableColumn? own) || own != column)
            throw new ArgumentException($"column '{column.Name}' is not a column of the definition of table '{tableDefinition.TableName}' that this row's database read", nameof(column));

        return TryGetColumn(tableDefinition, column, out value);
    }

    /// <summary>
    /// <see cref="TryGetOrdinal(SqlTableColumn, out object?)"/> for a <paramref name="column"/> of
    /// <paramref name="tableDefinition"/>, this row's table's definition
    /// </summary>
    internal bool TryGetColumn(SqlTableDefinition tableDefinition, SqlTableColumn column, out object? value)
    {
        value = null;

        // SQLite computes a VIRTUAL generated column when it is read, nothing is stored for it
        if (column.IsVirtualGenerated)
            return false;

        // A rowid alias column reads the rowid, which SQLite takes from the cell, not the record, so it reads even when
        // the record is shorter (REC-21)
        if (column == tableDefinition.RowIdColumn)
        {
            value = RowId;
            return true;
        }

        // Like SQLite, the record is decoded only up to this column. A record too short to hold it, such as one written
        // before ALTER TABLE ADD COLUMN, reads the column's default (REC-11). OP_Column in vdbe.c:
        //       if( pOp->p4type==P4_MEM ){
        //         sqlite3VdbeMemShallowCopy(pDest, pOp->p4.pMem, MEM_Static);
        //       }else{
        //         sqlite3VdbeMemSetNull(pDest);
        //       }
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3165 (OP_Column)
        if (!TryGetField(column.RecordIndex, out value))
        {
            if (!column.HasKnownDefault)
                return false;

            value = column.DefaultValue is byte[] blob ? blob.Clone() : column.DefaultValue;
        }

        // SQLite writes REAL values without a fractional part as integers, and turns them back into REAL when read
        // through a column with REAL affinity
        if (value is long integer && column.DetectedType == typeof(double))
            value = (double)integer;

        return true;
    }

    /// <summary>
    /// True when fields <paramref name="a"/> and <paramref name="b"/> of the record exist and decode to equal values
    /// </summary>
    internal bool FieldsEqual(int a, int b)
    {
        // Without the record, equal values can't be told from equal bytes; false only means "compare the values"
        return _columnData == null && _record.FieldsEqual(a, b, this);
    }

    internal bool TryGetField(int index, out object? value)
    {
        if (_columnData == null)
            return _record.TryGetValue(index, this, out value);

        bool exists = index < _columnData.Length;
        value = exists ? _columnData[index] : null;
        return exists;
    }

    /// <summary>
    /// The bytes of field <paramref name="index"/> of the record when it holds a BLOB, without copying them: the memory
    /// is a slice of the page or payload the row was read from, which it keeps alive. Once <see cref="ColumnData"/> is
    /// read, it is that value's array instead. Like <see cref="TryGetOrdinal(ushort, out object?)"/>, this decodes the
    /// record only up to that field. False when the record has fewer fields, or the field holds another type.
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed up to that field</exception>
    public bool TryGetBlob(ushort index, out ReadOnlyMemory<byte> value)
    {
        if (_columnData == null)
            return _record.TryGetBlob(index, this, out value);

        bool isBlob = index < _columnData.Length && _columnData[index] is byte[];
        value = isBlob ? (byte[])_columnData[index]! : default;
        return isBlob;
    }

    /// <summary>
    /// The value of field <paramref name="index"/> of the record when it is stored as an INTEGER, without boxing it as
    /// <see cref="TryGetOrdinal(ushort, out object?)"/> does. Like that method, this decodes the record only up to
    /// that field. False when the record has fewer fields, or the field holds NULL or another type: nothing is
    /// converted.
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed up to that field</exception>
    public bool TryGetInt64(ushort index, out long value)
    {
        if (_columnData == null)
            return _record.TryGetInt64(index, this, out value);

        bool isInteger = index < _columnData.Length && _columnData[index] is long;
        value = isInteger ? (long)_columnData[index]! : 0;
        return isInteger;
    }

    /// <summary>
    /// The value of field <paramref name="index"/> of the record when it is stored as a REAL or an INTEGER, without
    /// boxing it. An INTEGER is read too because SQLite stores a REAL without a fractional part as an INTEGER in a
    /// column of REAL affinity. Like <see cref="TryGetOrdinal(ushort, out object?)"/>, this decodes the record only up
    /// to that field. False when the record has fewer fields, or the field holds NULL, TEXT or a BLOB. A stored NaN
    /// reads as NULL, as in SQLite.
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed up to that field</exception>
    public bool TryGetDouble(ushort index, out double value)
    {
        if (_columnData == null)
            return _record.TryGetDouble(index, this, out value);

        object? stored = index < _columnData.Length ? _columnData[index] : null;
        value = stored switch { double real => real, long integer => integer, _ => 0 };
        return stored is double or long;
    }

    /// <summary>
    /// The value of field <paramref name="index"/> of the record when it is stored as TEXT, decoded in the database's
    /// encoding. Like <see cref="TryGetOrdinal(ushort, out object?)"/>, this decodes the record only up to that field.
    /// False when the record has fewer fields, or the field holds NULL or another type: nothing is converted.
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed up to that field</exception>
    public bool TryGetString(ushort index, [NotNullWhen(true)] out string? value)
    {
        if (_columnData == null)
            return _record.TryGetString(index, this, out value);

        value = index < _columnData.Length ? _columnData[index] as string : null;
        return value != null;
    }

    /// <summary>
    /// The value of field <paramref name="index"/> of the record, as <typeparamref name="T"/>. False when the record
    /// has fewer fields (as <see cref="TryGetOrdinal(ushort, out object?)"/>), or the value does not convert:
    /// <list type="bullet">
    /// <item>NULL converts to <c>default</c>: null for a reference type or <see cref="Nullable{T}"/>, 0 for a number,
    /// as <c>sqlite3_column_int</c> reads NULL.</item>
    /// <item>A value that already is <typeparamref name="T"/>, or the type a <see cref="Nullable{T}"/> wraps, is
    /// returned as it is.</item>
    /// <item>An INTEGER converts to <see cref="double"/>, <see cref="float"/> or <see cref="decimal"/>, to
    /// <see cref="bool"/> (true when not 0), and to a narrower integer type when it fits.</item>
    /// <item>A REAL converts to an integer type or <see cref="bool"/> as SQLite does, truncated toward zero
    /// (3.5 is 3, -3.5 is -3), when the result fits; to <see cref="float"/> and <see cref="decimal"/> when it fits.
    /// A REAL converts to <see cref="decimal"/> as .NET does, rounded to 15 significant digits.</item>
    /// <item>TEXT and BLOB convert to nothing else, and nothing converts to them: text is not parsed as a number, in
    /// any culture, and numbers are not formatted.</item>
    /// <item>Other types, such as enums, <see cref="char"/> or <see cref="ReadOnlyMemory{T}"/>, are not converted
    /// to; read a blob without a copy with <see cref="TryGetBlob"/>.</item>
    /// </list>
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed up to that field</exception>
    public bool TryGetOrdinal<T>(ushort index, out T? value)
    {
        if (!TryGetOrdinal(index, out object? stored))
        {
            value = default;
            return false;
        }

        return ValueConverter.TryConvert(stored, out value);
    }

    /// <summary>
    /// The value of <paramref name="column"/>, read as <see cref="TryGetOrdinal(SqlTableColumn, out object?)"/> reads
    /// it, as <typeparamref name="T"/>. False when the column has no value, or the value does not convert, with the
    /// conversions of <see cref="TryGetOrdinal{T}(ushort, out T)"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null</exception>
    /// <exception cref="ArgumentException"><paramref name="column"/> is not a column of this row's table definition,
    /// from <c>row.Table.GetTableDefinition()</c> (a column read from another database object of the same file is
    /// not)</exception>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed up to that column</exception>
    public bool TryGetOrdinal<T>(SqlTableColumn column, out T? value)
    {
        if (!TryGetOrdinal(column, out object? stored))
        {
            value = default;
            return false;
        }

        return ValueConverter.TryConvert(stored, out value);
    }
}