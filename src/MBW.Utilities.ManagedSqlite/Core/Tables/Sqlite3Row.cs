using System;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public sealed class Sqlite3Row
{
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
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed</exception>
    public object?[] ColumnData
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
    /// The value of field <paramref name="index"/> of the record. Like SQLite, this decodes the record only up to that
    /// field, so damage after it, or fields past the table's columns, do not fail the read. False when the record has
    /// fewer fields.
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed up to that field</exception>
    public bool TryGetOrdinal(ushort index, out object? value) => TryGetField(index, out value);

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
    /// (3.5 is 3, -3.5 is -3), when the result fits; to <see cref="float"/> and <see cref="decimal"/> when it fits.</item>
    /// <item>TEXT and BLOB convert to nothing else, and nothing converts to them: text is not parsed as a number, in
    /// any culture, and numbers are not formatted.</item>
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
}