using System;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

/// <summary>
/// The conversion behind the generic <c>TryGetOrdinal&lt;T&gt;</c> and <c>TryGetValueByName&lt;T&gt;</c>: a stored
/// value (null, long, double, string or byte[]) to <c>T</c>, as SQLite's <c>sqlite3_column_*</c> convert between
/// INTEGER and REAL, and never between TEXT or BLOB and anything else
/// </summary>
internal static class ValueConverter
{
    public static bool TryConvert<T>(object? stored, out T? value)
    {
        // NULL reads as the type's default: null for a reference or Nullable<T>, 0 for a number, as sqlite3_column_int
        // and sqlite3_column_double return 0 for NULL
        if (stored == null)
        {
            value = default;
            return true;
        }

        // Also true for the underlying type of a Nullable<T>, so long? takes a long as it is
        if (stored is T same)
        {
            value = same;
            return true;
        }

        Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        object? converted = stored switch
        {
            long integer => FromInteger(integer, target),
            double real => FromReal(real, target),
            _ => null
        };

        value = converted == null ? default : (T)converted;
        return converted != null;
    }

    private static object? FromInteger(long integer, Type target)
    {
        // SQLite turns an INTEGER into a REAL with a C cast. sqlite3VdbeRealValue in vdbemem.c:
        //   }else if( pMem->flags & (MEM_Int|MEM_IntReal) ){
        //     testcase( pMem->flags & MEM_IntReal );
        //     return (double)pMem->u.i;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L776 (sqlite3VdbeRealValue)
        if (target == typeof(double))
            return (double)integer;
        if (target == typeof(float))
            return (float)integer;
        if (target == typeof(decimal))
            return (decimal)integer;

        // SQLite has no boolean: an integer is true when it is not zero
        if (target == typeof(bool))
            return integer != 0;

        // Narrower integer types take the value only when it fits, it is never wrapped
        if (target == typeof(long))
            return integer;
        if (target == typeof(int))
            return integer >= int.MinValue && integer <= int.MaxValue ? (int)integer : null;
        if (target == typeof(short))
            return integer >= short.MinValue && integer <= short.MaxValue ? (short)integer : null;
        if (target == typeof(sbyte))
            return integer >= sbyte.MinValue && integer <= sbyte.MaxValue ? (sbyte)integer : null;
        if (target == typeof(ulong))
            return integer >= 0 ? (ulong)integer : null;
        if (target == typeof(uint))
            return integer >= 0 && integer <= uint.MaxValue ? (uint)integer : null;
        if (target == typeof(ushort))
            return integer >= 0 && integer <= ushort.MaxValue ? (ushort)integer : null;
        if (target == typeof(byte))
            return integer >= 0 && integer <= byte.MaxValue ? (byte)integer : null;

        return null;
    }

    private static object? FromReal(double real, Type target)
    {
        // A double just past float.MaxValue still rounds to it, as the C cast does; only one that rounds to an infinity
        // doesn't fit
        if (target == typeof(float))
        {
            float single = (float)real;
            return !float.IsInfinity(single) || double.IsInfinity(real) ? single : null;
        }

        // (decimal) throws past its range, and for an infinity; (double)decimal.MaxValue rounds up, so less than it fits
        if (target == typeof(decimal))
            return real > (double)decimal.MinValue && real < (double)decimal.MaxValue ? (decimal)real : null;

        // SQLite turns a REAL into an INTEGER by truncating it toward zero, and saturates past the range of a 64-bit
        // integer. sqlite3VdbeIntValue and sqlite3RealToI64 in vdbemem.c:
        //   }else if( flags & MEM_Real ){
        //     return sqlite3RealToI64(pMem->u.r);
        //   ...
        //   if( r<-9223372036854774784.0 ) return SMALLEST_INT64;
        //   if( r>+9223372036854774784.0 ) return LARGEST_INT64;
        //   return (i64)r;
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L650 (sqlite3VdbeIntValue)
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L879 (sqlite3RealToI64)
        // A saturated value is not the stored one, so a REAL outside that range does not convert. -2^63 is exact.
        if (real >= -9223372036854775808.0 && real <= 9223372036854774784.0)
            return FromInteger((long)real, target);

        return null;
    }
}
