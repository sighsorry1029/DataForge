using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace DataForge;

// Cuisine owns its reload timing; DataForge owns only explicitly overridden conversions.
internal static class CuisineConversionCompatibility
{
    internal const string PluginGuid = "XutzBR.ValheimCuisine";
    private static readonly HashSet<string> Facilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "piece_cookingstation", "piece_cookingstation_iron", "piece_oven", "fermenter",
        "VC_SmokehouseWood", "VC_SmokehouseStone", "VC_DryingHook", "VC_DryingRack",
        "VC_FoodBarrel1", "VC_FoodBarrel2", "VC_LegendFermenter", "VC_DvergrCauldron"
    };
    private static DataForgeFileWatcher.DebouncedAction? PendingApply;
    private static ZNetScene? PendingScene;
    private static long Generation;
    private static bool RemotePayloadAccepted;

    internal static bool IsPayloadReady(bool localAuthority, bool receivedKey) =>
        localAuthority || (RemotePayloadAccepted && receivedKey);

    internal static void AcceptPayload(bool receivedKey)
    {
        if (DataForgeWorldLifecycle.IsShuttingDown || !receivedKey) return;
        RemotePayloadAccepted = true;
        SchedulePending();
    }

    internal static void OnAuthorityChanged() => RemotePayloadAccepted = false;

    internal static void Initialize(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return;
        try
        {
            Type? type = plugin.Instance?.GetType().Assembly.GetType(
                "ValheimCuisine.ValheimCuisinePlugin+ConversionsZNetScene_AwakePost_Patch");
            var target = type == null ? null : AccessTools.DeclaredMethod(type, "ReapplyConversions",
                new[] { typeof(ZNetScene), typeof(bool) });
            if (target == null || !target.IsStatic || target.ReturnType != typeof(void))
                throw new MissingMethodException("Cuisine ReapplyConversions(ZNetScene, bool)");

            PendingApply = DataForgeFileWatcher.CreateDebouncedAction(TimeSpan.TicksPerSecond, ApplyPending);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(CuisineConversionCompatibility), nameof(AfterReapply)));
        }
        catch (Exception ex)
        {
            Dispose();
            DataForgePlugin.Log.LogWarning($"Cuisine conversion compatibility unavailable: {ex.GetBaseException().Message}");
        }
    }

    private static void AfterReapply(ZNetScene __0)
    {
        // false skips Cuisine's world scan, not its prefab changes. Its internal catch
        // can also leave partially changed lists, so this is not a success notification.
        if (__0 == null || __0 != ZNetScene.instance) return;
        PendingScene = __0;
        Generation++;
        SchedulePending();
    }

    internal static void SchedulePending()
    {
        if (!ReferenceEquals(PendingScene, null)) PendingApply?.Schedule();
    }

    private static void ApplyPending()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown || PendingScene == null ||
            PendingScene != ZNetScene.instance) return;
        long generation = Generation;
        try
        {
            // A not-yet-ready request is retained. Existing data/config lifecycle
            // callbacks wake it; no polling or repeated failed prefab lookups.
            if (PieceOverrideManager.TryReapplyCuisineConversions(Facilities) && generation == Generation)
                PendingScene = null;
        }
        catch (Exception ex)
        {
            if (generation == Generation) PendingScene = null;
            DataForgePlugin.Log.LogWarning($"Could not restore DataForge conversions after Cuisine: {ex}");
        }
    }

    internal static void OnWorldShutdown()
    {
        RemotePayloadAccepted = false;
        Generation++;
        PendingScene = null;
        if (PendingApply == null) return;
        PendingApply.Dispose();
        PendingApply = DataForgeFileWatcher.CreateDebouncedAction(TimeSpan.TicksPerSecond, ApplyPending);
    }

    internal static void Dispose()
    {
        RemotePayloadAccepted = false;
        Generation++;
        PendingScene = null;
        PendingApply?.Dispose();
        PendingApply = null;
    }
}
