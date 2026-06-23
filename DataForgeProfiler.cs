using System;
using System.Diagnostics;

namespace DataForge;

internal static class DataForgeProfiler
{
    internal static void Profile(string label, Action action)
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            action();
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            stopwatch.Stop();
            DataForgeConnectionProfiler.MarkProfiledPhase(label, stopwatch.Elapsed.TotalMilliseconds);
            DataForgePlugin.Log.LogInfo($"DataForge profile: {label} took {stopwatch.Elapsed.TotalMilliseconds:0.###} ms.");
        }
    }
}
