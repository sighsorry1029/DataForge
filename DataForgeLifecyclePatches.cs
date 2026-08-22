using HarmonyLib;

namespace DataForge;

internal static class DataForgeWorldLifecycle
{
    internal static bool IsShuttingDown { get; private set; }
    internal static bool IsGameStarted { get; private set; }

    internal static bool MarkStarting()
    {
        bool wasShuttingDown = IsShuttingDown;
        IsShuttingDown = false;
        if (wasShuttingDown)
        {
            IsGameStarted = false;
        }

        return wasShuttingDown;
    }

    internal static void MarkGameStarted()
    {
        if (!IsShuttingDown)
        {
            IsGameStarted = true;
        }
    }

    internal static void MarkShuttingDown()
    {
        IsShuttingDown = true;
        IsGameStarted = false;
    }
}

internal static class DataForgeLifecycleStep
{
    internal static bool Run(string name, System.Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (System.Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"DataForge {name} failed: {ex}");
            return false;
        }
    }
}

internal static class DataForgeRuntimeCleanup
{
    private static bool CleanupCompleted;
    private static bool CleanupRunning;

    internal static bool RunOnce()
    {
        if (CleanupCompleted || CleanupRunning)
        {
            return CleanupCompleted;
        }

        CleanupRunning = true;
        try
        {
            DataForgeWorldLifecycle.MarkShuttingDown();
            bool cleanupSucceeded = true;
            cleanupSucceeded &= DataForgeLifecycleStep.Run(
                "VNEI refresh cleanup",
                VneiRefreshManager.OnWorldShutdown);
            cleanupSucceeded &= DataForgeLifecycleStep.Run(
                "recipe cleanup",
                RecipeOverrideManager.OnWorldShutdown);
            bool statusEffectCleanupSucceeded = DataForgeLifecycleStep.Run(
                "status-effect cleanup",
                StatusEffectOverrideManager.OnWorldShutdown);
            cleanupSucceeded &= statusEffectCleanupSucceeded;
            bool itemCleanupSucceeded = DataForgeLifecycleStep.Run(
                "item cleanup",
                ItemOverrideManager.OnWorldShutdown);
            cleanupSucceeded &= itemCleanupSucceeded;
            bool pieceCleanupResult = false;
            bool pieceCleanupCompleted = DataForgeLifecycleStep.Run(
                "piece cleanup",
                () => pieceCleanupResult = PieceOverrideManager.OnWorldShutdown());
            bool pieceCleanupSucceeded = pieceCleanupCompleted && pieceCleanupResult;
            cleanupSucceeded &= pieceCleanupSucceeded;
            cleanupSucceeded &= DataForgeLifecycleStep.Run(
                "localization cleanup",
                LocalizationOverrideManager.OnWorldShutdown);
            cleanupSucceeded &= DataForgeLifecycleStep.Run(
                "icon sync cleanup",
                DataForgeIconSync.OnWorldShutdown);
            bool releaseIconResources =
                statusEffectCleanupSucceeded &&
                itemCleanupSucceeded &&
                pieceCleanupSucceeded;
            cleanupSucceeded &= DataForgeLifecycleStep.Run(
                "item-visual cleanup",
                () => ItemVisualOverrides.ResetWorldState(releaseIconResources));
            CleanupCompleted = cleanupSucceeded;
            return cleanupSucceeded;
        }
        finally
        {
            CleanupRunning = false;
        }
    }

    internal static void PrepareForNewWorld()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown && !CleanupCompleted)
        {
            RunOnce();
        }

        CleanupCompleted = false;
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
internal static class DataForgeObjectDBAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        NotifyObjectDBReady(writeGeneratedArtifacts: true);
    }

    internal static void NotifyObjectDBReady(bool writeGeneratedArtifacts)
    {
        if (DataForgeWorldLifecycle.IsShuttingDown)
        {
            return;
        }

        DataForgeLifecycleStep.Run(
            "ObjectDB source-of-truth setup",
            DataForgePlugin.EnsureSourceOfTruthFileMode);
        DataForgeLifecycleStep.Run(
            "status-effect ObjectDB setup",
            () => StatusEffectOverrideManager.OnObjectDBReady(writeGeneratedArtifacts));
        DataForgeLifecycleStep.Run(
            "item ObjectDB setup",
            () => ItemOverrideManager.OnObjectDBReady(writeGeneratedArtifacts));
        DataForgeLifecycleStep.Run(
            "recipe ObjectDB setup",
            () => RecipeOverrideManager.OnObjectDBReady(writeGeneratedArtifacts));
        DataForgeLifecycleStep.Run(
            "piece ObjectDB setup",
            () => PieceOverrideManager.OnObjectDBReady(writeGeneratedArtifacts));
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class DataForgeObjectDBCopyOtherDBPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        DataForgeObjectDBAwakePatch.NotifyObjectDBReady(
            writeGeneratedArtifacts: DataForgeWorldLifecycle.IsGameStarted);
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.UpdateRegisters))]
internal static class DataForgeObjectDBUpdateRegistersPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ObjectDB __instance)
    {
        DataForgeLifecycleStep.Run(
            "recipe registry reconciliation",
            () => RecipeOverrideManager.OnObjectDBRegistersUpdated(__instance));
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class DataForgeZNetSceneAwakePatch
{
    private static void Postfix()
    {
        DataForgeRuntimeCleanup.PrepareForNewWorld();
        bool startingAfterShutdown = DataForgeWorldLifecycle.MarkStarting();
        if (startingAfterShutdown)
        {
            DataForgeLifecycleStep.Run(
                "new-world VNEI invalidation",
                VneiRefreshManager.InvalidateForNewWorld);
        }

        if (startingAfterShutdown && ObjectDB.instance != null)
        {
            DataForgeLifecycleStep.Run(
                "new-world ObjectDB setup",
                () => DataForgeObjectDBAwakePatch.NotifyObjectDBReady(writeGeneratedArtifacts: false));
        }

        DataForgeLifecycleStep.Run(
            "ZNetScene source-of-truth setup",
            DataForgePlugin.EnsureSourceOfTruthFileMode);
        DataForgeLifecycleStep.Run(
            "recipe ZNetScene setup",
            RecipeOverrideManager.OnZNetSceneReady);
        DataForgeLifecycleStep.Run(
            "status-effect ZNetScene setup",
            StatusEffectOverrideManager.OnZNetSceneReady);
        DataForgeLifecycleStep.Run(
            "item ZNetScene setup",
            ItemOverrideManager.OnZNetSceneReady);
        DataForgeLifecycleStep.Run(
            "piece game-data setup",
            PieceOverrideManager.OnGameDataReady);
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class DataForgeMaterialReferenceZNetSceneAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown)
        {
            return;
        }

        DataForgeLifecycleStep.Run(
            "material-reference ZNetScene setup",
            MaterialReferenceWriter.WriteReferenceIfReady);
    }
}

[HarmonyPatch(typeof(DungeonDB), nameof(DungeonDB.Start))]
internal static class DataForgePieceDungeonDbStartPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown)
        {
            return;
        }

        DataForgeLifecycleStep.Run(
            "piece-table DungeonDB setup",
            PieceOverrideManager.OnPieceTablesReady);
        DataForgeLifecycleStep.Run(
            "recipe DungeonDB apply",
            RecipeOverrideManager.ApplyCurrentConfiguration);
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.Start))]
internal static class DataForgeGameStartPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown)
        {
            return;
        }

        DataForgeWorldLifecycle.MarkGameStarted();
        DataForgeLifecycleStep.Run(
            "localization game-start apply",
            LocalizationOverrideManager.ApplyCurrentLocalization);
        DataForgeLifecycleStep.Run(
            "status-effect game-start apply",
            StatusEffectOverrideManager.ApplyCurrentConfiguration);
        DataForgeLifecycleStep.Run(
            "item game-start apply",
            ItemOverrideManager.ApplyCurrentConfiguration);
        DataForgeLifecycleStep.Run(
            "piece game-start apply",
            PieceOverrideManager.ApplyCurrentConfiguration);
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
internal static class DataForgeZNetShutdownCleanupPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        DataForgeRuntimeCleanup.RunOnce();
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
internal static class DataForgeIconSyncConnectionPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ZNet __instance, ZNetPeer peer)
    {
        DataForgeLifecycleStep.Run(
            "icon sync connection setup",
            () => DataForgeIconSync.OnNewConnection(__instance, peer));
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
internal static class DataForgeIconSyncDisconnectPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(ZNetPeer peer)
    {
        DataForgeIconSync.OnPeerDisconnected(peer);
    }
}
