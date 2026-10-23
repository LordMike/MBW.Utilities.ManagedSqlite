using System;
using System.Diagnostics.CodeAnalysis;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public sealed class Sqlite3Row
{
    public Sqlite3Table Table { get; }

    /// <summary>
    /// The row's rowid. A row of a WITHOUT ROWID table has none, and reads 0 here.
    /// </summary>
    public long RowId { get; }

    private readonly Sqlite3Record _record;
    private object?[]? _columnData;

    /// <summary>
    /// The values of the row's record, in the order they are stored. For a rowid table that is the declared column
    /// order. For a WITHOUT ROWID table it is the primary key columns first, in primary key order, then the other
    /// columns in declared order; <c>TryGetValueByName</c> resolves names in both cases.
    ///
    /// This decodes the whole record, so damage anywhere in it throws. <see cref="TryGetOrdinal(ushort, out object?)"/>
    /// decodes only up to the field asked for, as SQLite does.
    /// </summary>
    /// <exception cref="Exceptions.SqliteCorruptException">The record is malformed</exception>
    public object?[] ColumnData
    {
        get
        {
            if (_columnData == null)
            {
                object?[] data = new object?[_record.FieldCount];
                for (int i = 0; i < data.Length; i++)
                    _record.TryGetValue(i, out data[i]);

                _columnData = data;
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
    public bool TryGetOrdinal(ushort index, out object? value) => _record.TryGetValue(index, out value);

    internal bool TryGetField(int index, out object? value) => _record.TryGetValue(index, out value);

    public bool TryGetOrdinal<T>(ushort index, [MaybeNullWhen(false)] out T value)
    {
        value = default;

        if (!TryGetOrdinal(index, out object? tmp))
            return false;

        // TODO: Is null a success case?
        if (tmp == null)
            return false;

        value = (T)Convert.ChangeType(tmp, typeof(T));
        return true;
    }
}