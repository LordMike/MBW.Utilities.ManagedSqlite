// Polyfill for System.Threading.Lock (.NET 9), which netstandard2.0 does not have. The compiler lowers `lock` on a
// System.Threading.Lock to EnterScope() and Scope.Dispose(), so the code reads as it does on .NET 9 and later; here
// the scope is a Monitor enter and exit.

#if !NET9_0_OR_GREATER
namespace System.Threading
{
    /// <summary>A lock object, entered and left with the <c>lock</c> statement.</summary>
    internal sealed class Lock
    {
        // Monitor needs an object; the Lock itself would raise CS9216, as `lock` on it means the Lock pattern
        private readonly object _monitor = new object();

        /// <summary>Enters the lock, waiting if another thread holds it.</summary>
        public void Enter() => Monitor.Enter(_monitor);

        /// <summary>Leaves the lock.</summary>
        public void Exit() => Monitor.Exit(_monitor);

        /// <summary>Enters the lock and returns a scope whose <see cref="Scope.Dispose"/> leaves it.</summary>
        public Scope EnterScope()
        {
            Monitor.Enter(_monitor);
            return new Scope(this);
        }

        /// <summary>A held lock, left on <see cref="Dispose"/>.</summary>
        public ref struct Scope
        {
            private readonly Lock _owner;

            internal Scope(Lock owner) => _owner = owner;

            /// <summary>Leaves the lock.</summary>
            public void Dispose() => _owner.Exit();
        }
    }
}
#endif
