using System;
using System.IO;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Perfolizer.Horology;

namespace MBW.Utilities.ManagedSqlite.Benchmarks;

/// <summary>
/// A short job so each benchmark case finishes in a few seconds. Runs in-process to skip the per-benchmark
/// rebuild and process launch that BenchmarkDotNet does by default.
/// </summary>
internal class FastConfig : ManualConfig
{
    public FastConfig()
    {
        Add(DefaultConfig.Instance);

        // Keep reports under bin/, which git ignores
        ArtifactsPath = Path.Combine(AppContext.BaseDirectory, "BenchmarkDotNet.Artifacts");

        AddJob(Job.Default
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithLaunchCount(1)
            .WithWarmupCount(2)
            .WithIterationCount(5)
            .WithIterationTime(TimeInterval.FromMilliseconds(100)));

        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(CategoriesColumn.Default);
        AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByParams);
        WithOptions(ConfigOptions.JoinSummary);
    }
}
