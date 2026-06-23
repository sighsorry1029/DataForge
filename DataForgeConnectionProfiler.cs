using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;

namespace DataForge;

internal static class DataForgeConnectionProfiler
{
    private static readonly object Lock = new();
    private static readonly Stopwatch Stopwatch = new();
    private static readonly List<Milestone> Milestones = new();
    private static readonly HashSet<string> SeenOneShotMilestones = new(StringComparer.Ordinal);
    private static bool Active;
    private static bool Completed;

    internal static void Begin(string label)
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        lock (Lock)
        {
            Active = true;
            Completed = false;
            Milestones.Clear();
            SeenOneShotMilestones.Clear();
            Stopwatch.Restart();
            AddMilestone(label);
        }
    }

    internal static void Mark(string label)
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        lock (Lock)
        {
            if (!Active || Completed)
            {
                return;
            }

            AddMilestone(label);
        }
    }

    internal static void MarkOnce(string label)
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        lock (Lock)
        {
            if (!Active || Completed || !SeenOneShotMilestones.Add(label))
            {
                return;
            }

            AddMilestone(label);
        }
    }

    internal static void MarkProfiledPhase(string label, double elapsedMs)
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        Mark($"DataForge {label} ({elapsedMs:0.###} ms)");
    }

    internal static void Complete(string label)
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        lock (Lock)
        {
            if (!Active || Completed)
            {
                return;
            }

            AddMilestone(label);
            Completed = true;
            Stopwatch.Stop();
            LogSummary("completed");
            Active = false;
        }
    }

    internal static void Abort(string label)
    {
        if (!DataForgePlugin.LogStartupTimings)
        {
            return;
        }

        lock (Lock)
        {
            if (!Active || Completed)
            {
                return;
            }

            AddMilestone(label);
            Completed = true;
            Stopwatch.Stop();
            LogSummary("aborted");
            Active = false;
        }
    }

    private static void AddMilestone(string label)
    {
        Milestones.Add(new Milestone(label, Stopwatch.Elapsed.TotalMilliseconds));
    }

    private static void LogSummary(string result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"DataForge lobby-to-world profile {result}: total {Stopwatch.Elapsed.TotalMilliseconds:0.###} ms.");

        double previousMs = 0;
        foreach (Milestone milestone in Milestones)
        {
            builder.Append("  +");
            builder.Append(milestone.ElapsedMs.ToString("0.###"));
            builder.Append(" ms");
            builder.Append(" (delta ");
            builder.Append((milestone.ElapsedMs - previousMs).ToString("0.###"));
            builder.Append(" ms): ");
            builder.AppendLine(milestone.Label);
            previousMs = milestone.ElapsedMs;
        }

        DataForgePlugin.Log.LogInfo(builder.ToString().TrimEnd());
    }

    private readonly struct Milestone
    {
        internal Milestone(string label, double elapsedMs)
        {
            Label = label;
            ElapsedMs = elapsedMs;
        }

        internal string Label { get; }
        internal double ElapsedMs { get; }
    }
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.JoinServer))]
internal static class DataForgeFejdStartupJoinServerProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.Begin("FejdStartup.JoinServer");
    }
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnStartGame))]
internal static class DataForgeFejdStartupOnStartGameProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.Begin("FejdStartup.OnStartGame");
    }
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.TransitionToMainScene))]
internal static class DataForgeFejdStartupTransitionToMainSceneProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.Mark("FejdStartup.TransitionToMainScene");
    }
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.LoadMainScene))]
internal static class DataForgeFejdStartupLoadMainSceneProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.Mark("FejdStartup.LoadMainScene");
    }
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowConnectError))]
internal static class DataForgeFejdStartupShowConnectErrorProfilerPatch
{
    private static void Postfix()
    {
        DataForgeConnectionProfiler.Abort($"FejdStartup.ShowConnectError status={ZNet.GetConnectionStatus()}");
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.Awake))]
internal static class DataForgeGameAwakeProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.MarkOnce("Game.Awake");
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.Start))]
internal static class DataForgeGameStartProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.MarkOnce("Game.Start");
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.RequestRespawn))]
internal static class DataForgeGameRequestRespawnProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.MarkOnce("Game.RequestRespawn");
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
internal static class DataForgeGameSpawnPlayerProfilerPatch
{
    private static void Postfix()
    {
        DataForgeConnectionProfiler.Complete("Game.SpawnPlayer local player ready");
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
internal static class DataForgeZNetAwakeProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.MarkOnce("ZNet.Awake");
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
internal static class DataForgeZNetStartProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.MarkOnce("ZNet.Start");
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.ClientConnect))]
internal static class DataForgeZNetClientConnectProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.MarkOnce("ZNet.ClientConnect");
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
internal static class DataForgeZNetOnNewConnectionProfilerPatch
{
    private static void Postfix()
    {
        DataForgeConnectionProfiler.MarkOnce("ZNet.OnNewConnection");
    }
}

[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
internal static class DataForgeZNetRpcPeerInfoProfilerPatch
{
    private static void Postfix()
    {
        DataForgeConnectionProfiler.MarkOnce($"ZNet.RPC_PeerInfo status={ZNet.GetConnectionStatus()}");
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Awake))]
internal static class DataForgeZoneSystemAwakeProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.MarkOnce("ZoneSystem.Awake");
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start))]
internal static class DataForgeZoneSystemStartProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.MarkOnce("ZoneSystem.Start");
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
internal static class DataForgeObjectDbAwakeProfilerPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        DataForgeHarmonyPatchProfiler.BeginObjectDbAwake();
        DataForgeConnectionProfiler.MarkOnce("ObjectDB.Awake");
    }

    [HarmonyPriority(Priority.Last)]
    private static void Finalizer()
    {
        DataForgeHarmonyPatchProfiler.ReportObjectDbAwake();
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class DataForgeZNetSceneAwakeProfilerPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        DataForgeHarmonyPatchProfiler.InstallObjectDbAwakeProfiling();
        DataForgeConnectionProfiler.MarkOnce("ZNetScene.Awake");
    }
}

[HarmonyPatch(typeof(DungeonDB), nameof(DungeonDB.Start))]
internal static class DataForgeDungeonDbStartProfilerPatch
{
    private static void Prefix()
    {
        DataForgeConnectionProfiler.MarkOnce("DungeonDB.Start");
    }
}
