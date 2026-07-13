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

        DataForgePlugin.EnsureSourceOfTruthFileMode();
        StatusEffectOverrideManager.OnObjectDBReady(writeGeneratedArtifacts);
        ItemOverrideManager.OnObjectDBReady(writeGeneratedArtifacts);
        RecipeOverrideManager.OnObjectDBReady(writeGeneratedArtifacts);
        PieceOverrideManager.OnObjectDBReady(writeGeneratedArtifacts);
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

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class DataForgeZNetSceneAwakePatch
{
    private static void Postfix()
    {
        if (DataForgeWorldLifecycle.MarkStarting() && ObjectDB.instance != null)
        {
            DataForgeObjectDBAwakePatch.NotifyObjectDBReady(writeGeneratedArtifacts: false);
        }

        DataForgePlugin.EnsureSourceOfTruthFileMode();
        RecipeOverrideManager.OnZNetSceneReady();
        StatusEffectOverrideManager.OnZNetSceneReady();
        ItemOverrideManager.OnZNetSceneReady();
        PieceOverrideManager.OnGameDataReady();
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

        MaterialReferenceWriter.WriteReferenceIfReady();
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

        PieceOverrideManager.OnPieceTablesReady();
        RecipeOverrideManager.ApplyCurrentConfiguration();
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
        LocalizationOverrideManager.ApplyCurrentLocalization();
        StatusEffectOverrideManager.ApplyCurrentConfiguration();
        ItemOverrideManager.ApplyCurrentConfiguration();
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
internal static class DataForgeZNetShutdownCleanupPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        DataForgeWorldLifecycle.MarkShuttingDown();
        RunCleanup("recipes", RecipeOverrideManager.OnWorldShutdown);
        RunCleanup("status effects", StatusEffectOverrideManager.OnWorldShutdown);
        RunCleanup("items", ItemOverrideManager.OnWorldShutdown);
        RunCleanup("pieces", PieceOverrideManager.OnWorldShutdown);
        RunCleanup("localization", LocalizationOverrideManager.OnWorldShutdown);
        RunCleanup("item visuals", ItemVisualOverrides.ResetWorldState);
    }

    private static void RunCleanup(string name, System.Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (System.Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"Failed to clean up DataForge {name} during world shutdown: {ex}");
        }
    }
}
