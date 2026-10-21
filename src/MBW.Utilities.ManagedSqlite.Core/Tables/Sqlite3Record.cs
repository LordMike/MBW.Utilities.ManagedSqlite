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
internal sealed class Sqlite3Record
{
    /// <summary>
    /// The largest header SQLite reads: a 3-byte serial type for each of at most 32768 columns, plus 3 bytes for the
    /// header size itself
    /// </summary>
    private const long MaxHeaderSize = 98307;

    private readonly ReaderBase _reader;
    private readonly uint _page;
    private readonly long? _rowId;

    /// <summary>
    /// The payload. For a 0-byte payload it holds one byte, the byte after the cell, see <see cref="Sqlite3Table"/>.
    /// </summary>
    private readonly byte[] _payload;

    private readonly int _length;
    private readonly long _headerSize;

    /// <summary>
    /// Where the next serial type starts, as SQLite's <c>iHdrOffset</c>
    /// </summary>
    private int _headerPos;

    /// <summary>
    /// Serial types of the fields parsed so far, and the offsets of their values: field i's value starts at
    /// <c>_offsets[i]</c>, and <c>_offsets[0]</c> is the header size (SQLite's <c>aType</c> and <c>aOffset</c>)
    /// </summary>
    private long[]? _types;
    private long[]? _offsets;

    /// <summary>
    /// Fields parsed so far (SQLite's <c>nHdrParsed</c>), -1 before the first parse
    /// </summary>
    private int _parsed = -1;

    public Sqlite3Record(ReaderBase reader, uint page, long? rowId, byte[] payload, int length)
    {
        _reader = reader;
        _page = page;
        _rowId = rowId;
        _payload = payload;
        _length = length;

        // The header size is a varint at the start of the payload. A 0-byte payload has none, and SQLite reads the byte
        // after the cell instead (aOffset[0] = pC->aRow[0]), which the caller put in the payload's one byte.
        int pos = 0;
        bool read = TryReadVarInt32(payload, Math.Max(length, 1), ref pos, out _headerSize);
        _headerPos = pos;

        // REC-03, CELL-21: a header larger than the payload, or larger than any record SQLite writes, is corrupt.
        // SQLite tests this only when the header is not in the cell's local bytes, but a header that is local is no larger
        // than they are, so the test holds for every record. OP_Column in vdbe.c:
        //   if( aOffset[0] > 98307 || aOffset[0] > pC->payloadSize ){
        //     goto op_column_corrupt;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3072 (OP_Column)
        // A header size that runs past the payload is larger than the payload.
        Checks.Strict(read && _headerSize <= MaxHeaderSize && _headerSize <= length, "REC-03",
            $"record header size {_headerSize} of {RowName} is outside the payload of {length} bytes", page, "record header");
    }

    private string RowName => _rowId.HasValue ? $"row {_rowId.Value}" : "a WITHOUT ROWID row";

    /// <summary>
    /// The number of fields, which parses the whole header
    /// </summary>
    public int FieldCount
    {
        get
        {
            Parse(int.MaxValue);
            return _parsed;
        }
    }

    /// <summary>
    /// The value of field <paramref name="index"/>, parsing the header up to it. False when the record has fewer
    /// fields, which SQLite reads as the column's default or NULL.
    /// </summary>
    public bool TryGetValue(int index, out object? value)
    {
        Parse(index);

        if (index >= _parsed)
        {
            value = null;
            return false;
        }

        value = Decode(_types![index], (int)_offsets![index]);
        return true;
    }

    /// <summary>
    /// Parses the header up to and including field <paramref name="p2"/>, or to its end. This is SQLite's
    /// op_column_read_header loop, with its checks on the part parsed so far.
    /// </summary>
    private void Parse(int p2)
    {
        if (_parsed > p2)
            return;

        if (_parsed < 0)
        {
            _parsed = 0;
            _offsets = new long[Math.Min(16, _headerSize + 1)];
            _types = new long[_offsets.Length];
            _offsets[0] = _headerSize;

            // REC-04, CELL-22: a header size of 0 is a record of no fields, so every column reads as its default or NULL.
            // SQLite parses one serial type past the (empty) header, finds the record corrupt, and then forgives it:
            //   if( aOffset[0]==0 ){
            //     i = 0;
            //     zHdr = zEndHdr;
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L3145 (OP_Column)
            if (_headerSize == 0)
            {
                _headerPos = 0;
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
        int i = _parsed;
        long offset = _offsets![i];
        bool overread = false;
        do
        {
            if (i + 1 == _offsets.Length)
            {
                Array.Resize(ref _offsets, _offsets.Length * 2);
                Array.Resize(ref _types, _offsets.Length);
            }

            // A serial type that runs past the payload runs past the header too, which the checks below reject
            if (!TryReadVarInt32(_payload, _length, ref _headerPos, out long serialType))
            {
                overread = true;
                break;
            }

            _types![i] = serialType;
            offset += SerialTypeSize(serialType);
            _offsets[++i] = offset;
        } while (i <= p2 && _headerPos < _headerSize);

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
        bool corrupt = overread || (_headerPos >= _headerSize && (_headerPos > _headerSize || offset != _length)) || offset > _length;
        Checks.Strict(!corrupt, "CELL-23",
            $"record of {RowName} has {(overread ? "a serial type past its payload" : $"a {_headerPos} byte header (declared {_headerSize}) and values up to byte {offset}")}, but the payload is {_length} bytes",
            _page, "record header");

        _parsed = i;
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

    private object? Decode(long serialType, int pos)
    {
        byte[] payload = _payload;
        int length = _length;

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
                return _reader.DecodeString(payload, pos, size);
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
