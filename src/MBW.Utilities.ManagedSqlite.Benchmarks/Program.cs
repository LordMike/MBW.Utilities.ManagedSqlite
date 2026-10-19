using BenchmarkDotNet.Running;

namespace MBW.Utilities.ManagedSqlite.Benchmarks;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new FastConfig());
}
