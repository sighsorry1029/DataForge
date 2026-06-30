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
        NotifyObjectDBReady();
    }

    internal static void NotifyObjectDBReady()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown)
        {
            return;
        }

        DataForgeProfiler.Profile("EnsureSourceOfTruthFileMode/ObjectDB", DataForgePlugin.EnsureSourceOfTruthFileMode);
        DataForgeProfiler.Profile("effects.OnObjectDBReady", StatusEffectOverrideManager.OnObjectDBReady);
        DataForgeProfiler.Profile("items.OnObjectDBReady", ItemOverrideManager.OnObjectDBReady);
        DataForgeProfiler.Profile("recipes.OnObjectDBReady", RecipeOverrideManager.OnObjectDBReady);
        DataForgeProfiler.Profile("pieces.OnObjectDBReady", PieceOverrideManager.OnObjectDBReady);
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class DataForgeObjectDBCopyOtherDBPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown)
        {
            return;
        }

        DataForgeObjectDBAwakePatch.NotifyObjectDBReady();
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class DataForgeRecipeZNetSceneAwakePatch
{
    private static void Postfix()
    {
        if (DataForgeWorldLifecycle.MarkStarting() && ObjectDB.instance != null)
        {
            DataForgeObjectDBAwakePatch.NotifyObjectDBReady();
        }

        DataForgeProfiler.Profile("EnsureSourceOfTruthFileMode/ZNetScene(recipes)", DataForgePlugin.EnsureSourceOfTruthFileMode);
        DataForgeProfiler.Profile("recipes.OnZNetSceneReady", RecipeOverrideManager.OnZNetSceneReady);
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class DataForgePieceZNetSceneAwakePatch
{
    private static void Postfix()
    {
        DataForgeWorldLifecycle.MarkStarting();
        DataForgeProfiler.Profile("EnsureSourceOfTruthFileMode/ZNetScene(domains)", DataForgePlugin.EnsureSourceOfTruthFileMode);
        DataForgeProfiler.Profile("effects.OnZNetSceneReady", StatusEffectOverrideManager.OnZNetSceneReady);
        DataForgeProfiler.Profile("items.OnZNetSceneReady", ItemOverrideManager.OnZNetSceneReady);
        DataForgeProfiler.Profile("pieces.OnGameDataReady", PieceOverrideManager.OnGameDataReady);
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

        DataForgeProfiler.Profile("materials.WriteReferenceIfReady", MaterialReferenceWriter.WriteReferenceIfReady);
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

        DataForgeProfiler.Profile("pieces.OnPieceTablesReady", PieceOverrideManager.OnPieceTablesReady);
        DataForgeProfiler.Profile("recipes.OnPieceTablesReady", RecipeOverrideManager.ApplyCurrentConfiguration);
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
        DataForgeProfiler.Profile("effects.OnGameReady", StatusEffectOverrideManager.ApplyCurrentConfiguration);
        DataForgeProfiler.Profile("items.OnGameReady", ItemOverrideManager.ApplyCurrentConfiguration);
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
internal static class DataForgeZNetShutdownCleanupPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        try
        {
            DataForgeWorldLifecycle.MarkShuttingDown();
            RecipeOverrideManager.OnWorldShutdown();
            StatusEffectOverrideManager.OnWorldShutdown();
            ItemOverrideManager.OnWorldShutdown();
            PieceOverrideManager.OnWorldShutdown();
        }
        catch (System.Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"Failed to clean up DataForge-created clones during world shutdown: {ex}");
        }
    }
}
