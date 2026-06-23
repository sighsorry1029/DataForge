using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace DataForge;

internal static class DataForgeHarmonyPatchProfiler
{
    private static readonly object Lock = new();
    private static readonly Harmony InstrumentationHarmony = new(DataForgePlugin.ModGUID + ".patchprofiler");
    private static readonly HashSet<MethodBase> InstrumentedMethods = new();
    private static readonly Dictionary<MethodBase, PatchDescriptor> Descriptors = new();
    private static readonly Dictionary<MethodBase, PatchTiming> ObjectDbAwakeTimings = new();
    private static readonly Stopwatch ObjectDbAwakeStopwatch = new();
    private static bool ObjectDbAwakeActive;
    private static bool ObjectDbAwakeReported;
    private static bool ObjectDbAwakeInstallAttempted;

    internal static void InstallObjectDbAwakeProfiling()
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        lock (Lock)
        {
            if (ObjectDbAwakeInstallAttempted)
            {
                return;
            }

            ObjectDbAwakeInstallAttempted = true;
        }

        MethodBase? target = AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.Awake));
        Patches? patchInfo = target != null ? Harmony.GetPatchInfo(target) : null;
        if (target == null || patchInfo == null)
        {
            return;
        }

        int installed = 0;
        int skipped = 0;
        int failed = 0;
        foreach ((Patch patch, string patchType) in EnumerateInvocationPatches(patchInfo))
        {
            MethodBase? patchMethod = patch.PatchMethod;
            if (!CanInstrument(patchMethod))
            {
                skipped++;
                continue;
            }

            try
            {
                lock (Lock)
                {
                    if (!InstrumentedMethods.Add(patchMethod!))
                    {
                        skipped++;
                        continue;
                    }

                    Descriptors[patchMethod!] = new PatchDescriptor(
                        patch.owner ?? "",
                        patchType,
                        patch.priority,
                        patch.index,
                        FormatMethodName(patchMethod!));
                }

                InstrumentationHarmony.Patch(
                    patchMethod!,
                    prefix: new HarmonyMethod(typeof(DataForgeHarmonyPatchProfiler), nameof(ProfiledPatchPrefix)),
                    finalizer: new HarmonyMethod(typeof(DataForgeHarmonyPatchProfiler), nameof(ProfiledPatchFinalizer)));
                installed++;
            }
            catch (Exception ex)
            {
                failed++;
                DataForgePlugin.Log.LogWarning($"Could not instrument ObjectDB.Awake {patchType} patch '{FormatMethodName(patchMethod!)}': {ex.Message}");
            }
        }

        DataForgePlugin.Log.LogInfo($"ObjectDB.Awake patch profiler installed: instrumented={installed}, skipped={skipped}, failed={failed}.");
    }

    internal static void BeginObjectDbAwake()
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        lock (Lock)
        {
            ObjectDbAwakeTimings.Clear();
            ObjectDbAwakeReported = false;
            ObjectDbAwakeActive = true;
            ObjectDbAwakeStopwatch.Restart();
        }
    }

    internal static void ReportObjectDbAwake()
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        List<(PatchDescriptor Descriptor, PatchTiming Timing)> timings;
        double totalMs;
        lock (Lock)
        {
            if (!ObjectDbAwakeActive || ObjectDbAwakeReported)
            {
                return;
            }

            ObjectDbAwakeStopwatch.Stop();
            totalMs = ObjectDbAwakeStopwatch.Elapsed.TotalMilliseconds;
            ObjectDbAwakeActive = false;
            ObjectDbAwakeReported = true;
            timings = ObjectDbAwakeTimings
                .Select(pair => (Descriptors.TryGetValue(pair.Key, out PatchDescriptor descriptor)
                    ? descriptor
                    : new PatchDescriptor("", "unknown", 0, 0, FormatMethodName(pair.Key)), pair.Value))
                .OrderByDescending(pair => pair.Value.ElapsedMs)
                .ToList();
        }

        StringBuilder builder = new();
        builder.AppendLine($"ObjectDB.Awake Harmony patch profile: total={totalMs:0.###} ms, measuredPatches={timings.Count}.");
        if (timings.Count == 0)
        {
            builder.AppendLine("  No instrumented ObjectDB.Awake patch methods were invoked.");
        }
        else
        {
            foreach ((PatchDescriptor descriptor, PatchTiming timing) in timings.Take(40))
            {
                builder.Append("  ");
                builder.Append(timing.ElapsedMs.ToString("0.###"));
                builder.Append(" ms");
                if (timing.Count > 1)
                {
                    builder.Append(" x").Append(timing.Count);
                }

                builder.Append(": ");
                builder.Append(descriptor.PatchType);
                builder.Append(" ");
                builder.Append(string.IsNullOrWhiteSpace(descriptor.Owner) ? "<unknown>" : descriptor.Owner);
                builder.Append(" [priority=");
                builder.Append(descriptor.Priority);
                builder.Append(", index=");
                builder.Append(descriptor.Index);
                builder.Append("] :: ");
                builder.AppendLine(descriptor.MethodName);
            }

            if (timings.Count > 40)
            {
                builder.Append("  ... ");
                builder.Append(timings.Count - 40);
                builder.AppendLine(" more patch methods omitted.");
            }
        }

        DataForgePlugin.Log.LogInfo(builder.ToString().TrimEnd());
    }

    private static void ProfiledPatchPrefix(MethodBase __originalMethod, out long __state)
    {
        __state = 0;
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        lock (Lock)
        {
            if (!ObjectDbAwakeActive)
            {
                return;
            }
        }

        __state = Stopwatch.GetTimestamp();
    }

    private static Exception? ProfiledPatchFinalizer(MethodBase __originalMethod, long __state, Exception? __exception)
    {
        if (__state <= 0 || !DataForgePlugin.LogStartupTimings)
        {
            return __exception;
        }

        double elapsedMs = (Stopwatch.GetTimestamp() - __state) * 1000d / Stopwatch.Frequency;
        lock (Lock)
        {
            if (ObjectDbAwakeActive)
            {
                if (!ObjectDbAwakeTimings.TryGetValue(__originalMethod, out PatchTiming timing))
                {
                    timing = new PatchTiming();
                    ObjectDbAwakeTimings[__originalMethod] = timing;
                }

                timing.Add(elapsedMs);
            }
        }

        return __exception;
    }

    private static IEnumerable<(Patch Patch, string PatchType)> EnumerateInvocationPatches(Patches patchInfo)
    {
        foreach (Patch patch in patchInfo.Prefixes)
        {
            yield return (patch, "prefix");
        }

        foreach (Patch patch in patchInfo.Postfixes)
        {
            yield return (patch, "postfix");
        }

        foreach (Patch patch in patchInfo.Finalizers)
        {
            yield return (patch, "finalizer");
        }
    }

    private static bool CanInstrument(MethodBase? method)
    {
        if (method == null ||
            method.DeclaringType == typeof(DataForgeHarmonyPatchProfiler) ||
            method.DeclaringType == null ||
            method.ContainsGenericParameters ||
            method.IsAbstract)
        {
            return false;
        }

        return true;
    }

    private static string FormatMethodName(MethodBase method)
    {
        string typeName = method.DeclaringType?.FullName ?? "<dynamic>";
        return typeName + "." + method.Name;
    }

    private readonly struct PatchDescriptor
    {
        internal PatchDescriptor(string owner, string patchType, int priority, int index, string methodName)
        {
            Owner = owner;
            PatchType = patchType;
            Priority = priority;
            Index = index;
            MethodName = methodName;
        }

        internal string Owner { get; }
        internal string PatchType { get; }
        internal int Priority { get; }
        internal int Index { get; }
        internal string MethodName { get; }
    }

    private sealed class PatchTiming
    {
        internal int Count { get; private set; }
        internal double ElapsedMs { get; private set; }

        internal void Add(double elapsedMs)
        {
            Count++;
            ElapsedMs += elapsedMs;
        }
    }
}
