using System;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

/// <summary>
/// A row's record (https://www.sqlite.org/fileformat.html#record_format), decoded lazily as SQLite's OP_Column in
/// vdbe.c decodes it: the header is parsed only up to the field that is asked for, and the consistency checks apply
/// to the part parsed so far. A field after the last one asked for is never looked at, so damage there, or surplus
/// fields a table does not have columns for, do not fail a read (REC-10).
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L2975 (OP_Column)
/// </summary>
/// <remarks>
/// A mutable struct, so a row holds its record without a second object. It is kept in a field of
/// <see cref="Sqlite3Row"/> and never copied after it is created.
/// </remarks>
internal struct Sqlite3Record
{
    /// <summary>
    /// The largest header SQLite reads: a 3-byte serial type for each of at most 32768 columns, plus 3 bytes for the
    /// header size itself
    /// </summary>
    private const long MaxHeaderSize = 98307;

    private readonly uint _page;

    /// <summary>
    /// The array that holds the payload, at <see cref="_start"/>: the page's for a payload that is all on its page,
    /// otherwise one of its own. A 0-byte payload is followed by one more byte, the byte after the cell, see
    /// <see cref="Sqlite3Table"/>.
    /// </summary>
    private readonly byte[] _data;

    private readonly int _start;

    private readonly int _length;

    /// <summary>
    /// The header size (SQLite's <c>aOffset[0]</c>), read by the first <see cref="Parse"/>
    /// </summary>
    private int _headerSize;

    /// <summary>
    /// Where the next serial type starts, as SQLite's <c>iHdrOffset</c>
    /// </summary>
    private int _headerPos;

    /// <summary>
    /// Field i's serial type at <c>[2 * i]</c>, as a uint, and the offset of its value at <c>[2 * i + 1]</c> (SQLite's
    /// <c>aType</c> and <c>aOffset</c>), for the fields parsed so far. Allocated by the first
    /// <see cref="TryGetValue"/>; <see cref="ReadAll"/> does without, so a row whose values are all read at once, or
    /// never read, costs nothing here.
    /// </summary>
    private int[]? _fields;

    /// <summary>
    /// Where the value after the last parsed field starts (SQLite's <c>aOffset[nHdrParsed]</c>)
    /// </summary>
    private int _nextOffset;

    /// <summary>
    /// Fields parsed so far (SQLite's <c>nHdrParsed</c>), -1 before the first parse
    /// </summary>
    private int _parsed = -1;

    public Sqlite3Record(uint page, ArraySegment<byte> payload)
    {
        _page = page;
        _data = payload.Array!;
        _start = payload.Offset;
        _length = payload.Count;
    }

    /// <summary>
    /// The row in error messages. The record's methods take the row that holds it, for its rowid and its table's reader,
    /// so the record need not keep them.
    /// </summary>
    private static string RowName(Sqlite3Row row) => row.Table.IsWithoutRowId ? "a WITHOUT ROWID row" : $"row {row.RowId}";

    /// <summary>
    /// The value of field <paramref name="index"/>, parsing the header up to it. False when the record has fewer
    /// fields, which SQLite reads as the column's default or NULL.
    /// </summary>
    public bool TryGetValue(int index, Sqlite3Row row, out object? value)
    {
        if (!ParseField(index, row))
        {
            value = null;
            return false;
        }

        value = Decode(row.Table.Reader, (uint)_fields![2 * index], _fields[2 * index + 1]);
        return true;
    }

    /// <summary>
    /// The bytes of field <paramref name="index"/> when it is a BLOB, as a slice of the array the record is read from,
    /// without a copy. False when the record has fewer fields, or the field is not a BLOB. Like
    /// <see cref="TryGetValue"/>, a record that is malformed up to that field throws <see cref="Exceptions.SqliteCorruptException"/>.
    /// </summary>
    public bool TryGetBlob(int index, Sqlite3Row row, out ReadOnlyMemory<byte> blob)
    {
        if (ParseField(index, row))
        {
            uint serialType = (uint)_fields![2 * index];
            if (serialType >= 12 && (serialType & 0x01) == 0x00)
            {
                blob = new ReadOnlyMemory<byte>(_data, _start + _fields[2 * index + 1], (int)((serialType - 12) / 2));
                return true;
            }
        }

        blob = default;
        return false;
    }

    /// <summary>
    /// True when fields <paramref name="a"/> and <paramref name="b"/> both exist and are stored alike: the same serial
    /// type and the same bytes, so they decode to equal values
    /// </summary>
    public bool FieldsEqual(int a, int b, Sqlite3Row row)
    {
        if (!ParseField(Math.Max(a, b), row))
            return false;

        int typeA = _fields![2 * a];
        if (typeA != _fields[2 * b])
            return false;

        int size = (int)SerialTypeSize((uint)typeA);
        return new ReadOnlySpan<byte>(_data, _start + _fields[2 * a + 1], size)
            .SequenceEqual(new ReadOnlySpan<byte>(_data, _start + _fields[2 * b + 1], size));
    }

    /// <summary>
    /// Parses the header up to field <paramref name="index"/>, keeping the fields. False when the record has fewer
    /// fields.
    /// </summary>
    private bool ParseField(int index, Sqlite3Row row)
    {
        // ReadAll parsed the whole header without keeping the fields, so parse it again, keeping them
        if (_fields == null && _parsed > 0)
            _parsed = -1;

        Parse(index, true, row);
        return index < _parsed;
    }

    /// <summary>
    /// The values of all fields. Like a read of the last field, this parses the whole header, and its checks fail
    /// before any value is decoded. The values are then decoded in one more walk over the header, so the fields are
    /// not kept.
    /// </summary>
    public object?[] ReadAll(Sqlite3Row row)
    {
        Parse(int.MaxValue, false, row);
        ReaderBase reader = row.Table.Reader;

        object?[] values = new object?[_parsed];
        if (_fields != null)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = Decode(reader, (uint)_fields[2 * i], _fields[2 * i + 1]);

            return values;
        }

        // The checks in Parse passed, so every serial type and value is inside the payload
        int end = _start + _length;
        int pos = _start;
        TryReadVarInt32(_data, end, ref pos, out _);

        int offset = _headerSize;
        for (int i = 0; i < values.Length; i++)
        {
            TryReadVarInt32(_data, end, ref pos, out long serialType);
            values[i] = Decode(reader, serialType, offset);
            offset += (int)SerialTypeSize(serialType);
        }

        return values;
    }

    /// <summary>
    /// Parses the header up to and including field <paramref name="p2"/>, or to its end. This is SQLite's
    /// op_column_read_header loop, with its checks on the part parsed so far. Like SQLite, the state moves on only when
    /// the checks pass, so a record that failed fails again on every read. <paramref name="keep"/> keeps the fields in
    /// <see cref="_fields"/>, which is then allocated by the first parse.
    /// </summary>
    private void Parse(int p2, bool keep, Sqlite3Row row)
    {
        if (_parsed > p2)
            return;

        if (_parsed < 0)
        {
            // The header size is a varint at the start of the payload, read when a value is first read, as OP_Column
            // reads it. A 0-byte payload has none, and SQLite reads the byte after the cell instead
            // (aOffset[0] = pC->aRow[0]), which the caller put in the payload's one byte.
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3048 (OP_Column)
            int pos = _start;
            bool read = TryReadVarInt32(_data, _start + Math.Max(_length, 1), ref pos, out long headerSize);

            // REC-03, CELL-21: a header larger than the payload, or larger than any record SQLite writes, is corrupt.
            // SQLite tests this only when the header is not in the cell's local bytes, but a header that is local is no
            // larger than they are, so the test holds for every record. OP_Column in vdbe.c:
            //   if( aOffset[0] > 98307 || aOffset[0] > pC->payloadSize ){
            //     goto op_column_corrupt;
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3072 (OP_Column)
            // A header size varint that runs past the payload is taken as larger than it. SQLite reads on into the bytes
            // after the cell there, which only matters for a varint that still decodes to 0.
            Checks.Strict(read && headerSize <= MaxHeaderSize && headerSize <= _length, "REC-03",
                $"record header size {(read ? headerSize.ToString() : "varint")} of {RowName(row)} runs past the payload of {_length} bytes", _page, "record header");

            _headerSize = (int)headerSize;
            _headerPos = pos - _start;
            _nextOffset = (int)headerSize;
            // Each serial type takes at least one byte of the header, after the header size itself
            if (keep && headerSize > 0)
                _fields = new int[2 * Math.Min(8, Math.Max(1, headerSize - _headerPos))];

            // REC-04, CELL-22: a header size of 0 is a record of no fields, so every column reads as its default or NULL.
            // SQLite parses one serial type past the (empty) header, finds the record corrupt, and then forgives it:
            //   if( aOffset[0]==0 ){
            //     i = 0;
            //     zHdr = zEndHdr;
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3145 (OP_Column)
            if (headerSize == 0)
            {
                _headerPos = 0;
                _parsed = 0;
                return;
            }
        }
        else if (_headerPos >= _headerSize)
        {
            // The whole header is parsed: "if( pC->iHdrOffset<aOffset[0] ){ ... }else{ t = 0; }"
            return;
        }

        // The first parse reads at least one serial type, even when the header size leaves no room for one (a header
        // size of 1), which then fails the checks below (REC-05)
        int i = Math.Max(_parsed, 0);
        int headerPos = _headerPos;
        long offset = _nextOffset;
        bool overread = false;
        do
        {
            // Grow to at least double, and straight to field p2 when that is further, so reading a late column of a wide
            // table makes one larger array instead of a row of discarded doubled ones. Neither goes past the fields the
            // rest of the header can hold, as each serial type takes at least one byte.
            if (_fields != null && 2 * i + 2 > _fields.Length)
            {
                long most = i + Math.Max(1, _headerSize - headerPos);
                long fields = Math.Min(Math.Max(_fields.Length, (long)p2 + 1), most);
                Array.Resize(ref _fields, 2 * (int)Math.Max(fields, i + 1));
            }

            // A serial type that runs past the payload runs past the header too, which the checks below reject
            int pos = _start + headerPos;
            bool read = TryReadVarInt32(_data, _start + _length, ref pos, out long serialType);
            headerPos = pos - _start;
            if (!read)
            {
                overread = true;
                break;
            }

            // The offsets of a header that fails the checks below may not fit; they are never read
            if (_fields != null)
            {
                _fields[2 * i] = unchecked((int)serialType);
                _fields[2 * i + 1] = unchecked((int)offset);
            }

            offset += SerialTypeSize(serialType);
            i++;
        } while (i <= p2 && headerPos < _headerSize);

        // REC-05, CELL-23: the record is corrupt if (1) the serial types run past the declared header size, (2) the whole
        // header is parsed but the values do not fill the payload exactly, or (3) the values run past the payload.
        // OP_Column in vdbe.c:
        //   if( (zHdr>=zEndHdr && (zHdr>zEndHdr || offset64!=pC->payloadSize))
        //    || (offset64 > pC->payloadSize)
        //   ){
        //     if( aOffset[0]==0 ){
        //       ...
        //     }else{
        //       ...
        //       goto op_column_corrupt;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3142 (OP_Column)
        bool corrupt = overread || (headerPos >= _headerSize && (headerPos > _headerSize || offset != _length)) || offset > _length;
        Checks.Strict(!corrupt, "CELL-23",
            $"record of {RowName(row)} has {(overread ? "a serial type past its payload" : $"a {headerPos} byte header (declared {_headerSize}) and values up to byte {offset}")}, but the payload is {_length} bytes",
            _page, "record header");

        _parsed = i;
        _headerPos = headerPos;
        _nextOffset = (int)offset;
    }

    /// <summary>
    /// Reads a varint as SQLite's sqlite3GetVarint32 in util.c does: a value above 32 bits reads as 0xffffffff. False
    /// when it runs past <paramref name="length"/>.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L1794 (sqlite3GetVarint32)
    /// </summary>
    private static bool TryReadVarInt32(byte[] data, int length, ref int pos, out long value)
    {
        ulong res = 0;
        for (int i = 0; i < 9; i++)
        {
            if (pos >= length)
            {
                value = 0;
                return false;
            }

            byte b = data[pos++];
            if (i == 8)
            {
                res = (res << 8) | b;
                break;
            }

            res = (res << 7) | (byte)(b & 0x7F);
            if ((b & 0x80) == 0)
                break;
        }

        value = (long)Math.Min(res, uint.MaxValue);
        return true;
    }

    /// <summary>
    /// Decodes the value of <paramref name="serialType"/> that starts at <paramref name="offset"/> in the payload
    /// </summary>
    private readonly object? Decode(ReaderBase reader, long serialType, int offset)
    {
        byte[] payload = _data;
        int length = _start + _length;
        int pos = _start + offset;

        switch (serialType)
        {
            case 0:
            case 10:
            case 11:
                // 10 and 11 are reserved. SQLite reads them as NULL with no payload bytes, and so does this.
                return null;
            case 1:
            case 2:
            case 3:
            case 4:
                return ByteArrayReader.ReadInteger(payload, length, ref pos, (int)serialType);
            case 5:
                return ByteArrayReader.ReadInteger(payload, length, ref pos, 6);
            case 6:
                return ByteArrayReader.ReadInteger(payload, length, ref pos, 8);
            case 7:
            {
                // Serial type 7 is a big-endian IEEE 754 double (https://www.sqlite.org/fileformat.html#record_format).
                // SQLite never stores a NaN: sqlite3VdbeMemSetDouble in vdbemem.c turns one into NULL before it is
                // written, so binding, computing or casting a NaN all store NULL. When it reads a record, serialGet in
                // vdbeaux.c does the same for a NaN that another writer or damage put in the file
                // ("pMem->flags = IsNaN(x) ? MEM_Null : MEM_Real"), so a NaN reads as NULL here too.
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbeaux.c#L4071 (serialGet, serial type 7)
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L1092 (sqlite3VdbeMemSetDouble)
                double real = BitConverter.Int64BitsToDouble(ByteArrayReader.ReadInteger(payload, length, ref pos, 8));
                return double.IsNaN(real) ? null : real;
            }
            case 8:
                // Serial types 8 and 9: integer constants 0 and 1, no payload bytes
                return 0L;
            case 9:
                return 1L;
            default:
            {
                // The checks in Parse keep the value inside the payload
                int size = (int)((serialType - 12) / 2);

                if ((serialType & 0x01) == 0x00)
                {
                    // Even number
                    byte[] blob = new byte[size];
                    Buffer.BlockCopy(payload, pos, blob, 0, size);
                    return blob;
                }

                // Odd number
                return reader.DecodeString(payload, pos, size);
            }
        }
    }

    private static long SerialTypeSize(long serialType)
    {
        switch (serialType)
        {
            case 0:
            case 8:
            case 9:
            case 10:
            case 11:
                return 0;
            case 1:
            case 2:
            case 3:
            case 4:
                return serialType;
            case 5:
                return 6;
            case 6:
            case 7:
                return 8;
            default:
                return (serialType - 12) / 2;
        }
    }
}
