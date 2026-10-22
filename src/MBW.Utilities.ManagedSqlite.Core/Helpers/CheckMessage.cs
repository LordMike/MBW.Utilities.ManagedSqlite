using System.Runtime.CompilerServices;
using System.Text;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

/// <summary>
/// The message of a consistency check, built only when the check fails. The compiler passes the check's result to the
/// constructor, and when it is true none of the interpolated string's parts are evaluated, so a passing check costs no
/// allocation.
/// </summary>
/// <remarks>
/// A plain struct, not a ref struct, so it can be used inside the iterator methods that walk pages and rows.
/// </remarks>
[InterpolatedStringHandler]
internal struct CheckMessage
{
    private readonly StringBuilder? _builder;

    public CheckMessage(int literalLength, int formattedCount, bool ok, out bool shouldAppend)
    {
        shouldAppend = !ok;
        _builder = ok ? null : new StringBuilder(literalLength + formattedCount * 8);
    }

    public void AppendLiteral(string value) => _builder!.Append(value);

    // Boxes value types through StringBuilder.Append(object), which only happens on a failed check
    public void AppendFormatted<T>(T value) => _builder!.Append(value);

    public override string ToString() => _builder?.ToString() ?? string.Empty;
}
