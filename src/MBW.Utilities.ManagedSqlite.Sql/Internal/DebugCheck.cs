using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal;

internal static class DebugCheck
{
    /// <summary>
    /// <see cref="Debug.Assert(bool)"/>, with the annotation the netstandard reference assemblies lack, so the
    /// nullable analysis knows what the condition guarantees after the call.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Assert([DoesNotReturnIf(false)] bool condition) => Debug.Assert(condition);
}
