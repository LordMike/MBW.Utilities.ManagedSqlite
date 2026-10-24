using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

/// <summary>
/// A row's record (https://www.sqlite.org/fileformat.html#record_format), decoded lazily as SQLite's OP_Column in
/// vdbe.c decodes it: the header is parsed only up to the field that is asked for, and the consistency checks apply
/// to the part parsed so far. A field after the last one asked for is never looked at, so damage there, or surplus
/// fields a table does not have columns for, do not fail a read (REC-10). A payload that overflows its page is read
/// the same way: the overflow pages are read only for the header or a value that lies past the cell's local bytes, so
/// damage to the overflow chain fails the read that needs it, not the row.
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

    private static readonly object BoxedZero = 0L;
    private static readonly object BoxedOne = 1L;

    private readonly uint _page;

    /// <summary>
    /// The array that holds the payload's local bytes, at <see cref="_start"/>: the page's, which the page owns, so the
    /// record can keep it. A 0-byte payload is followed by one more byte, the byte after the cell, see
    /// <see cref="Sqlite3Table"/>.
    /// </summary>
    private readonly byte[] _data;

    private readonly int _start;

    /// <summary>
    /// The size of the whole payload, local bytes and overflow
    /// </summary>
    private readonly int _length;

    /// <summary>
    /// The rest of a payload that overflows its page; null when the payload is all in <see cref="_data"/>
    /// </summary>
    private readonly OverflowChain? _overflow;

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

    /// <summary>
    /// The field table that the rows of one enumeration share, see <see cref="Sqlite3Table.EnumerateRows(bool)"/>, and
    /// the number of the row that owns it: the latest one made
    /// </summary>
    internal sealed class SharedFields
    {
        public int[] Fields = Array.Empty<int>();
        public int Row;
    }

    private readonly SharedFields? _shared;

    /// <summary>
    /// This record's number in <see cref="_shared"/>, which owns the shared field table while it is the latest
    /// </summary>
    private readonly int _sharedRow;

    /// <summary>
    /// A record whose field table is <paramref name="shared"/>'s, which this record now owns
    /// </summary>
    public Sqlite3Record(uint page, ArraySegment<byte> payload, SharedFields shared) : this(page, payload)
    {
        _shared = shared;
        _sharedRow = ++shared.Row;
    }

    /// <summary>
    /// A record whose payload is all in <paramref name="payload"/>
    /// </summary>
    public Sqlite3Record(uint page, ArraySegment<byte> payload)
    {
        _page = page;
        _data = payload.Array!;
        _start = payload.Offset;
        _length = payload.Count;
    }

    /// <summary>
    /// A record of <paramref name="length"/> bytes, whose first bytes are <paramref name="local"/> and whose rest is on
    /// the overflow chain that starts at <paramref name="overflowPage"/>
    /// </summary>
    public Sqlite3Record(uint page, ArraySegment<byte> local, int length, uint overflowPage) : this(page, local)
    {
        _length = length;
        _overflow = new OverflowChain(local.Count, overflowPage);
    }

    /// <summary>
    /// A record of <paramref name="length"/> bytes that overflows, as above, whose field table is
    /// <paramref name="shared"/>'s, which this record now owns
    /// </summary>
    public Sqlite3Record(uint page, ArraySegment<byte> local, int length, uint overflowPage, SharedFields shared)
        : this(page, local, length, overflowPage)
    {
        _shared = shared;
        _sharedRow = ++shared.Row;
    }

    /// <summary>
    /// The number of payload bytes in <see cref="_data"/>
    /// </summary>
    private readonly int Local => _overflow?.Local ?? _length;

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
        _overflow?.BeginRead();
        if (!ParseField(index, row))
        {
            value = null;
            return false;
        }

        value = Decode(row, (uint)_fields![2 * index], _fields[2 * index + 1]);
        return true;
    }

    /// <summary>
    /// The bytes of field <paramref name="index"/> when it is a BLOB: a slice of the page's array, without a copy, when
    /// it is in the local bytes, otherwise a new array read from the overflow pages. False when the record has fewer
    /// fields, or the field is not a BLOB. Like <see cref="TryGetValue"/>, a record that is malformed up to that field
    /// throws <see cref="Exceptions.SqliteCorruptException"/>.
    /// </summary>
    public bool TryGetBlob(int index, Sqlite3Row row, out ReadOnlyMemory<byte> blob)
    {
        _overflow?.BeginRead();
        if (ParseField(index, row))
        {
            uint serialType = (uint)_fields![2 * index];
            if (serialType >= 12 && (serialType & 0x01) == 0x00)
            {
                int offset = _fields[2 * index + 1];
                int size = (int)((serialType - 12) / 2);
                blob = offset + size <= Local
                    ? new ReadOnlyMemory<byte>(_data, _start + offset, size)
                    : ReadPayload(offset, size, row);
                return true;
            }
        }

        blob = default;
        return false;
    }

    /// <summary>
    /// The value of field <paramref name="index"/> when it is stored as an INTEGER (serial types 1 to 6, 8 and 9),
    /// without boxing it. False when the record has fewer fields, or the field holds another type.
    /// </summary>
    public bool TryGetInt64(int index, Sqlite3Row row, out long value)
    {
        _overflow?.BeginRead();
        if (ParseField(index, row))
        {
            uint serialType = (uint)_fields![2 * index];
            if (serialType is >= 1 and <= 6 or 8 or 9)
            {
                value = DecodeInteger(serialType, _fields[2 * index + 1], row);
                return true;
            }
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// The value of field <paramref name="index"/> when it is stored as a REAL (serial type 7) or an INTEGER, without
    /// boxing it. False when the record has fewer fields, or the field holds another type, or a NaN, which SQLite reads
    /// as NULL (see <see cref="Decode"/>).
    /// </summary>
    public bool TryGetDouble(int index, Sqlite3Row row, out double value)
    {
        _overflow?.BeginRead();
        if (ParseField(index, row))
        {
            uint serialType = (uint)_fields![2 * index];
            if (serialType == 7)
            {
                value = DecodeReal(_fields[2 * index + 1], row);
                if (!double.IsNaN(value))
                    return true;
            }

            // SQLite writes a REAL without a fractional part, in a column of REAL affinity, as an INTEGER, so a REAL
            // column reads integers too: https://www.sqlite.org/datatype3.html#type_affinity
            if (serialType is >= 1 and <= 6 or 8 or 9)
            {
                value = DecodeInteger(serialType, _fields[2 * index + 1], row);
                return true;
            }
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// The value of field <paramref name="index"/> when it is stored as TEXT (an odd serial type of 13 or more),
    /// decoded in the database's encoding. False when the record has fewer fields, or the field holds another type.
    /// </summary>
    public bool TryGetString(int index, Sqlite3Row row, [NotNullWhen(true)] out string? value)
    {
        _overflow?.BeginRead();
        if (ParseField(index, row))
        {
            uint serialType = (uint)_fields![2 * index];
            if (serialType >= 13 && (serialType & 0x01) == 0x01)
            {
                // Text that reaches past the local bytes is read from the overflow pages, see Decode
                int offset = _fields[2 * index + 1];
                int size = (int)((serialType - 13) / 2);
                value = offset + size > Local
                    ? (string)Decode(row, serialType, offset)!
                    : row.Table.Reader.DecodeString(_data, _start + offset, size);
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// True when fields <paramref name="a"/> and <paramref name="b"/> both exist and are stored alike: the same serial
    /// type and the same bytes, so they decode to equal values. False, without reading them, when either is past the
    /// local bytes.
    /// </summary>
    public bool FieldsEqual(int a, int b, Sqlite3Row row)
    {
        _overflow?.BeginRead();
        if (!ParseField(Math.Max(a, b), row))
            return false;

        int typeA = _fields![2 * a];
        if (typeA != _fields[2 * b])
            return false;

        int size = (int)SerialTypeSize((uint)typeA);
        if (Math.Max(_fields[2 * a + 1], _fields[2 * b + 1]) + size > Local)
            return false;

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
        _overflow?.BeginRead();
        Parse(int.MaxValue, false, row);

        object?[] values = new object?[_parsed];
        if (_fields != null)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = Decode(row, (uint)_fields[2 * i], _fields[2 * i + 1]);

            return values;
        }

        // The checks in Parse passed, so every serial type and value is inside the payload
        byte[] header = HeaderBytes(out int pos, out int end);
        TryReadVarInt32(header, end, ref pos, out _);

        int offset = _headerSize;
        for (int i = 0; i < values.Length; i++)
        {
            TryReadVarInt32(header, end, ref pos, out long serialType);
            values[i] = Decode(row, serialType, offset);
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
        // A later row of the enumeration owns the shared field table now, and may have written over this row's fields
        if (_shared != null && _shared.Row != _sharedRow)
            throw new InvalidOperationException("This row came from EnumerateRows(shareRowBuffers: true), and the enumeration has moved past it. Read its values inside the loop, or read ColumnData there to keep them.");

        if (_parsed > p2)
            return;

        if (_parsed < 0)
        {
            // The header size is a varint at the start of the payload, read when a value is first read, as OP_Column
            // reads it. A 0-byte payload has none, and SQLite reads the byte after the cell instead
            // (aOffset[0] = pC->aRow[0]), which the caller put in the payload's one byte.
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3048 (OP_Column)
            // The varint is in the local bytes: a payload that overflows keeps at least SQLite's minLocal bytes, over 9.
            int pos = _start;
            bool read = TryReadVarInt32(_data, _start + Math.Max(Local, 1), ref pos, out long headerSize);

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

            // A header that runs past the local bytes is read from the overflow pages, as OP_Column reads it when the
            // local bytes (szRow) do not hold it all:
            //   if( pC->szRow<aOffset[0] ){      /*OPTIMIZATION-IF-FALSE*/
            //     ...
            //     pC->aRow = 0;
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3055 (OP_Column)
            // A header in the local bytes is parsed from them, and one read from the overflow pages is read to its end
            // and no further: a serial type that runs past the header is corrupt (zHdr>zEndHdr) whatever follows it.
            if (_overflow != null && _overflow.Header == null && headerSize > _overflow.Local)
            {
                _overflow.Header = new byte[Math.Min(_length, headerSize)];
                CopyPayload(0, _overflow.Header, row);
            }

            // Each serial type takes at least one byte of the header, after the header size itself
            // Sized once for the whole record: each serial type takes at least one byte of the header, after the header
            // size itself, so the parse never names more fields than this. The first parse reads one serial type even
            // when there is no room for one, so the table holds at least one.
            if (keep && headerSize > 0)
            {
                int size = (int)(2 * Math.Max(1, headerSize - _headerPos));
                if (_shared == null)
                    _fields = new int[size];
                else
                {
                    // The table only grows. It needs no clearing: a field is read only after the parse wrote it.
                    if (_shared.Fields.Length < size)
                        _shared.Fields = new int[size];
                    _fields = _shared.Fields;
                }
            }

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
        byte[] header = HeaderBytes(out int headerStart, out int headerEnd);
        int headerPos = _headerPos;
        long offset = _nextOffset;
        bool overread = false;
        do
        {
            // A serial type that runs past the payload runs past the header too, which the checks below reject
            int pos = headerStart + headerPos;
            bool read = TryReadVarInt32(header, headerEnd, ref pos, out long serialType);
            headerPos = pos - headerStart;
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
    /// The array that holds the header, from <paramref name="start"/> (the header size varint) to <paramref name="end"/>:
    /// the local bytes, or the copy <see cref="Parse"/> read from the overflow pages
    /// </summary>
    private readonly byte[] HeaderBytes(out int start, out int end)
    {
        byte[]? header = _overflow?.Header;
        if (header != null)
        {
            start = 0;
            end = header.Length;
            return header;
        }

        start = _start;
        end = _start + Local;
        return _data;
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
    private readonly object? Decode(Sqlite3Row row, long serialType, int offset)
    {
        ReaderBase reader = row.Table.Reader;
        int pos = _start + offset;

        // A value past the local bytes is read from the overflow pages, as OP_Column reads it when the local bytes do
        // not hold it ("if( pC->szRow>=aOffset[p2+1] ){ ... }else{ ... sqlite3VdbeMemFromBtree(...)"), and only then.
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3192 (OP_Column)
        // A BLOB is read straight into its array, and a TEXT into a pooled buffer that is decoded; a number goes
        // through the chain's few bytes of scratch, see NumberBytes.
        int size = (int)SerialTypeSize(serialType);
        if (serialType >= 12 && offset + size > Local)
        {
            if (serialType >= 12 && (serialType & 0x01) == 0x00)
                return ReadPayload(offset, size, row);

            if (serialType >= 12)
            {
                byte[] text = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    CopyPayload(offset, new Span<byte>(text, 0, size), row);
                    return reader.DecodeString(text, 0, size);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(text);
                }
            }
        }

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
            case 5:
            case 6:
                return DecodeInteger((uint)serialType, offset, row);
            case 7:
            {
                // Serial type 7 is a big-endian IEEE 754 double (https://www.sqlite.org/fileformat.html#record_format).
                // SQLite never stores a NaN: sqlite3VdbeMemSetDouble in vdbemem.c turns one into NULL before it is
                // written, so binding, computing or casting a NaN all store NULL. When it reads a record, serialGet in
                // vdbeaux.c does the same for a NaN that another writer or damage put in the file
                // ("pMem->flags = IsNaN(x) ? MEM_Null : MEM_Real"), so a NaN reads as NULL here too.
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbeaux.c#L4071 (serialGet, serial type 7)
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L1092 (sqlite3VdbeMemSetDouble)
                double real = DecodeReal(offset, row);
                return double.IsNaN(real) ? null : real;
            }
            case 8:
                // Serial types 8 and 9: integer constants 0 and 1, no payload bytes. SQLite (file format 4) stores
                // every 0 and 1 this way, so they come from shared boxes instead of a new box for each value
                return BoxedZero;
            case 9:
                return BoxedOne;
            default:
            {
                // The checks in Parse keep the value inside the payload, and the code above inside the local bytes
                if ((serialType & 0x01) == 0x00)
                {
                    // Even number
                    byte[] blob = new byte[size];
                    Buffer.BlockCopy(_data, pos, blob, 0, size);
                    return blob;
                }

                // Odd number
                return reader.DecodeString(_data, pos, size);
            }
        }
    }

    /// <summary>
    /// <paramref name="size"/> bytes of the payload from <paramref name="offset"/>, in an array of their own
    /// </summary>
    private readonly byte[] ReadPayload(int offset, int size, Sqlite3Row row)
    {
        byte[] bytes = new byte[size];
        CopyPayload(offset, bytes, row);
        return bytes;
    }

    /// <summary>
    /// Copies the payload's bytes from <paramref name="offset"/> into <paramref name="destination"/>: first those in
    /// the local bytes, then those on the overflow pages, as SQLite's accessPayload in btree.c reads them
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5125 (accessPayload)
    /// </summary>
    private readonly void CopyPayload(int offset, Span<byte> destination, Sqlite3Row row)
    {
        OverflowChain overflow = _overflow!;
        if (offset < overflow.Local)
        {
            int count = Math.Min(destination.Length, overflow.Local - offset);
            new ReadOnlySpan<byte>(_data, _start + offset, count).CopyTo(destination);
            destination = destination.Slice(count);
            offset += count;
        }

        // Overflow pages begin with a 4 byte big-endian pointer to the next page of the chain (0 on the last), and the
        // payload continues right after it (https://www.sqlite.org/fileformat.html#ovflpgs)
        ReaderBase reader = row.Table.Reader;
        int pageData = (int)(reader.PageSize - reader.ReservedSpace) - sizeof(uint);
        int index = (offset - overflow.Local) / pageData;
        int skip = (offset - overflow.Local) % pageData;
        while (destination.Length > 0)
        {
            byte[] data = overflow.ReadPage(index, row, this);
            int count = Math.Min(destination.Length, pageData - skip);
            new ReadOnlySpan<byte>(data, sizeof(uint) + skip, count).CopyTo(destination);
            destination = destination.Slice(count);
            index++;
            skip = 0;
        }
    }

    /// <summary>
    /// The INTEGER of serial type 1 to 6, 8 or 9 that starts at <paramref name="offset"/> in the payload
    /// </summary>
    private readonly long DecodeInteger(uint serialType, int offset, Sqlite3Row row)
    {
        // Serial types 8 and 9: integer constants 0 and 1, no payload bytes
        if (serialType is 8 or 9)
            return serialType - 8;

        int size = (int)SerialTypeSize(serialType);
        byte[] data = NumberBytes(offset, size, row, out int pos, out int end);
        return ByteArrayReader.ReadInteger(data, end, ref pos, size);
    }

    /// <summary>
    /// The overflow pages of a payload that does not fit on its page, found as they are needed, as SQLite's
    /// accessPayload in btree.c keeps them in its <c>aOverflow</c> cache
    /// </summary>
    private sealed class OverflowChain
    {
        /// <summary>
        /// The number of payload bytes on the b-tree page
        /// </summary>
        public readonly int Local;

        private readonly uint _first;

        /// <summary>
        /// Page i of the chain at [i], for the first <see cref="_known"/>; allocated by the first read. A 0 is a
        /// chain that ended there, which fails only a read that needs that page.
        /// </summary>
        private uint[]? _pages;

        private int _known;

        /// <summary>
        /// The last page read, and its index in the chain. It is in the reader's page buffer, so it is valid only until
        /// the next page read, and is forgotten by <see cref="BeginRead"/>.
        /// </summary>
        private int _readIndex = -1;

        private byte[]? _readData;

        /// <summary>
        /// The header, with up to 8 bytes after it, when it runs past the local bytes
        /// </summary>
        public byte[]? Header;

        /// <summary>
        /// A number that runs past the local bytes is copied here to be decoded
        /// </summary>
        public byte[]? Scratch;

        public OverflowChain(int local, uint first)
        {
            Local = local;
            _first = first;
        }

        /// <summary>
        /// Called as a value is read: pages may have been read into the reader's page buffer since the last one
        /// </summary>
        public void BeginRead() => _readIndex = -1;

        /// <summary>
        /// Page <paramref name="index"/> of the chain, read into the reader's page buffer, following the next pointers
        /// of the pages before it that are not yet known
        /// </summary>
        public byte[] ReadPage(int index, Sqlite3Row row, in Sqlite3Record record)
        {
            ReaderBase reader = row.Table.Reader;
            int pageData = (int)(reader.PageSize - reader.ReservedSpace) - sizeof(uint);
            if (_pages == null)
            {
                _pages = new uint[(record._length - Local + pageData - 1) / pageData];
                _pages[0] = _first;
                _known = 1;
            }

            while (_known <= index)
                Read(_known - 1, row, record, pageData);

            return Read(index, row, record, pageData);
        }

        private byte[] Read(int index, Sqlite3Row row, in Sqlite3Record record, int pageData)
        {
            if (index == _readIndex)
                return _readData!;

            uint page = _pages![index];

            // CELL-13: the overflow chain ended before the payload did. accessPayload in btree.c:
            //   if( rc==SQLITE_OK && amt>0 ){
            //     /* Overflow chain ends prematurely */
            //     return SQLITE_CORRUPT_PAGE(pPage);
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5321 (accessPayload)
            // SQLite reads the chain only as far as the value it reads, so a chain that ends early fails only the reads
            // past its end.
            Checks.Strict(page != 0, "CELL-13",
                $"the overflow chain of {RowName(row)} ended with {record._length - Local - index * pageData} bytes of the payload left",
                index == 0 ? record._page : _pages[index - 1], "overflow chain");

            // Each page is read through the reader, so a page the WAL or a journal holds is read from there
            byte[] data = row.Table.Reader.ReadPage(page);
            _readIndex = index;
            _readData = data;

            // CELL-16: the next pointer is followed only while payload remains, so the last overflow page's pointer is
            // never read, whatever it holds. accessPayload in btree.c stops as soon as the payload is copied:
            //   nextPage = get4byte(aPayload);
            //   ...
            //   amt -= a;
            //   if( amt==0 ) return rc;
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5306 (accessPayload)
            if (index + 1 == _known && _known < _pages.Length)
                _pages[_known++] = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);

            return data;
        }
    }

    /// <summary>
    /// The REAL of serial type 7 that starts at <paramref name="offset"/>, a big-endian IEEE 754 double
    /// (https://www.sqlite.org/fileformat.html#record_format)
    /// </summary>
    private readonly double DecodeReal(int offset, Sqlite3Row row)
    {
        byte[] data = NumberBytes(offset, 8, row, out int pos, out int end);
        return BitConverter.Int64BitsToDouble(ByteArrayReader.ReadInteger(data, end, ref pos, 8));
    }

    /// <summary>
    /// Where the <paramref name="size"/> bytes of a number at <paramref name="offset"/> are: in the local bytes, or,
    /// when the number reaches past them, copied from the overflow pages into the chain's scratch
    /// </summary>
    private readonly byte[] NumberBytes(int offset, int size, Sqlite3Row row, out int pos, out int end)
    {
        if (offset + size <= Local)
        {
            pos = _start + offset;
            end = _start + Local;
            return _data;
        }

        byte[] scratch = _overflow!.Scratch ??= new byte[8];
        CopyPayload(offset, new Span<byte>(scratch, 0, size), row);
        pos = 0;
        end = size;
        return scratch;
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
