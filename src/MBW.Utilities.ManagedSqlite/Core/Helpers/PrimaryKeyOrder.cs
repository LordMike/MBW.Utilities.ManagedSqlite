using System;
using System.IO;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

/// <summary>
/// INTEG-33: checks that the rows of a WITHOUT ROWID table come in strictly increasing PRIMARY KEY order, as SQLite's
/// integrity_check does. Each row's record is compared with the key of the row before it, then becomes the key the next
/// row is compared with. This is a port of the comparison SQLite makes there, OP_IdxGT on the table's own cursor, see
/// <see cref="IsInOrder"/>.
/// </summary>
internal sealed class PrimaryKeyOrder
{
    private enum Collation : byte
    {
        Binary,
        NoCase,
        RTrim
    }

    private enum Kind : byte
    {
        Null,
        Integer,
        Real,
        Text,
        Blob
    }

    /// <summary>
    /// A key field of the row before, as SQLite holds it in a register (a Mem). Text and blobs point into
    /// <see cref="_previous"/>.
    /// </summary>
    private struct Field
    {
        public Kind Kind;
        public long Integer;
        public double Real;
        public int Offset;
        public int Length;
    }

    private readonly Collation[] _collations;
    private readonly bool[] _descending;
    private readonly bool[] _realAffinity;
    private readonly Encoding? _utf16;
    private readonly Field[] _key;
    private ArraySegment<byte> _previous;
    private bool _hasKey;

    private PrimaryKeyOrder(Collation[] collations, bool[] descending, bool[] realAffinity, Encoding? utf16)
    {
        _collations = collations;
        _descending = descending;
        _realAffinity = realAffinity;
        _utf16 = utf16;
        _key = new Field[collations.Length];
    }

    /// <summary>
    /// The check for a WITHOUT ROWID table, or null when its key uses a collation other than SQLite's built-in BINARY,
    /// NOCASE and RTRIM. SQLite can't open such a table without the application's collation; this library reads it
    /// regardless, but can't tell its order.
    /// </summary>
    public static PrimaryKeyOrder? Create(ParsedTable table, SqliteEncoding encoding)
    {
        ParsedIndex? pk = table.PrimaryKeyIndex;
        if (pk == null || pk.Columns.Count == 0)
            return null;

        // The key's KeyInfo, sqlite3KeyInfoOfIndex in build.c: a collation and sort order per key column, which is
        // the column's own collation unless the PRIMARY KEY names one
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L5653 (sqlite3KeyInfoOfIndex)
        int count = pk.Columns.Count;
        Collation[] collations = new Collation[count];
        bool[] descending = new bool[count];
        bool[] realAffinity = new bool[count];

        for (int i = 0; i < count; i++)
        {
            // Collation names are matched ignoring case, as sqlite3FindCollSeq does. The three built-in collations are
            // registered in openDatabase in main.c.
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/main.c#L3551 (openDatabase)
            string name = pk.Collations[i];
            if (AsciiCaseInsensitiveComparer.Instance.Equals(name, "BINARY"))
                collations[i] = Collation.Binary;
            else if (AsciiCaseInsensitiveComparer.Instance.Equals(name, "NOCASE"))
                collations[i] = Collation.NoCase;
            else if (AsciiCaseInsensitiveComparer.Instance.Equals(name, "RTRIM"))
                collations[i] = Collation.RTrim;
            else
                return null;

            descending[i] = pk.SortOrders[i] == 1;
            realAffinity[i] = table.Columns[pk.Columns[i]].Affinity == ParsedColumn.AFF_REAL;
        }

        Encoding? utf16 = encoding switch
        {
            SqliteEncoding.UTF16LE => Encoding.Unicode,
            SqliteEncoding.UTF16BE => Encoding.BigEndianUnicode,
            _ => null
        };

        return new PrimaryKeyOrder(collations, descending, realAffinity, utf16);
    }

    /// <summary>
    /// True when <paramref name="record"/>, the record of the next row, sorts after the row before it. The record then
    /// becomes the key the next row is compared with. integrity_check in pragma.c:
    ///   a1 = sqlite3VdbeAddOp4Int(v, OP_IdxGT, iDataCur, 0,r2,pPk->nKeyCol);
    ///   sqlite3VdbeAddOp1(v, OP_IsNull, r2);
    ///   zErr = sqlite3MPrintf(db, "row not in PRIMARY KEY order for %s", pTab->zName);
    ///   ...
    ///   for(j=0; j&lt;pPk->nKeyCol; j++){
    ///     sqlite3ExprCodeLoadIndexColumn(pParse, pPk, iDataCur, j, r2+j);
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pragma.c#L1887 (sqlite3Pragma, integrity_check)
    /// The registers start out NULL, so the first row, and any row after one whose first key field is NULL, is in order.
    /// </summary>
    public bool IsInOrder(ArraySegment<byte> record)
    {
        bool inOrder = !_hasKey || _key[0].Kind == Kind.Null || Compare(record) >= 0;

        _previous = record;
        _hasKey = LoadKey(record);

        return inOrder;
    }

    /// <summary>
    /// Reads the key fields of <paramref name="record"/> into <see cref="_key"/>, as the loop above loads them into its
    /// registers with OP_Column. That reads serial types 10 and 11 and a NaN as NULL (sqlite3VdbeSerialGet and
    /// serialGet7 in vdbeaux.c), and sqlite3ColumnDefault in update.c adds OP_RealAffinity for a column of REAL
    /// affinity, which turns an INTEGER into a REAL.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbeaux.c#L4123 (sqlite3VdbeSerialGet)
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/update.c#L80 (sqlite3ColumnDefault)
    /// A field the record lacks is NULL; OP_Column would read the column's DEFAULT, but a key field is never missing
    /// from a record that isn't damaged. False when the record is malformed: the next row is then not compared. Its
    /// own read reports the damage.
    /// </summary>
    private bool LoadKey(ArraySegment<byte> record)
    {
        byte[] data = record.Array!;
        int end = record.Offset + record.Count;

        try
        {
            int pos = record.Offset;
            long headerSize = ReadVarInt(data, end, ref pos);
            if (headerSize > record.Count)
                return false;

            int headerEnd = record.Offset + (int)headerSize;
            int body = headerEnd;

            for (int i = 0; i < _key.Length; i++)
            {
                ref Field field = ref _key[i];
                field = default;

                if (pos >= headerEnd)
                    continue;

                long serialType = ReadVarInt(data, headerEnd, ref pos);
                long length = SerialTypeLength(serialType);
                if (length > end - body)
                    return false;

                int at = body;
                switch (serialType)
                {
                    case >= 1 and <= 6:
                        field.Kind = Kind.Integer;
                        field.Integer = ByteArrayReader.ReadInteger(data, end, ref at, (int)length);
                        break;
                    case 7:
                        double real = BitConverter.Int64BitsToDouble(ByteArrayReader.ReadInteger(data, end, ref at, 8));
                        if (!double.IsNaN(real))
                        {
                            field.Kind = Kind.Real;
                            field.Real = real;
                        }

                        break;
                    case 8 or 9:
                        field.Kind = Kind.Integer;
                        field.Integer = serialType - 8;
                        break;
                    case >= 12:
                        field.Kind = (serialType & 1) == 1 ? Kind.Text : Kind.Blob;
                        field.Offset = body;
                        field.Length = (int)length;
                        break;
                }

                if (field.Kind == Kind.Integer && _realAffinity[i])
                {
                    field.Kind = Kind.Real;
                    field.Real = field.Integer;
                }

                body += (int)length;
            }

            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>
    /// sqlite3VdbeRecordCompareWithSkip in vdbeaux.c, with default_rc = -1 as OP_IdxGT sets it: negative when
    /// <paramref name="record"/> sorts before or equal to the key, positive when after, and 0 when the record is
    /// malformed. OP_IdxGT then adds 1 and takes the record to be in order when the result is above 0, so equal keys are
    /// out of order, and so is nothing when the record is malformed: OP_IdxGT does not look at the errCode.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L6828 (OP_IdxGT)
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbeaux.c#L4733 (sqlite3VdbeRecordCompareWithSkip)
    /// SQLite reads a record's bytes past its end from the page's padding; here a record that runs short is malformed.
    /// </summary>
    private int Compare(ArraySegment<byte> record)
    {
        byte[] data = record.Array!;
        int end = record.Offset + record.Count;

        try
        {
            int idx = record.Offset;
            long headerSize = ReadVarInt(data, end, ref idx);
            if (headerSize > record.Count)
                return 0;

            int headerEnd = record.Offset + (int)headerSize;
            int d = headerEnd;

            for (int i = 0; ; i++)
            {
                if (idx >= headerEnd)
                    return 0;

                long serialType = ReadVarInt(data, headerEnd, ref idx);
                int rc = CompareField(data, d, end, serialType, i);

                if (rc != 0)
                    return _descending[i] ? -rc : rc;

                // All the fields so far are equal
                if (i + 1 == _key.Length)
                    return -1;

                d += (int)SerialTypeLength(serialType);
                if (d > end)
                    return -1;
            }
        }
        catch (EndOfStreamException)
        {
            return 0;
        }
    }

    /// <summary>
    /// One field of the loop in sqlite3VdbeRecordCompareWithSkip: the record's field at <paramref name="d"/>, of
    /// <paramref name="serialType"/>, against key field <paramref name="i"/>. NULLs sort first, then numbers, text and
    /// blobs. A malformed field throws <see cref="EndOfStreamException"/>.
    /// </summary>
    private int CompareField(byte[] data, int d, int end, long serialType, int i)
    {
        ref readonly Field rhs = ref _key[i];
        int pos = d;

        switch (rhs.Kind)
        {
            case Kind.Integer:
                if (serialType >= 10)
                    return serialType == 10 ? -1 : +1;
                if (serialType == 0)
                    return -1;
                if (serialType == 7)
                    return -IntFloatCompare(rhs.Integer, ReadReal(data, end, ref pos));

                long lhs = ReadInteger(data, end, ref pos, serialType);
                return lhs < rhs.Integer ? -1 : lhs > rhs.Integer ? +1 : 0;

            case Kind.Real:
                if (serialType >= 10)
                    return serialType == 10 ? -1 : +1;
                if (serialType == 0)
                    return -1;
                if (serialType == 7)
                {
                    double real = ReadReal(data, end, ref pos);
                    if (double.IsNaN(real))
                        return -1;

                    return real < rhs.Real ? -1 : real > rhs.Real ? +1 : 0;
                }

                return IntFloatCompare(ReadInteger(data, end, ref pos, serialType), rhs.Real);

            case Kind.Text:
            {
                if (serialType < 12)
                    return -1;
                if ((serialType & 1) == 0)
                    return +1;

                long length = (serialType - 12) / 2;
                if (length > end - d)
                    throw new EndOfStreamException();

                return CompareText(data, d, (int)length, _previous.Array!, rhs.Offset, rhs.Length, _collations[i]);
            }

            case Kind.Blob:
            {
                if (serialType < 12 || (serialType & 1) == 1)
                    return -1;

                long length = (serialType - 12) / 2;
                if (length > end - d)
                    throw new EndOfStreamException();

                return CompareBinary(data, d, (int)length, _previous.Array!, rhs.Offset, rhs.Length);
            }

            default:
                // A NULL key field equals a NULL, a serial type 10, and a NaN; anything else sorts after it
                if (serialType == 0 || serialType == 10)
                    return 0;
                if (serialType == 7)
                    return double.IsNaN(ReadReal(data, end, ref pos)) ? 0 : 1;

                return 1;
        }
    }

    /// <summary>
    /// Text compared with its collation. BINARY compares the bytes as stored, in any encoding, as
    /// sqlite3KeyInfoOfIndex leaves it out (aColl[i] is 0) and the record compare uses memcmp. NOCASE and RTRIM are
    /// registered for UTF-8 only, so vdbeCompareMemString converts UTF-16 text to UTF-8 for them.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbeaux.c#L4476 (vdbeCompareMemString)
    /// Text that is not valid UTF-16 converts as .NET converts it, which can differ from SQLite's own translation.
    /// </summary>
    private int CompareText(byte[] a, int aOffset, int aLength, byte[] b, int bOffset, int bLength, Collation collation)
    {
        if (collation == Collation.Binary)
            return CompareBinary(a, aOffset, aLength, b, bOffset, bLength);

        if (_utf16 != null)
        {
            a = Encoding.UTF8.GetBytes(_utf16.GetString(a, aOffset, aLength));
            b = Encoding.UTF8.GetBytes(_utf16.GetString(b, bOffset, bLength));
            aOffset = bOffset = 0;
            aLength = a.Length;
            bLength = b.Length;
        }

        if (collation == Collation.RTrim)
        {
            // rtrimCollFunc in main.c: trailing spaces are dropped, then the text compares as BINARY
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/main.c#L1067 (rtrimCollFunc)
            while (aLength > 0 && a[aOffset + aLength - 1] == ' ')
                aLength--;
            while (bLength > 0 && b[bOffset + bLength - 1] == ' ')
                bLength--;

            return CompareBinary(a, aOffset, aLength, b, bOffset, bLength);
        }

        // nocaseCollatingFunc in main.c, through sqlite3_strnicmp in util.c: ASCII letters fold to lower case, up to the
        // shorter length or a 0 byte, then the shorter text sorts first
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/main.c#L1096 (nocaseCollatingFunc)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L435 (sqlite3_strnicmp)
        int count = Math.Min(aLength, bLength);
        for (int k = 0; k < count; k++)
        {
            int x = FoldAscii(a[aOffset + k]);
            int diff = x - FoldAscii(b[bOffset + k]);
            if (diff != 0)
                return diff;
            if (x == 0)
                break;
        }

        return aLength - bLength;
    }

    private static int FoldAscii(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? c + 32 : c;

    /// <summary>
    /// memcmp over the shorter length, then the shorter sorts first, as the record compare and binCollFunc do
    /// </summary>
    private static int CompareBinary(byte[] a, int aOffset, int aLength, byte[] b, int bOffset, int bLength) =>
        new ReadOnlySpan<byte>(a, aOffset, aLength).SequenceCompareTo(new ReadOnlySpan<byte>(b, bOffset, bLength));

    /// <summary>
    /// sqlite3IntFloatCompare in vdbeaux.c: negative, zero or positive as <paramref name="i"/> is less than, equal to or
    /// greater than <paramref name="r"/>, exactly, with a NaN taken as NULL
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbeaux.c#L4551 (sqlite3IntFloatCompare)
    /// </summary>
    private static int IntFloatCompare(long i, double r)
    {
        // SQLite considers NaN to be a NULL, and all integers are greater than NULL
        if (double.IsNaN(r))
            return 1;
        if (r < -9223372036854775808.0)
            return +1;
        if (r >= 9223372036854775808.0)
            return -1;

        long y = (long)r;
        if (i < y)
            return -1;
        if (i > y)
            return +1;

        return (double)i < r ? -1 : (double)i > r ? 1 : 0;
    }

    /// <summary>
    /// The integer of serial type 1 to 6, 8 or 9 (vdbeRecordDecodeInt in vdbeaux.c)
    /// </summary>
    private static long ReadInteger(byte[] data, int end, ref int pos, long serialType)
    {
        if (serialType is 8 or 9)
            return serialType - 8;

        return ByteArrayReader.ReadInteger(data, end, ref pos, (int)SerialTypeLength(serialType));
    }

    private static double ReadReal(byte[] data, int end, ref int pos) =>
        BitConverter.Int64BitsToDouble(ByteArrayReader.ReadInteger(data, end, ref pos, 8));

    /// <summary>
    /// A varint, read without a call when it is one byte, as the header sizes and serial types of a key nearly always are
    /// </summary>
    private static long ReadVarInt(byte[] data, int length, ref int pos)
    {
        if (pos < length && data[pos] < 0x80)
            return data[pos++];

        return ByteArrayReader.ReadVarInt(data, length, ref pos);
    }

    /// <summary>
    /// sqlite3VdbeSerialTypeLen in vdbeaux.c
    /// </summary>
    private static long SerialTypeLength(long serialType) => serialType switch
    {
        >= 12 => (serialType - 12) / 2,
        5 => 6,
        6 or 7 => 8,
        >= 1 and <= 4 => serialType,
        _ => 0
    };
}
