// Polyfills for the interpolated string handler attributes, which netstandard1.0 and netstandard2.0 do not have.
// The compiler matches these by name, so internal copies are enough for Helpers/CheckMessage.cs.

#if !NET6_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>Marks a type as an interpolated string handler.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }

    /// <summary>Names the parameters whose values are passed to the handler's constructor.</summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        public InterpolatedStringHandlerArgumentAttribute(string argument) => Arguments = new[] { argument };

        public InterpolatedStringHandlerArgumentAttribute(params string[] arguments) => Arguments = arguments;

        public string[] Arguments { get; }
    }
}
#endif
