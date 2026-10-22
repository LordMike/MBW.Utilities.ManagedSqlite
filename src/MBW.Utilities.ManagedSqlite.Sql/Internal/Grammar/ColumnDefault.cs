using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The value SQLite reads for a column that a record is too short to hold, as a row written before ALTER TABLE ADD
/// COLUMN is. OP_Column then takes the value sqlite3ColumnDefault in update.c put in its P4: the DEFAULT (or a STORED
/// generated column's expression) as sqlite3ValueFromExpr in vdbemem.c evaluates it, with the column's affinity.
///   sqlite3ValueFromExpr(sqlite3VdbeDb(v),
///                        sqlite3ColumnExpr(pTab,pCol), enc,
///                        pCol->affinity, &amp;pValue);
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/update.c#L61 (sqlite3ColumnDefault)
/// sqlite3ValueFromExpr evaluates only a literal, a negated number, a CAST of those, TRUE or FALSE; for anything else it
/// makes no value, and the column reads as NULL. ALTER TABLE ADD COLUMN refuses a DEFAULT it cannot evaluate ("Cannot
/// add a column with non-constant default").
/// </summary>
internal static class ColumnDefault
{
    /// <summary>
    /// The value of <paramref name="expr"/>, the column's default, or null when there is none. False for a CAST and for
    /// a negated negation, which this library does not evaluate.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L1795 (valueFromExpr)
    /// </summary>
    public static bool TryEvaluate(Expr? expr, char affinity, out object? value)
    {
        value = null;
        if (expr == null)
            return true;

        //   while( (op = pExpr->op)==TK_UPLUS || op==TK_SPAN ) pExpr = pExpr->pLeft;
        int op;
        while ((op = expr.Op) == TK.UPLUS || op == TK.SPAN)
            expr = expr.Left!;

        // CAST converts with sqlite3VdbeMemCast, which this library does not port
        if (op == TK.CAST)
            return false;

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
                    value = ParseReal((string)value);
                else if (op == TK.INTEGER)
                    value = ApplyAffinity(value, ParsedColumn.AFF_NUMERIC);
            }
            else
                value = ApplyAffinity(value, affinity);

            return true;
        }

        if (op == TK.UMINUS)
        {
            // -(-5), or a negated hexadecimal literal: the operand's value, made numeric and negated
            //   if( SQLITE_OK==valueFromExpr(db,pExpr->pLeft,enc,affinity,&pVal,pCtx)
            //    && pVal!=0
            //   ){
            //     sqlite3VdbeMemNumerify(pVal);
            // Only a negated hexadecimal literal is evaluated here, as its value is an integer; the rest would need
            // sqlite3VdbeMemNumerify's text conversions.
            Expr left = expr.Left!;
            if (left.Op != TK.INTEGER || (left.Flags & Expr.EP_IntValue) != 0 || !TryDecOrHexToI64(left.Text!, out long hex))
                return false;

            //     }else if( pVal->u.i==SMALLEST_INT64 ){
            //       pVal->u.r = -(double)SMALLEST_INT64;
            // That real would be made text for a TEXT column, with SQLite's real formatting, which is not ported
            if (hex == long.MinValue && affinity == ParsedColumn.AFF_TEXT)
                return false;

            value = ApplyAffinity(hex == long.MinValue ? -(double)long.MinValue : (object)(-hex), affinity);
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
            value = bytes;
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
                // hexadecimal literal that is a real, -0x8000000000000000, is not evaluated for a TEXT column
                if (value is long integer)
                    value = integer.ToString(CultureInfo.InvariantCulture);
                break;
        }

        return value;
    }

    // sqlite3AtoF in util.c, for text that is a number from start to end: optional white space, a sign, digits with
    // an optional '.', an optional exponent, and optional white space
    private static readonly Regex Number = new Regex(@"^[ \t\n\v\f\r]*[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?[ \t\n\v\f\r]*$");

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
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbe.c#L357 (applyNumericAffinity)
    /// </summary>
    private static object ApplyNumericAffinity(string text)
    {
        Match match = Number.Match(text);
        if (!match.Success)
            return text;

        if (!text.Contains(".") && !match.Groups[2].Success &&
            long.TryParse(text.Trim(' ', '\t', '\n', '\v', '\f', '\r'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
            return integer;

        return IntegerAffinity(ParseReal(text));
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
        long ix = real < -9223372036854774784.0 ? long.MinValue : real > 9223372036854774784.0 ? long.MaxValue : (long)real;
        return real == ix && ix > long.MinValue && ix < long.MaxValue ? (object)ix : real;
    }

    /// <summary>
    /// The real of numeric text, rounded to nearest; a number too large for a double is infinite, as from sqlite3AtoF
    /// </summary>
    private static double ParseReal(string text)
    {
        text = text.Trim(' ', '\t', '\n', '\v', '\f', '\r');
        if (double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out double real))
            return real;

        // Older frameworks fail on overflow instead of returning infinity
        return text[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
    }
}
