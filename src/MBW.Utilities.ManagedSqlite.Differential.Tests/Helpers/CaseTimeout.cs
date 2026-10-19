using System;
using System.Threading.Tasks;
using Xunit.Sdk;

namespace MBW.Utilities.ManagedSqlite.Differential.Tests.Helpers;

internal static class CaseTimeout
{
    /// <summary>Generous: every case finishes in well under a second today.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runs a case on the thread pool, so a library bug that loops forever fails that one case instead of hanging the
    /// test run. The looping thread is abandoned; it does not keep the test process alive.
    /// </summary>
    public static async Task Run(Action action, string caseName)
    {
        Task task = Task.Run(action);

        if (await Task.WhenAny(task, Task.Delay(Default)) != task)
            throw new XunitException($"Case '{caseName}' did not finish within {Default.TotalSeconds:0}s (hang?)");

        await task;
    }
}
