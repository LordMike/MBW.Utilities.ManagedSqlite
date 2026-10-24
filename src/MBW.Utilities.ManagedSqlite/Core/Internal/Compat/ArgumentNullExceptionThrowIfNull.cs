// Polyfill for ArgumentNullException.ThrowIfNull (.NET 6), which netstandard2.0 does not have. A static extension
// member, so the call reads as it does on .NET 6 and later, and CallerArgumentExpression names the parameter.

#if !NET6_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System
{
    internal static class ArgumentNullExceptionExtensions
    {
        extension(ArgumentNullException)
        {
            /// <summary>Throws an <see cref="ArgumentNullException"/> if <paramref name="argument"/> is null.</summary>
            public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            {
                if (argument is null)
                    throw new ArgumentNullException(paramName);
            }
        }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>Gives a parameter the source text of another parameter's argument.</summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

        public string ParameterName { get; }
    }
}
#endif
