using System;
using System.Globalization;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The value SQLite reads for a column that a record is too short to hold, as a row written before ALTER TABLE ADD
/// COLUMN is. OP_Column then takes the value sqlite3ColumnDefault in update.c put in its P4: the DEFAULT (or a STORED
/// generated column's expression) as sqlite3ValueFromExpr in vdbemem.c evaluates it, with the column's affinity.
///   sqlite3ValueFromExpr(sqlite3VdbeDb(v),
///                        sqlite3ColumnExpr(pTab,pCol), enc,
///                        pCol->affinity, &amp;pValue);
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/update.c#L61 (sqlite3ColumnDefault)
/// sqlite3ValueFromExpr evaluates only a literal, TRUE, FALSE, and a negation or CAST of those; for anything else it
/// makes no value, and the column reads as NULL. ALTER TABLE ADD COLUMN refuses a DEFAULT it cannot evaluate ("Cannot
/// add a column with non-constant default").
/// </summary>
internal static class ColumnDefault
{
    /// <summary>
    /// The value of <paramref name="expr"/>, the column's default, in a database of text encoding
    /// <paramref name="encoding"/>, or null when there is none. False when a real would be made text, as SQLite's real
    /// formatting is not ported.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L1795 (valueFromExpr)
    /// </summary>
    public static bool TryEvaluate(Expr? expr, char affinity, SqliteEncoding encoding, out object? value)
    {
        if (!TryEvaluateValue(expr, affinity, encoding, out value))
            return false;

        if (value is Blob blob)
            value = blob.Bytes;
        return true;
    }

    /// <summary>
    /// A blob and the encoding SQLite keeps with it (Mem.enc), which decides how it reads as text or a number: UTF-8 for
    /// a blob literal (sqlite3VdbeMemSetStr with enc 0), the database's encoding for text or a number cast to BLOB
    ///   }else if( enc==0 ){
    ///     flags = MEM_Blob;
    ///     enc = SQLITE_UTF8;
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L1308 (sqlite3VdbeMemSetStr)
    /// </summary>
    private sealed class Blob
    {
        public readonly byte[] Bytes;
        public readonly SqliteEncoding Encoding;

        public Blob(byte[] bytes, SqliteEncoding encoding)
        {
            Bytes = bytes;
            Encoding = encoding;
        }
    }

    private static bool TryEvaluateValue(Expr? expr, char affinity, SqliteEncoding encoding, out object? value)
    {
        value = null;
        if (expr == null)
            return true;

        //   while( (op = pExpr->op)==TK_UPLUS || op==TK_SPAN ) pExpr = pExpr->pLeft;
        int op;
        while ((op = expr.Op) == TK.UPLUS || op == TK.SPAN)
            expr = expr.Left!;

        // The operand evaluated with the affinity of the CAST's type, converted to that type, and then given the
        // column's affinity
        //   if( op==TK_CAST ){
        //     u8 aff;
        //     assert( !ExprHasProperty(pExpr, EP_IntValue) );
        //     aff = sqlite3AffinityType(pExpr->u.zToken,0);
        //     rc = valueFromExpr(db, pExpr->pLeft, enc, aff, ppVal, pCtx);
        //     testcase( rc!=SQLITE_OK );
        //     if( *ppVal ){
        //       ...
        //       sqlite3VdbeMemCast(*ppVal, aff, enc);
        //       sqlite3ValueApplyAffinity(*ppVal, affinity, enc);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L1820 (valueFromExpr)
        if (op == TK.CAST)
        {
            char castAffinity = SchemaParser.AffinityType(expr.Text!);
            if (!TryEvaluateValue(expr.Left, castAffinity, encoding, out object? operand))
                return false;

            // NULL, or no value, stays so
            if (operand == null)
                return true;

            if (!TryCast(operand, castAffinity, encoding, out object cast) || (cast is double && affinity == ParsedColumn.AFF_TEXT))
                return false;

            value = ApplyAffinity(cast, affinity);
            return true;
        }

        // A negative number in one step, so -9223372036854775808 is an integer. Not for a hexadecimal literal, which
        // is negated as a value below.
        //   if( op==TK_UMINUS ){
        //     Expr *pLeft = pExpr->pLeft;
        //     if( (pLeft->op==TK_INTEGER || pLeft->op==TK_FLOAT) ){
        //       if( ExprHasProperty(pLeft, EP_IntValue)
        //        || pLeft->u.zToken[0]!='0' || (pLeft->u.zToken[1] & ~0x20)!='X'
        bool negative = false;
        if (op == TK.UMINUS)
        {
            Expr left = expr.Left!;
            if ((left.Op == TK.INTEGER || left.Op == TK.FLOAT) &&
                ((left.Flags & Expr.EP_IntValue) != 0 || !IsHex(left.Text!)))
            {
                expr = left;
                op = expr.Op;
                negative = true;
            }
        }

        if (op == TK.STRING || op == TK.FLOAT || op == TK.INTEGER)
        {
            if ((expr.Flags & Expr.EP_IntValue) != 0)
                value = (long)expr.IntValue * (negative ? -1 : 1);
            else if (op == TK.INTEGER && TryDecOrHexToI64(expr.Text!, out long integer))
                value = negative ? -integer : integer;
            else
                value = (negative ? "-" : "") + expr.Text;

            //   if( affinity==SQLITE_AFF_BLOB ){
            //     if( op==TK_FLOAT ){
            //       sqlite3AtoF(pVal->z, &pVal->u.r);
            //       pVal->flags = MEM_Real;
            //     }else if( op==TK_INTEGER ){
            //       sqlite3ValueApplyAffinity(pVal, SQLITE_AFF_NUMERIC, SQLITE_UTF8);
            //     }
            //   }else{
            //     sqlite3ValueApplyAffinity(pVal, affinity, SQLITE_UTF8);
            //   }
            if (affinity == ParsedColumn.AFF_BLOB)
            {
                if (op == TK.FLOAT)
                {
                    AtoF((string)value, out double real);
                    value = real;
                }
                else if (op == TK.INTEGER)
                    value = ApplyAffinity(value, ParsedColumn.AFF_NUMERIC);
            }
            else
                value = ApplyAffinity(value, affinity);

            return true;
        }

        if (op == TK.UMINUS)
        {
            // Any other negation, such as -'5' or -(-5): the operand's value, made numeric and negated
            //   if( SQLITE_OK==valueFromExpr(db,pExpr->pLeft,enc,affinity,&pVal,pCtx)
            //    && pVal!=0
            //   ){
            //     sqlite3VdbeMemNumerify(pVal);
            //     if( pVal->flags & MEM_Real ){
            //       pVal->u.r = -pVal->u.r;
            //     }else if( pVal->u.i==SMALLEST_INT64 ){
            //       pVal->u.r = -(double)SMALLEST_INT64;
            //       MemSetTypeFlag(pVal, MEM_Real);
            //     }else{
            //       pVal->u.i = -pVal->u.i;
            //     }
            //     sqlite3ValueApplyAffinity(pVal, affinity, enc);
            if (!TryEvaluateValue(expr.Left, affinity, encoding, out object? operand))
                return false;

            // NULL, or no value, stays so
            if (operand == null)
                return true;

            object number = Numerify(operand);
            if (number is double real)
            {
                // A real is made text for a TEXT column with SQLite's real formatting, which is not ported
                if (affinity == ParsedColumn.AFF_TEXT)
                    return false;
                value = ApplyAffinity(-real, affinity);
            }
            else
            {
                long integer = (long)number;
                if (integer == long.MinValue && affinity == ParsedColumn.AFF_TEXT)
                    return false;
                value = ApplyAffinity(integer == long.MinValue ? -(double)long.MinValue : (object)(-integer), affinity);
            }

            return true;
        }

        if (op == TK.NULL)
            return true;

        //   else if( op==TK_BLOB ){
        //     zVal = &pExpr->u.zToken[2];
        //     nVal = sqlite3Strlen30(zVal)-1;
        //     sqlite3VdbeMemSetStr(pVal, sqlite3HexToBlob(db, zVal, nVal), nVal/2,
        if (op == TK.BLOB)
        {
            string hex = expr.Text!;
            byte[] bytes = new byte[(hex.Length - 3) / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)((SchemaParser.HexToInt(hex[2 + 2 * i]) << 4) | SchemaParser.HexToInt(hex[3 + 2 * i]));
            value = new Blob(bytes, SqliteEncoding.UTF8);
            return true;
        }

        //   else if( op==TK_TRUEFALSE ){
        //       pVal->u.i = pExpr->u.zToken[4]==0;
        if (op == TK.TRUEFALSE)
        {
            value = ApplyAffinity(expr.Text!.Length == 4 ? 1L : 0L, affinity);
            return true;
        }

        // No value: OP_Column reads NULL
        return true;
    }

    /// <summary>
    /// sqlite3VdbeMemCast in vdbemem.c: <paramref name="value"/>, which is not NULL, converted to the affinity
    /// <paramref name="affinity"/> even where that loses data. False when a real would be made text.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L926 (sqlite3VdbeMemCast)
    /// </summary>
    private static bool TryCast(object value, char affinity, SqliteEncoding encoding, out object cast)
    {
        cast = value;
        switch (affinity)
        {
            case ParsedColumn.AFF_BLOB:
                // Made text, in the database's encoding, and its bytes kept as a blob
                //   if( (pMem->flags & MEM_Blob)==0 ){
                //     sqlite3ValueApplyAffinity(pMem, SQLITE_AFF_TEXT, encoding);
                //     assert( pMem->flags & MEM_Str || pMem->db->mallocFailed );
                //     if( pMem->flags & MEM_Str ) MemSetTypeFlag(pMem, MEM_Blob);
                if (value is Blob)
                    return true;
                if (value is double)
                    return false;
                cast = new Blob(ToEncoding(encoding).GetBytes(value as string ?? ((long)value).ToString(CultureInfo.InvariantCulture)), encoding);
                return true;
            case ParsedColumn.AFF_NUMERIC:
                cast = Numerify(value);
                return true;
            case ParsedColumn.AFF_INTEGER:
                // sqlite3VdbeMemIntegerify: sqlite3VdbeIntValue, which reads text with sqlite3Atoi64 and ignores its
                // result code
                //   }else if( flags & MEM_Real ){
                //     return sqlite3RealToI64(pMem->u.r);
                //   }else if( (flags & (MEM_Str|MEM_Blob))!=0 && pMem->z!=0 ){
                //     return memIntValue(pMem);
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L641 (sqlite3VdbeIntValue)
                if (value is double fromReal)
                    cast = RealToI64(fromReal);
                else if (value is not long)
                {
                    Atoi64(ToText(value, out _), out long fromText);
                    cast = fromText;
                }

                return true;
            case ParsedColumn.AFF_REAL:
                // sqlite3VdbeMemRealify: sqlite3VdbeRealValue, which reads text with sqlite3AtoF and ignores its result
                // code
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L770 (sqlite3VdbeRealValue)
                if (value is long fromInteger)
                    cast = (double)fromInteger;
                else if (value is not double)
                {
                    AtoF(ToText(value, out _), out double fromText);
                    cast = fromText;
                }

                return true;
            default:
                // TEXT: a blob's bytes become text in the blob's encoding, which is then translated to the database's.
                // In a UTF-16 database an odd last byte is dropped first.
                //   pMem->flags |= (pMem->flags&MEM_Blob)>>3;
                //   sqlite3ValueApplyAffinity(pMem, SQLITE_AFF_TEXT, encoding);
                //   ...
                //   if( encoding!=SQLITE_UTF8 ) pMem->n &= ~1;
                //   rc = sqlite3VdbeChangeEncoding(pMem, encoding);
                if (value is Blob blob)
                    cast = ToEncoding(blob.Encoding).GetString(blob.Bytes, 0, encoding == SqliteEncoding.UTF8 ? blob.Bytes.Length : blob.Bytes.Length & ~1);
                else if (value is long number)
                    cast = number.ToString(CultureInfo.InvariantCulture);
                else if (value is double)
                    return false;

                return true;
        }
    }

    private static Encoding ToEncoding(SqliteEncoding encoding) => encoding switch
    {
        SqliteEncoding.UTF16LE => Encoding.Unicode,
        SqliteEncoding.UTF16BE => Encoding.BigEndianUnicode,
        _ => Encoding.UTF8
    };

    /// <summary>
    /// Text as SQLite's numeric conversions read it. A UTF-8 blob's bytes are the C string SQLite reads, which only
    /// matters up to the first byte that is not part of a number. A UTF-16 blob is read as SQLite copies it for
    /// sqlite3AtoF: the low bytes of its characters, up to the first character above 0xFF, which sets
    /// <paramref name="cut"/>.
    ///   if( pMem->enc==SQLITE_UTF16LE ){
    ///     for(i=j=0; i&lt;n-1; i+=2, j++){
    ///       zCopy[j] = z[i];
    ///       if( z[i+1]!=0 ) break;
    ///     }
    ///   ...
    ///   if( i&lt;n ) rc = -100;
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L697 (sqlite3MemRealValueRCSlowPath)
    /// sqlite3Atoi64 in util.c stops at the same character.
    /// </summary>
    private static string ToText(object value, out bool cut)
    {
        cut = false;
        if (value is not Blob blob)
            return (string)value;

        byte[] bytes = blob.Bytes;
        if (blob.Encoding == SqliteEncoding.UTF8)
        {
            char[] chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                chars[i] = (char)bytes[i];
            return new string(chars);
        }

        int low = blob.Encoding == SqliteEncoding.UTF16LE ? 0 : 1;
        StringBuilder text = new StringBuilder(bytes.Length / 2);
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i + 1 - low] != 0)
            {
                cut = true;
                break;
            }

            text.Append((char)bytes[i + low]);
        }

        return text.ToString();
    }

    private static bool IsHex(string token) => token.Length > 1 && token[0] == '0' && (token[1] | 0x20) == 'x';

    /// <summary>
    /// sqlite3DecOrHexToI64 in util.c: a hexadecimal literal of up to 16 digits as its 64 bits, or a decimal one that
    /// fits a 64-bit integer
    /// </summary>
    private static bool TryDecOrHexToI64(string token, out long value)
    {
        if (IsHex(token))
        {
            int i = 2;
            while (i < token.Length && token[i] == '0')
                i++;

            ulong u = 0;
            for (int k = i; k < token.Length; k++)
                u = (u << 4) + (ulong)SchemaParser.HexToInt(token[k]);

            value = unchecked((long)u);
            return token.Length - i <= 16;
        }

        return long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// applyAffinity in vdbe.c, then OP_RealAffinity for a REAL column (sqlite3ColumnDefault): NUMERIC, INTEGER and
    /// REAL make well-formed numeric text a number, preferring an integer; TEXT makes an integer text. BLOB changes
    /// nothing.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L397 (applyAffinity)
    /// </summary>
    private static object ApplyAffinity(object value, char affinity)
    {
        switch (affinity)
        {
            case ParsedColumn.AFF_NUMERIC:
            case ParsedColumn.AFF_INTEGER:
            case ParsedColumn.AFF_REAL:
                if (value is string text)
                    value = ApplyNumericAffinity(text);
                //   }else if( affinity<=SQLITE_AFF_REAL ){
                //     sqlite3VdbeIntegerAffinity(pRec);
                else if (value is double real)
                    value = IntegerAffinity(real);
                break;
            case ParsedColumn.AFF_TEXT:
                // A real is never made text here: a literal real stays the text of its token, and the one negated
                // hexadecimal literal that is a real, -0x8000000000000000, and a CAST to a real are not evaluated for a
                // TEXT column
                if (value is long integer)
                    value = integer.ToString(CultureInfo.InvariantCulture);
                break;
        }

        return value;
    }

    /// <summary>
    /// applyNumericAffinity in vdbe.c, with bTryForInt: text that is a number becomes an integer when it is written as
    /// one and fits, otherwise a real, which is then an integer if it is a whole number that fits. Other text is kept.
    ///   rc = sqlite3MemRealValueRC(pRec, &amp;rValue);
    ///   if( rc&lt;=0 ) return;
    ///   if( (rc&amp;2)==0 &amp;&amp; alsoAnInt(pRec, rValue, &amp;pRec->u.i) ){
    ///     pRec->flags |= MEM_Int;
    ///   }else{
    ///     pRec->u.r = rValue;
    ///     pRec->flags |= MEM_Real;
    ///     if( bTryForInt ) sqlite3VdbeIntegerAffinity(pRec);
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L354 (applyNumericAffinity)
    /// </summary>
    private static object ApplyNumericAffinity(string text)
    {
        int rc = AtoF(text, out double real);
        if (rc <= 0)
            return text;

        // alsoAnInt in vdbe.c: the real is an integer below 2^51, or the text is an integer that fits
        if ((rc & 2) == 0)
        {
            long ix = RealToI64(real);
            if (RealSameAsInt(real, ix))
                return ix;
            if (Atoi64(text, out ix) == 0)
                return ix;
        }

        return IntegerAffinity(real);
    }

    /// <summary>
    /// sqlite3VdbeMemNumerify in vdbemem.c: text, or a blob's bytes, becomes the number it starts with (0 when none),
    /// an integer when it is written as one that fits, or a whole real below 2^51
    ///   rc = sqlite3MemRealValueRC(pMem, &amp;pMem->u.r);
    ///   if( ((rc&amp;2)==0 &amp;&amp; sqlite3Atoi64(pMem->z, &amp;ix, pMem->n, pMem->enc)&lt;2)
    ///    || sqlite3RealSameAsInt(pMem->u.r, (ix = sqlite3RealToI64(pMem->u.r)))
    ///   ){
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L893 (sqlite3VdbeMemNumerify)
    /// </summary>
    private static object Numerify(object value)
    {
        if (value is long || value is double)
            return value;

        string text = ToText(value, out bool cut);
        int rc = AtoF(text, out double real);
        if (cut)
            rc = -100;
        if ((rc & 2) == 0 && Atoi64(text, out long ix) < 2)
            return ix;

        ix = RealToI64(real);
        return RealSameAsInt(real, ix) ? (object)ix : real;
    }

    /// <summary>
    /// sqlite3VdbeIntegerAffinity in vdbemem.c: a real that is a whole number, other than the smallest and largest
    /// integers, becomes that integer
    ///   i64 ix = sqlite3RealToI64(pMem->u.r);
    ///   if( pMem->u.r==ix &amp;&amp; ix>SMALLEST_INT64 &amp;&amp; ix&lt;LARGEST_INT64 ){
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L802 (sqlite3VdbeIntegerAffinity)
    /// </summary>
    private static object IntegerAffinity(double real)
    {
        long ix = RealToI64(real);
        return real == ix && ix > long.MinValue && ix < long.MaxValue ? (object)ix : real;
    }

    /// <summary>
    /// sqlite3RealToI64 in vdbemem.c
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L879 (sqlite3RealToI64)
    /// </summary>
    private static long RealToI64(double r) =>
        r < -9223372036854774784.0 ? long.MinValue : r > 9223372036854774784.0 ? long.MaxValue : (long)r;

    /// <summary>
    /// sqlite3RealSameAsInt in vdbemem.c: zero, or the same bits as the integer, which is below 2^51
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L868 (sqlite3RealSameAsInt)
    /// </summary>
    private static bool RealSameAsInt(double r, long i) =>
        r == 0.0 || (BitConverter.DoubleToInt64Bits(r) == BitConverter.DoubleToInt64Bits(i) && i >= -2251799813685248L && i < 2251799813685248L);

    private static bool IsSpace(char c) => c == ' ' || (c >= '\t' && c <= '\r');

    private static char At(string z, int i) => i < z.Length ? z[i] : '\0';

    /// <summary>
    /// sqlite3AtoF in util.c. The value is s * 10^d for a 64-bit mantissa s, so only about the first 19 significant
    /// digits count, as in SQLite. The result is the mState bits (1: a digit, 2: a '.' or an exponent) when the whole
    /// text is a number, negative when only a prefix is, and 0 when none is.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L871 (sqlite3AtoF)
    /// </summary>
    private static int AtoF(string z, out double result)
    {
        const ulong limit = (ulong.MaxValue - 9) / 10;
        int p = 0;
        bool neg = false;
        ulong s = 0;
        int d = 0;
        int mState = 0;

        while (IsSpace(At(z, p)))
            p++;
        if (At(z, p) == '-' || At(z, p) == '+')
        {
            neg = At(z, p) == '-';
            p++;
        }

        if (IsDigit(At(z, p)))
        {
            mState = 1;
            s = (ulong)(At(z, p++) - '0');
            while (IsDigit(At(z, p)))
            {
                s = s * 10 + (ulong)(At(z, p++) - '0');
                if (s >= limit)
                {
                    mState = 9;
                    while (IsDigit(At(z, p)))
                    {
                        p++;
                        d++;
                    }

                    break;
                }
            }
        }

        if (At(z, p) == '.')
        {
            p++;
            if (IsDigit(At(z, p)))
            {
                mState |= 1;
                do
                {
                    if (s < limit)
                    {
                        s = s * 10 + (ulong)(At(z, p) - '0');
                        d--;
                    }
                    else
                        mState = 11;
                } while (IsDigit(At(z, ++p)));
            }
            else if (mState == 0)
            {
                result = 0.0;
                return 0;
            }

            mState |= 2;
        }
        else if (mState == 0)
        {
            result = 0.0;
            return 0;
        }

        if (At(z, p) == 'e' || At(z, p) == 'E')
        {
            int q = p + 1;
            int esign = 1;
            if (At(z, q) == '-')
            {
                esign = -1;
                q++;
            }
            else if (At(z, q) == '+')
                q++;

            if (IsDigit(At(z, q)))
            {
                int exp = 0;
                while (IsDigit(At(z, q)))
                {
                    exp = exp < 10000 ? exp * 10 + (At(z, q) - '0') : 10000;
                    q++;
                }

                mState |= 2;
                d += esign * exp;
                p = q;
            }
        }

        // sqlite3Fp10Convert2 rounds s * 10^d to the nearest double, as parsing that text does
        if (s == 0)
            result = 0.0;
        else if (!double.TryParse(s.ToString(CultureInfo.InvariantCulture) + "E" + d.ToString(CultureInfo.InvariantCulture),
                     NumberStyles.AllowExponent | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
            // Older frameworks fail on overflow instead of returning infinity
            result = double.PositiveInfinity;
        if (neg)
            result = -result;

        while (IsSpace(At(z, p)))
            p++;
        return At(z, p) == '\0' ? mState : unchecked((int)0xfffffff0) | mState;
    }

    /// <summary>
    /// sqlite3Atoi64 in util.c: white space, a sign, and the digits that follow, as a 64-bit integer, or the smallest or
    /// largest one when they do not fit. 0 when that is all the text and it fits, 1 when other text follows, 2 or 3 when
    /// it does not fit, -1 for no digits.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L1166 (sqlite3Atoi64)
    /// </summary>
    private static int Atoi64(string z, out long value)
    {
        int p = 0;
        bool neg = false;
        while (IsSpace(At(z, p)))
            p++;
        if (At(z, p) == '-' || At(z, p) == '+')
            neg = z[p++] == '-';

        int start = p;
        while (At(z, p) == '0')
            p++;
        int first = p;
        ulong u = 0;
        while (IsDigit(At(z, p)))
            u = unchecked(u * 10 + (ulong)(z[p++] - '0'));
        int digits = p - first;

        value = u > long.MaxValue ? (neg ? long.MinValue : long.MaxValue) : neg ? -(long)u : (long)u;

        int rc = 0;
        if (digits == 0 && first == start)
            rc = -1;
        else
        {
            for (int k = p; At(z, k) != '\0'; k++)
            {
                if (!IsSpace(z[k]))
                {
                    rc = 1;
                    break;
                }
            }
        }

        if (digits < 19)
            return rc;

        // A 19-digit number against 9223372036854775808; more digits never fit
        int c = digits > 19 ? 1 : string.CompareOrdinal(z.Substring(first, 19), "9223372036854775808");
        if (c < 0)
            return rc;
        value = neg ? long.MinValue : long.MaxValue;
        if (c > 0)
            return 2;
        return neg ? rc : 3;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
