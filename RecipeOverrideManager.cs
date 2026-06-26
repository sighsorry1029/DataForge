using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using ServerSync;
using UnityEngine;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static DataForge.DataForgeValue;

namespace DataForge;

internal static class RecipeOverrideManager
{
    private const string DomainName = "recipes";
    private const string OverrideFileName = "recipes.yml";
    private const string ReferenceFileName = "recipes.reference.yml";
    private const string FullScaffoldFileName = "recipes.full.yml";
    private const string SyncedPayloadKey = "recipes";
    private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;
    private const string ReferenceStateKey = "recipes";
    private const string ReferenceLogicVersion = "2026-06-24-recipe-reference-state-v2";

    private static readonly object StateLock = new();
    private static readonly Dictionary<string, RecipeDefinition> Baselines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Recipe> BaselineRecipes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CreatedRecipes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RuntimeAppliedRecipeKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<QualityBonusRule>> ActiveQualityBonuses = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<QualityBonusRule> EmptyQualityBonusRules = new();
    private static Dictionary<string, List<Recipe>>? RecipeLookupCache;
    private static readonly MethodInfo? UpdateKnownRecipesListMethod =
        AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
    private static readonly MethodInfo? InventoryGuiUpdateRecipeMethod =
        AccessTools.Method(typeof(InventoryGui), "UpdateRecipe");
    private static readonly MethodInfo? InventoryGuiUpdateCraftingPanelMethod =
        AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel");
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new RequirementDefinitionYamlConverter())
        .WithTypeConverter(new QualityBonusDefinitionYamlConverter())
        .Build();
    private static readonly ISerializer SparseSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new RequirementDefinitionYamlConverter())
        .WithTypeConverter(new QualityBonusDefinitionYamlConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases()
        .Build();
    private static readonly ISerializer FullSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new RequirementDefinitionYamlConverter())
        .WithTypeConverter(new QualityBonusDefinitionYamlConverter())
        .DisableAliases()
        .Build();

    private static List<RecipeEntry> ActiveEntries = new();
    private static CustomSyncedValue<string>? SyncedPayload;
    private static string? LastAppliedSyncedPayload;
    private static FileSystemWatcher? Watcher;
    private static DataForgeFileWatcher.DebouncedAction? ReloadDebouncer;
    private static bool ObjectDbReady;
    private static bool ZNetSceneReady;
    private static int ActiveQualityBonusRecipeCount;
    private static bool RecipeLookupCacheDirty = true;
    private static bool RuntimeStateWasApplied;
    private static Dictionary<string, string> ActiveEntrySignaturesByRecipe = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string>? PendingChangedRecipeKeys;
    private static bool HasPendingScopedApply;
    private static bool ForceNextFullApply = true;

    private static string ConfigDirectory => Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName);

    internal static void Initialize(ConfigSync configSync)
    {
        SyncedPayload = new CustomSyncedValue<string>(configSync, SyncedPayloadKey, "");
        SyncedPayload.ValueChanged += OnSyncedPayloadChanged;
    }

    internal static void Dispose()
    {
        if (SyncedPayload != null)
        {
            SyncedPayload.ValueChanged -= OnSyncedPayloadChanged;
        }

        Watcher?.Dispose();
        Watcher = null;
        ReloadDebouncer?.Dispose();
        ReloadDebouncer = null;
    }

    internal static void SetupFileWatcher()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles)
        {
            Watcher?.Dispose();
            Watcher = null;
            ReloadDebouncer?.Dispose();
            ReloadDebouncer = null;
            return;
        }

        EnsureConfigDirectoryAndDefaultOverride();
        Watcher?.Dispose();
        ReloadDebouncer?.Dispose();
        ReloadDebouncer = DataForgeFileWatcher.CreateDebouncedAction(ReloadDelayTicks, ReloadYamlValues);
        Watcher = DataForgeFileWatcher.Create(ConfigDirectory, "*.*", includeSubdirectories: false, ReadYamlValues);
    }

    internal static void ReloadFromDiskAndSync()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles)
        {
            ApplySyncedPayload(SyncedPayload?.Value ?? "");
            return;
        }

        EnsureConfigDirectoryAndDefaultOverride();
        List<RecipeEntry> entries = LoadEntriesFromDisk();
        lock (StateLock)
        {
            SetActiveEntries(entries);
        }

        PublishPayload(SerializeEntries(entries));
        ApplyCurrentConfiguration();
    }

    internal static void OnObjectDBReady()
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        ObjectDbReady = true;
        if (ShouldSkipRemoteClientBaselineWork())
        {
            return;
        }

        WriteGeneratedArtifacts();
        ApplyCurrentConfiguration();
    }

    internal static void OnZNetSceneReady()
    {
        if (ZNetScene.instance == null)
        {
            return;
        }

        ZNetSceneReady = true;
        if (ShouldSkipRemoteClientBaselineWork())
        {
            return;
        }

        ApplyCurrentConfiguration();
        WriteGeneratedArtifacts();
    }

    internal static void ApplyCurrentConfiguration()
    {
        if (!ObjectDbReady || ObjectDB.instance == null || !ZNetSceneReady || ZNetScene.instance == null)
        {
            return;
        }

        if (ShouldSkipRemoteClientBaselineWork())
        {
            return;
        }

        InvalidateRecipeLookupCache();

        List<RecipeEntry> entries;
        HashSet<string>? changedRecipeKeys;
        lock (StateLock)
        {
            entries = ActiveEntries.ToList();
            changedRecipeKeys = ConsumePendingChangedRecipeKeys();
        }

        if (changedRecipeKeys is { Count: 0 })
        {
            return;
        }

        List<RecipeEntry> entriesToApply = FilterEntries(entries, changedRecipeKeys);
        CaptureBaselinesForEntriesIfNeeded(entriesToApply);
        HashSet<string> runtimeRecipeKeys = GetRuntimeApplyRecipeKeys(entriesToApply);
        HashSet<string> cleanedCreatedRecipes = CleanupCreatedRecipes(entries);
        EnsureAddedRecipes(entries);
        RestoreBaselineRecipes(runtimeRecipeKeys);
        ClearActiveQualityBonuses();

        if (!DataForgePlugin.RecipeOverridesEnabled)
        {
            RefreshLiveRecipeState();
            UpdateRuntimeAppliedRecipeState(new List<RecipeEntry>());
            VneiRefreshManager.RequestRefresh(DomainName);
            return;
        }

        foreach (RecipeEntry entry in entriesToApply)
        {
            using (DataForgeLogContext.Push(entry.LogContext))
            {
                string recipeName = ToRecipeName(entry.Recipe);
                if (!entry.Override)
                {
                    continue;
                }

                if (entry.Remove)
                {
                    RemoveRecipe(entry.Recipe, warnIfMissing: !cleanedCreatedRecipes.Contains(ToRecipeName(entry.Recipe)));
                    continue;
                }

                Recipe? recipe = ResolveRecipe(entry.Recipe);
                if (recipe == null)
                {
                    DataForgeLogContext.Warning($"Could not find recipe '{recipeName}'.");
                    continue;
                }

                if (entry.HasDefinition)
                {
                    ApplyDefinition(recipe, RecipeDefinition.From(entry));
                    ApplyQualityBonuses(recipe, entry.QualityBonus);
                }
            }
        }

        RefreshLiveRecipeState();
        UpdateRuntimeAppliedRecipeState(entries);
        VneiRefreshManager.RequestRefresh(DomainName);
    }

    private static void RefreshLiveRecipeState()
    {
        RefreshKnownRecipes();
        RefreshInventoryGuiRecipes();
    }

    private static bool ShouldSkipRemoteClientBaselineWork()
    {
        if (!DataForgePlugin.IsRemoteServerClient)
        {
            return false;
        }

        lock (StateLock)
        {
            return ActiveEntries.Count == 0 && CreatedRecipes.Count == 0 && ActiveQualityBonusRecipeCount == 0;
        }
    }

    private static void RefreshKnownRecipes()
    {
        if (UpdateKnownRecipesListMethod == null || Player.s_players == null)
        {
            return;
        }

        foreach (Player player in Player.s_players)
        {
            if (player == null)
            {
                continue;
            }

            try
            {
                UpdateKnownRecipesListMethod.Invoke(player, null);
            }
            catch (Exception ex)
            {
                DataForgePlugin.Log.LogDebug($"Could not refresh known recipes after recipe update: {ex.Message}");
            }
        }
    }

    private static void RefreshInventoryGuiRecipes()
    {
        InventoryGui gui = InventoryGui.instance;
        if (gui == null)
        {
            return;
        }

        InvokeInventoryGuiRefresh(gui, InventoryGuiUpdateRecipeMethod, "UpdateRecipe");
        InvokeInventoryGuiRefresh(gui, InventoryGuiUpdateCraftingPanelMethod, "UpdateCraftingPanel");
    }

    private static void InvokeInventoryGuiRefresh(InventoryGui gui, MethodInfo? method, string methodName)
    {
        if (method == null)
        {
            return;
        }

        try
        {
            method.Invoke(gui, null);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not refresh InventoryGui.{methodName} after recipe update: {ex.Message}");
        }
    }

    private static void ReadYamlValues(object sender, FileSystemEventArgs e)
    {
        if (!ShouldReloadForFileEvent(e))
        {
            return;
        }

        ReloadDebouncer?.Schedule();
    }

    private static void ReloadYamlValues()
    {
        try
        {
            DataForgePlugin.Log.LogDebug("Reloading recipe YAML files...");
            ReloadFromDiskAndSync();
            DataForgePlugin.Log.LogInfo("Recipe YAML reload complete.");
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Error reloading recipe YAML files: {ex}");
        }
    }

    private static bool ShouldReloadForFileEvent(FileSystemEventArgs e)
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles)
        {
            return false;
        }

        if (IsOverrideFile(e.FullPath))
        {
            return true;
        }

        return e is RenamedEventArgs renamed && IsOverrideFile(renamed.OldFullPath);
    }

    private static void OnSyncedPayloadChanged()
    {
        if (DataForgePlugin.UsesLocalAuthorityFiles)
        {
            return;
        }

        string payload = SyncedPayload?.Value ?? "";
        DataForgeProfiler.Profile($"{DomainName}.ApplySyncedPayload chars={payload.Length}", () => ApplySyncedPayload(payload));
    }

    private static void ApplySyncedPayload(string payload)
    {
        if (string.Equals(LastAppliedSyncedPayload, payload, StringComparison.Ordinal))
        {
            return;
        }

        LastAppliedSyncedPayload = payload;
        List<RecipeEntry> entries = DeserializeEntries(payload, "synced recipe payload");
        lock (StateLock)
        {
            SetActiveEntries(entries);
        }

        ApplyCurrentConfiguration();
    }

    private static void SetActiveEntries(List<RecipeEntry> entries)
    {
        Dictionary<string, string> signatures = BuildEntrySignaturesByRecipe(entries);
        if (!ForceNextFullApply)
        {
            PendingChangedRecipeKeys = GetChangedKeys(ActiveEntrySignaturesByRecipe, signatures);
            HasPendingScopedApply = true;
        }

        ActiveEntries = entries;
        ActiveEntrySignaturesByRecipe = signatures;
    }

    private static HashSet<string>? ConsumePendingChangedRecipeKeys()
    {
        if (ForceNextFullApply)
        {
            ForceNextFullApply = false;
            PendingChangedRecipeKeys = null;
            HasPendingScopedApply = false;
            return null;
        }

        if (!HasPendingScopedApply)
        {
            return null;
        }

        HashSet<string> changedKeys = PendingChangedRecipeKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PendingChangedRecipeKeys = null;
        HasPendingScopedApply = false;
        return changedKeys;
    }

    private static Dictionary<string, string> BuildEntrySignaturesByRecipe(List<RecipeEntry> entries)
    {
        Dictionary<string, List<RecipeEntry>> entriesByRecipe = new(StringComparer.OrdinalIgnoreCase);
        foreach (RecipeEntry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Recipe))
            {
                continue;
            }

            if (!entriesByRecipe.TryGetValue(entry.Recipe, out List<RecipeEntry> recipeEntries))
            {
                recipeEntries = new List<RecipeEntry>();
                entriesByRecipe[entry.Recipe] = recipeEntries;
            }

            recipeEntries.Add(entry);
        }

        Dictionary<string, string> signatures = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<RecipeEntry>> pair in entriesByRecipe)
        {
            signatures[pair.Key] = SparseSerializer.Serialize(pair.Value);
        }

        return signatures;
    }

    private static HashSet<string> GetChangedKeys(Dictionary<string, string> oldSignatures, Dictionary<string, string> newSignatures)
    {
        HashSet<string> changedKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in oldSignatures)
        {
            if (!newSignatures.TryGetValue(pair.Key, out string newSignature) ||
                !string.Equals(pair.Value, newSignature, StringComparison.Ordinal))
            {
                changedKeys.Add(pair.Key);
            }
        }

        foreach (KeyValuePair<string, string> pair in newSignatures)
        {
            if (!oldSignatures.TryGetValue(pair.Key, out string oldSignature) ||
                !string.Equals(oldSignature, pair.Value, StringComparison.Ordinal))
            {
                changedKeys.Add(pair.Key);
            }
        }

        return changedKeys;
    }

    private static List<RecipeEntry> FilterEntries(List<RecipeEntry> entries, HashSet<string>? recipeKeys)
    {
        return recipeKeys == null
            ? entries
            : entries.Where(entry => recipeKeys.Contains(entry.Recipe)).ToList();
    }

    private static void PublishPayload(string payload)
    {
        DataForgeSync.PublishPayload(SyncedPayload, DomainName, payload);
    }

    private static List<RecipeEntry> LoadEntriesFromDisk()
    {
        return DataForgeOverrideFiles.LoadEntries(GetOverrideFiles(), DeserializeEntries);
    }

    private static List<RecipeEntry> DeserializeEntries(string yaml, string source)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new List<RecipeEntry>();
        }

        try
        {
            List<RecipeEntry>? entries = Deserializer.Deserialize<List<RecipeEntry>>(yaml);
            return NormalizeEntries(entries, source);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Failed to parse {source}: {ex.Message}");
            return new List<RecipeEntry>();
        }
    }

    private static List<RecipeEntry> NormalizeEntries(List<RecipeEntry>? entries, string source)
    {
        List<RecipeEntry> normalized = new();
        if (entries == null)
        {
            return normalized;
        }

        int entryIndex = 0;
        foreach (RecipeEntry entry in entries)
        {
            entryIndex++;
            string sourceContext = DataForgeLogContext.FormatSource(source, entryIndex);
            if (string.IsNullOrWhiteSpace(entry.Recipe))
            {
                DataForgeLogContext.Warning($"{sourceContext}: Skipping recipe entry without recipe.");
                continue;
            }

            using (DataForgeLogContext.Push(sourceContext))
            {
                if (!TryNormalizeRecipeHeader(entry.Recipe, out string normalizedRecipe, out string error))
                {
                    DataForgeLogContext.Warning($"{sourceContext}: Skipping recipe entry '{entry.Recipe}'. {error}");
                    continue;
                }

                entry.Recipe = normalizedRecipe;
            }
            entry.SetLogContext($"{sourceContext} recipe={ToRecipeKey(entry.Recipe)}");
            normalized.Add(entry);
        }

        return normalized;
    }

    private static string SerializeEntries(List<RecipeEntry> entries)
    {
        return SparseSerializer.Serialize(entries);
    }

    private static IEnumerable<string> GetOverrideFiles()
    {
        return DataForgeOverrideFiles.GetOverrideFiles(ConfigDirectory, IsOverrideFile);
    }

    private static bool IsOverrideFile(string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = Path.GetFileName(path);
        if (fileName.Equals(ReferenceFileName, StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(FullScaffoldFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fileName.Equals(OverrideFileName, StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("recipes_", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureConfigDirectoryAndDefaultOverride()
    {
        DataForgeOverrideFiles.EnsureDefaultOverride(ConfigDirectory, OverrideFileName, GetOverrideFiles, DefaultOverrideTemplate);
    }

    private static string DefaultOverrideTemplate()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# DataForge recipe overrides.",
            "# Copy entries from recipes.reference.yml, or run `dataforge:full recipe` to generate recipes.full.yml for exhaustive field examples.",
            "# You can also create additional override files like recipes_asdf.yml; DataForge loads recipes.yml and recipes_*.yml together.",
            "# Omitted fields keep the current recipe value. Values below are common defaults or examples.",
            "#",
            "# Schema:",
            "# - recipe: SwordIron, 1                   # result item prefab; use SwordIron;1 / SwordIron;2 when reference lists multiple recipes. Custom additions can use SwordIron;myVariant.",
            "#                                          # Variant ids after ';' should be one word; use letters, numbers, '_' or '-' rather than spaces.",
            "#   override: true                        # default true; false skips this entire entry, including remove.",
            "#   remove: false                         # default false; true removes this recipe from ObjectDB.m_recipes.",
            "#   craftingStation: forge, 2              # station prefab and optional min station level. Use none for hand craft.",
            "#   requireOnlyOneIngredient: false, 1     # true, 1 => any one listed ingredient can craft; selected ingredient quality increases output by ceil((quality - 1) * amount * 1). If false, the multiplier is effectively unused.",
            "#   listSortWeight: 100                    # UI sort weight.",
            "#   resources:",
            "#   - Iron: 20, 10, 0                      # shorthand: itemPrefab: amount, upgradeAmount, extraAmountOnlyOneIngredient. Reference only shows upgradeAmount when the result item has maxQuality > 1.",
            "#   - Wood: 5                              # shorthand: itemPrefab: amount.",
            "#   qualityBonus:",
            "#   - Fish1: 1                             # DataForge extension: if this resource is consumed at quality 3, add ceil((3 - 1) * 1) result items per craft.",
            "#",
            "# Example:",
            "# - recipe: SwordIron, 1",
            "#   craftingStation: forge, 2",
            "#   resources:",
            "#   - Iron: 20, 10",
            "#   - Wood: 5"
        }) + Environment.NewLine;
    }

    private static void CaptureAllBaselinesIfNeeded()
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        int added = 0;
        foreach (Recipe recipe in ObjectDB.instance.m_recipes)
        {
            if (CaptureBaseline(recipe))
            {
                added++;
            }
        }

        if (added > 0)
        {
            DataForgePlugin.Log.LogInfo($"Captured {added} new recipe baselines. Tracking {Baselines.Count} total.");
        }
    }

    private static void CaptureBaselinesForEntriesIfNeeded(List<RecipeEntry> entries)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        int added = 0;
        foreach (RecipeEntry entry in entries)
        {
            if (!entry.Override || string.IsNullOrWhiteSpace(entry.Recipe))
            {
                continue;
            }

            Recipe? recipe = ResolveRecipe(entry.Recipe);
            if (CaptureBaseline(recipe))
            {
                added++;
            }
        }

        if (added > 0)
        {
            DataForgePlugin.Log.LogInfo($"Captured {added} targeted recipe baselines. Tracking {Baselines.Count} total.");
        }
    }

    private static bool CaptureBaseline(Recipe? recipe)
    {
        if (ObjectDB.instance == null ||
            recipe == null ||
            !recipe.m_enabled ||
            string.IsNullOrWhiteSpace(recipe.name))
        {
            return false;
        }

        if (Baselines.ContainsKey(recipe.name))
        {
            if (BaselineRecipes.TryGetValue(recipe.name, out Recipe? baselineRecipe) &&
                !ReferenceEquals(baselineRecipe, recipe) &&
                !ObjectDB.instance.m_recipes.Contains(baselineRecipe))
            {
                Baselines[recipe.name] = RecipeDefinition.From(recipe);
                BaselineRecipes[recipe.name] = recipe;
            }

            return false;
        }

        Baselines[recipe.name] = RecipeDefinition.From(recipe);
        BaselineRecipes[recipe.name] = recipe;
        return true;
    }

    private static HashSet<string> GetRuntimeApplyRecipeKeys(List<RecipeEntry> entries)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (RecipeEntry entry in entries)
        {
            if (!entry.Override || string.IsNullOrWhiteSpace(entry.Recipe))
            {
                continue;
            }

            Recipe? recipe = ResolveRecipe(entry.Recipe);
            if (recipe != null && !string.IsNullOrWhiteSpace(recipe.name))
            {
                keys.Add(recipe.name);
                continue;
            }

            string recipeName = ToRecipeName(entry.Recipe);
            if (recipeName.Length > 0)
            {
                keys.Add(recipeName);
            }
        }

        if (RuntimeStateWasApplied)
        {
            foreach (string key in RuntimeAppliedRecipeKeys)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static void UpdateRuntimeAppliedRecipeState(List<RecipeEntry> entries)
    {
        RuntimeAppliedRecipeKeys.Clear();
        foreach (RecipeEntry entry in entries)
        {
            if (!entry.Override || string.IsNullOrWhiteSpace(entry.Recipe))
            {
                continue;
            }

            Recipe? recipe = ResolveRecipe(entry.Recipe);
            if (recipe != null && !string.IsNullOrWhiteSpace(recipe.name))
            {
                RuntimeAppliedRecipeKeys.Add(recipe.name);
                continue;
            }

            string recipeName = ToRecipeName(entry.Recipe);
            if (recipeName.Length > 0)
            {
                RuntimeAppliedRecipeKeys.Add(recipeName);
            }
        }

        RuntimeStateWasApplied = RuntimeAppliedRecipeKeys.Count > 0;
    }

    private static HashSet<string> CleanupCreatedRecipes(List<RecipeEntry> entries)
    {
        HashSet<string> removedCreatedNames = new(StringComparer.OrdinalIgnoreCase);
        if (ObjectDB.instance == null)
        {
            return removedCreatedNames;
        }

        HashSet<string> activeCreatedNames = new(
            entries
                .Where(entry => entry.Override && !entry.Remove && ShouldCreateRecipe(entry))
                .Select(entry => ToRecipeName(entry.Recipe)),
            StringComparer.OrdinalIgnoreCase);

        foreach (string recipeName in CreatedRecipes.ToList())
        {
            if (activeCreatedNames.Contains(recipeName))
            {
                continue;
            }

            RemoveCreatedRecipe(recipeName, destroy: false);
            removedCreatedNames.Add(recipeName);
        }

        return removedCreatedNames;
    }

    internal static void CleanupCreatedRecipesForWorldTransition()
    {
        if (ObjectDB.instance == null)
        {
            CreatedRecipes.Clear();
            return;
        }

        foreach (string recipeName in CreatedRecipes.ToList())
        {
            RemoveCreatedRecipe(recipeName, destroy: true);
        }
    }

    internal static void OnWorldShutdown()
    {
        ObjectDbReady = false;
        ZNetSceneReady = false;
        RuntimeStateWasApplied = false;
        RuntimeAppliedRecipeKeys.Clear();
        CleanupCreatedRecipesForWorldTransition();
    }

    private static void RemoveCreatedRecipe(string recipeName, bool destroy)
    {
        Recipe? recipe = FindRecipeByExactName(recipeName) ?? ResolveRecipe(recipeName);
        if (recipe != null && ObjectDB.instance != null)
        {
            ObjectDB.instance.m_recipes.Remove(recipe);
            InvalidateRecipeLookupCache();
            if (destroy)
            {
                UnityEngine.Object.Destroy(recipe);
            }
        }

        CreatedRecipes.Remove(recipeName);
        Baselines.Remove(recipeName);
        BaselineRecipes.Remove(recipeName);
    }

    private static void EnsureAddedRecipes(List<RecipeEntry> entries)
    {
        foreach (RecipeEntry entry in entries)
        {
            if (!entry.Override || entry.Remove || !ShouldCreateRecipe(entry))
            {
                continue;
            }

            using (DataForgeLogContext.Push(entry.LogContext))
            {
                EnsureAddedRecipe(entry);
            }
        }
    }

    private static void EnsureAddedRecipe(RecipeEntry entry)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        string recipeName = ToRecipeName(entry.Recipe);
        if (FindRecipeByExactName(recipeName) != null || ResolveRecipe(entry.Recipe) != null)
        {
            return;
        }

        Recipe recipe = ScriptableObject.CreateInstance<Recipe>();
        recipe.m_resources = Array.Empty<Piece.Requirement>();
        recipe.m_amount = 1;
        recipe.m_minStationLevel = 1;

        recipe.name = recipeName;
        ItemDrop? item = ResolveItemFromRecipeKey(entry.Recipe);
        if (item == null)
        {
            DataForgeLogContext.Warning($"Could not add recipe '{recipeName}': recipe key must start with a result item prefab.");
            UnityEngine.Object.Destroy(recipe);
            return;
        }

        recipe.m_item = item;
        UnityEngine.Object.DontDestroyOnLoad(recipe);
        ObjectDB.instance.m_recipes.Add(recipe);
        InvalidateRecipeLookupCache();
        Baselines[recipeName] = RecipeDefinition.From(recipe);
        BaselineRecipes[recipeName] = recipe;
        CreatedRecipes.Add(recipeName);
        DataForgePlugin.Log.LogInfo($"Added recipe '{recipeName}'.");
    }

    private static void RestoreBaselineRecipes(IReadOnlyCollection<string> recipeNames)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        foreach (string recipeName in recipeNames)
        {
            if (!BaselineRecipes.TryGetValue(recipeName, out Recipe recipe))
            {
                continue;
            }

            if (!ObjectDB.instance.m_recipes.Contains(recipe))
            {
                Recipe? replacement = FindRecipeByExactName(recipeName);
                if (replacement != null)
                {
                    BaselineRecipes[recipeName] = replacement;
                    Baselines[recipeName] = RecipeDefinition.From(replacement);
                    continue;
                }

                ObjectDB.instance.m_recipes.Add(recipe);
                InvalidateRecipeLookupCache();
            }

            if (Baselines.TryGetValue(recipeName, out RecipeDefinition? baseline))
            {
                ApplyDefinition(BaselineRecipes[recipeName], baseline);
            }
        }
    }

    private static void RemoveRecipe(string recipeName, bool warnIfMissing = true)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        Recipe? recipe = ResolveRecipe(recipeName);
        if (recipe == null)
        {
            if (warnIfMissing)
            {
                DataForgeLogContext.Warning($"Could not remove recipe '{recipeName}': recipe was not found.");
            }
            return;
        }

        ObjectDB.instance.m_recipes.Remove(recipe);
        InvalidateRecipeLookupCache();
        if (!string.IsNullOrWhiteSpace(recipe.name) && CreatedRecipes.Remove(recipe.name))
        {
            Baselines.Remove(recipe.name);
            BaselineRecipes.Remove(recipe.name);
        }
    }

    private static Recipe? ResolveRecipe(string? recipeName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(recipeName))
        {
            return null;
        }

        string key = ToRecipeKey(recipeName!);
        Dictionary<string, List<Recipe>> lookup = GetRecipeLookup();
        if (!lookup.TryGetValue(key, out List<Recipe> matches))
        {
            Recipe? exact = FindRecipeByExactName(ToRecipeName(key));
            if (exact != null)
            {
                return exact;
            }

            if (!HasRecipeVariant(key) && CountRecipesByResultItem(key) > 1)
            {
                DataForgeLogContext.Warning($"Recipe key '{key}' matched multiple recipes. Use the exact key from recipes.reference.yml.");
            }

            return null;
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            DataForgeLogContext.Warning($"Recipe key '{key}' matched multiple recipes. Use a numbered recipe key from recipes.reference.yml.");
        }

        return null;
    }

    private static Recipe? FindRecipeByExactName(string? recipeName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(recipeName))
        {
            return null;
        }

        return ObjectDB.instance.m_recipes.FirstOrDefault(recipe =>
            recipe != null &&
            recipe.name != null &&
            recipe.name.Equals(recipeName, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountRecipesByResultItem(string? itemName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(itemName))
        {
            return 0;
        }

        string normalized = ToRecipeItemKey(itemName!);
        return ObjectDB.instance.m_recipes.Count(recipe =>
            recipe != null &&
            recipe.m_item != null &&
            GetItemName(recipe.m_item).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAnyRecipeForReferenceKey(string? recipeName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(recipeName))
        {
            return false;
        }

        string key = ToRecipeKey(recipeName!);
        if (GetRecipeLookup().TryGetValue(key, out List<Recipe> matches) && matches.Count > 0)
        {
            return true;
        }

        if (FindRecipeByExactName(ToRecipeName(key)) != null)
        {
            return true;
        }

        return !HasRecipeVariant(key) && CountRecipesByResultItem(key) > 0;
    }

    private static bool HasNonCreatedRecipeForReferenceKey(string? recipeName, string createdName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(recipeName))
        {
            return false;
        }

        string key = ToRecipeKey(recipeName!);
        if (GetRecipeLookup().TryGetValue(key, out List<Recipe> matches) &&
            matches.Any(recipe =>
                recipe != null &&
                !string.Equals(recipe.name, createdName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        Recipe? exact = FindRecipeByExactName(ToRecipeName(key));
        if (exact != null && !string.Equals(exact.name, createdName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !HasRecipeVariant(key) && CountRecipesByResultItem(key) > 0;
    }

    private static Dictionary<string, List<Recipe>> GetRecipeLookup()
    {
        if (!RecipeLookupCacheDirty && RecipeLookupCache != null)
        {
            return RecipeLookupCache;
        }

        Dictionary<string, List<Recipe>> lookup = new(StringComparer.OrdinalIgnoreCase);
        if (ObjectDB.instance == null)
        {
            RecipeLookupCache = lookup;
            RecipeLookupCacheDirty = false;
            return lookup;
        }

        List<Recipe> recipes = ObjectDB.instance.m_recipes
            .Where(recipe => recipe != null)
            .ToList();
        Dictionary<string, string> referenceKeys = BuildReferenceKeyMap(recipes);
        foreach (Recipe recipe in recipes)
        {
            string referenceKey = ToReferenceRecipeKey(recipe.name, RecipeDefinition.From(recipe), referenceKeys);
            if (referenceKey.Length == 0)
            {
                continue;
            }

            if (!lookup.TryGetValue(referenceKey, out List<Recipe> matches))
            {
                matches = new List<Recipe>();
                lookup[referenceKey] = matches;
            }

            matches.Add(recipe);
        }

        RecipeLookupCache = lookup;
        RecipeLookupCacheDirty = false;
        return lookup;
    }

    private static void InvalidateRecipeLookupCache()
    {
        RecipeLookupCacheDirty = true;
    }

    private static bool ShouldCreateRecipe(RecipeEntry entry)
    {
        if (!entry.HasDefinition)
        {
            return false;
        }

        string createdName = ToRecipeName(entry.Recipe);
        return CreatedRecipes.Contains(createdName)
            ? !HasNonCreatedRecipeForReferenceKey(entry.Recipe, createdName)
            : !HasAnyRecipeForReferenceKey(entry.Recipe);
    }

    private static void ApplyDefinition(Recipe recipe, RecipeDefinition definition)
    {
        (string? craftingStation, int? stationLevel) = ParseStation(definition.CraftingStation);
        Copy(definition.Item, value =>
        {
            ItemDrop? item = ResolveItem(value);
            if (item != null)
            {
                recipe.m_item = item;
            }
        });
        Copy(definition.Amount, value => recipe.m_amount = Math.Max(1, value));
        Copy(craftingStation, value => recipe.m_craftingStation = ResolveCraftingStation(value));
        Copy(stationLevel ?? definition.MinStationLevel, value => recipe.m_minStationLevel = Math.Max(1, value));
        ApplyRequireOnlyOneIngredient(recipe, definition.RequireOnlyOneIngredient);
        Copy(definition.ListSortWeight, value => recipe.m_listSortWeight = value);

        if (definition.Resources != null)
        {
            recipe.m_resources = BuildRequirements(definition.Resources).ToArray();
        }
    }

    private static List<Piece.Requirement> BuildRequirements(List<RequirementDefinition> definitions)
    {
        List<Piece.Requirement> requirements = new();
        foreach (RequirementDefinition definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Item))
            {
                DataForgeLogContext.Warning("Skipping recipe requirement without item.");
                continue;
            }

            ItemDrop? item = ResolveItem(definition.Item);
            if (item == null)
            {
                DataForgeLogContext.Warning($"Skipping recipe requirement for unknown item '{definition.Item}'.");
                continue;
            }

            requirements.Add(new Piece.Requirement
            {
                m_resItem = item,
                m_amount = Math.Max(0, definition.Amount ?? 0),
                m_amountPerLevel = Math.Max(0, definition.AmountPerLevel ?? 0),
                m_extraAmountOnlyOneIngredient = Math.Max(0, definition.ExtraAmountOnlyOneIngredient ?? 0),
                m_recover = true
            });
        }

        return requirements;
    }

    private static void ApplyQualityBonuses(Recipe recipe, List<QualityBonusDefinition>? definitions)
    {
        if (definitions == null || definitions.Count == 0)
        {
            return;
        }

        List<QualityBonusRule> rules = new();
        foreach (QualityBonusDefinition definition in definitions)
        {
            string itemName = definition.Item?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(itemName))
            {
                DataForgeLogContext.Warning($"Skipping qualityBonus entry without item on recipe '{recipe.name}'.");
                continue;
            }

            float amountPerLevel = Math.Max(0f, definition.AmountPerLevel ?? 0f);
            if (amountPerLevel <= 0f)
            {
                continue;
            }

            ItemDrop? item = ResolveItem(itemName);
            if (item == null)
            {
                DataForgeLogContext.Warning($"Skipping qualityBonus for unknown item '{itemName}' on recipe '{recipe.name}'.");
                continue;
            }

            rules.Add(new QualityBonusRule(
                itemName,
                GetItemName(item),
                item.m_itemData.m_shared.m_name,
                amountPerLevel));
        }

        if (rules.Count == 0)
        {
            return;
        }

        lock (StateLock)
        {
            ActiveQualityBonuses[recipe.name] = rules;
            ActiveQualityBonusRecipeCount = ActiveQualityBonuses.Count;
        }
    }

    private static void ClearActiveQualityBonuses()
    {
        lock (StateLock)
        {
            ActiveQualityBonuses.Clear();
            ActiveQualityBonusRecipeCount = 0;
        }
    }

    internal static int GetQualityBonusAmount(Recipe recipe, int qualityLevel, ItemDrop.ItemData? singleReqItem, int craftMultiplier)
    {
        if (ActiveQualityBonusRecipeCount == 0 || Player.m_localPlayer == null)
        {
            return 0;
        }

        List<QualityBonusRule> rules = GetActiveQualityBonusRules(recipe);
        if (rules.Count == 0)
        {
            return 0;
        }

        int multiplier = Math.Max(1, craftMultiplier);
        int bonusPerCraft = 0;
        if (recipe.m_requireOnlyOneIngredient)
        {
            if (singleReqItem == null)
            {
                return 0;
            }

            foreach (QualityBonusRule rule in rules)
            {
                if (RuleMatchesItemData(rule, singleReqItem))
                {
                    bonusPerCraft += CalculateQualityBonus(singleReqItem.m_quality, rule.AmountPerLevel);
                }
            }

            return bonusPerCraft * multiplier;
        }

        Inventory inventory = Player.m_localPlayer.GetInventory();
        foreach (QualityBonusRule rule in rules)
        {
            ItemDrop.ItemData? item = FindQualifyingItemForBonus(recipe, rule, inventory, qualityLevel, multiplier, out _);
            if (item != null)
            {
                bonusPerCraft += CalculateQualityBonus(item.m_quality, rule.AmountPerLevel);
            }
        }

        return bonusPerCraft * multiplier;
    }

    internal static bool TryConsumeQualityBonusResources(Player player, Piece.Requirement[] requirements, int qualityLevel, int itemQuality, int multiplier)
    {
        if (ActiveQualityBonusRecipeCount == 0 || itemQuality >= 0 || requirements == null)
        {
            return false;
        }

        Recipe? recipe = FindRecipeByRequirements(requirements);
        if (recipe == null)
        {
            return false;
        }

        List<QualityBonusRule> rules = GetActiveQualityBonusRules(recipe);
        if (rules.Count == 0)
        {
            return false;
        }

        Inventory inventory = player.GetInventory();
        int craftMultiplier = Math.Max(1, multiplier);
        foreach (Piece.Requirement requirement in requirements)
        {
            if (!requirement.m_resItem)
            {
                continue;
            }

            int amount = requirement.GetAmount(qualityLevel) * craftMultiplier;
            if (amount <= 0)
            {
                continue;
            }

            int removeQuality = itemQuality;
            if (rules.Any(rule => RuleMatchesItemDrop(rule, requirement.m_resItem)))
            {
                ItemDrop.ItemData? item = FindQualifyingInventoryItem(inventory, requirement.m_resItem, amount);
                if (item != null)
                {
                    removeQuality = item.m_quality;
                }
            }

            inventory.RemoveItem(requirement.m_resItem.m_itemData.m_shared.m_name, amount, removeQuality);
        }

        return true;
    }

    private static Recipe? FindRecipeByRequirements(Piece.Requirement[] requirements)
    {
        if (ActiveQualityBonusRecipeCount == 0 || ObjectDB.instance == null)
        {
            return null;
        }

        foreach (Recipe recipe in ObjectDB.instance.m_recipes)
        {
            if (recipe == null || !ReferenceEquals(recipe.m_resources, requirements))
            {
                continue;
            }

            if (GetActiveQualityBonusRules(recipe).Count > 0)
            {
                return recipe;
            }
        }

        return null;
    }

    private static List<QualityBonusRule> GetActiveQualityBonusRules(Recipe recipe)
    {
        if (ActiveQualityBonusRecipeCount == 0)
        {
            return EmptyQualityBonusRules;
        }

        lock (StateLock)
        {
            return ActiveQualityBonuses.TryGetValue(recipe.name, out List<QualityBonusRule>? rules)
                ? rules
                : EmptyQualityBonusRules;
        }
    }

    private static ItemDrop.ItemData? FindQualifyingItemForBonus(
        Recipe recipe,
        QualityBonusRule rule,
        Inventory inventory,
        int qualityLevel,
        int craftMultiplier,
        out Piece.Requirement? matchedRequirement)
    {
        matchedRequirement = null;
        foreach (Piece.Requirement requirement in recipe.m_resources ?? Array.Empty<Piece.Requirement>())
        {
            if (!requirement.m_resItem || !RuleMatchesItemDrop(rule, requirement.m_resItem))
            {
                continue;
            }

            matchedRequirement = requirement;
            int requiredAmount = requirement.GetAmount(qualityLevel) * Math.Max(1, craftMultiplier);
            return FindQualifyingInventoryItem(inventory, requirement.m_resItem, requiredAmount);
        }

        return null;
    }

    private static ItemDrop.ItemData? FindQualifyingInventoryItem(Inventory inventory, ItemDrop item, int requiredAmount)
    {
        if (requiredAmount <= 0)
        {
            return null;
        }

        string sharedName = item.m_itemData.m_shared.m_name;
        int maxQuality = Math.Max(1, item.m_itemData.m_shared.m_maxQuality);
        for (int quality = maxQuality; quality >= 1; quality--)
        {
            if (inventory.CountItems(sharedName, quality) >= requiredAmount)
            {
                return inventory.GetItem(sharedName, quality);
            }
        }

        return null;
    }

    private static int CalculateQualityBonus(int itemQuality, float amountPerLevel)
    {
        return Mathf.CeilToInt(Math.Max(0, itemQuality - 1) * amountPerLevel);
    }

    private static bool RuleMatchesItemDrop(QualityBonusRule rule, ItemDrop item)
    {
        return rule.PrefabName.Equals(GetItemName(item), StringComparison.OrdinalIgnoreCase) ||
               rule.SharedName.Equals(item.m_itemData.m_shared.m_name, StringComparison.OrdinalIgnoreCase) ||
               rule.Input.Equals(GetItemName(item), StringComparison.OrdinalIgnoreCase) ||
               rule.Input.Equals(item.m_itemData.m_shared.m_name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RuleMatchesItemData(QualityBonusRule rule, ItemDrop.ItemData item)
    {
        string prefabName = item.m_dropPrefab != null ? GetPrefabName(item.m_dropPrefab) : "";
        return rule.PrefabName.Equals(prefabName, StringComparison.OrdinalIgnoreCase) ||
               rule.SharedName.Equals(item.m_shared.m_name, StringComparison.OrdinalIgnoreCase) ||
               rule.Input.Equals(prefabName, StringComparison.OrdinalIgnoreCase) ||
               rule.Input.Equals(item.m_shared.m_name, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyRequireOnlyOneIngredient(Recipe recipe, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length == 0 || parts[0].Length == 0)
        {
            return;
        }

        if (!bool.TryParse(parts[0], out bool requireOnlyOneIngredient))
        {
            DataForgeLogContext.Warning($"Could not parse requireOnlyOneIngredient value '{parts[0]}'. Expected true or false.");
            return;
        }

        float qualityResultAmountMultiplier = 1f;
        if (parts.Length > 1 && parts[1].Length > 0 &&
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out qualityResultAmountMultiplier))
        {
            DataForgeLogContext.Warning($"Could not parse requireOnlyOneIngredient multiplier '{parts[1]}'. Expected a number.");
            return;
        }

        recipe.m_requireOnlyOneIngredient = requireOnlyOneIngredient;
        recipe.m_qualityResultAmountMultiplier = Math.Max(0f, qualityResultAmountMultiplier);
    }

    private static (string? Station, int? MinStationLevel) ParseStation(string? value)
    {
        if (value == null)
        {
            return (null, null);
        }

        string[] parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length == 0)
        {
            return (value, null);
        }

        int? level = null;
        if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLevel))
        {
            level = parsedLevel;
        }

        return (parts[0], level);
    }

    private static ItemDrop? ResolveItem(string? itemName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        GameObject? prefab = ObjectDB.instance.GetItemPrefab(itemName);
        if (prefab == null)
        {
            DataForgeLogContext.Warning($"Could not resolve recipe item '{itemName}'.");
            return null;
        }

        return prefab.GetComponent<ItemDrop>();
    }

    private static ItemDrop? ResolveItemFromRecipeKey(string recipeKey)
    {
        if (ObjectDB.instance == null)
        {
            return null;
        }

        string key = ToRecipeItemKey(recipeKey);
        GameObject exactPrefab = ObjectDB.instance.GetItemPrefab(key);
        if (exactPrefab != null)
        {
            return exactPrefab.GetComponent<ItemDrop>();
        }

        foreach (GameObject prefab in ObjectDB.instance.m_items
                     .Where(prefab => prefab != null)
                     .OrderByDescending(prefab => GetPrefabName(prefab).Length))
        {
            string prefabName = GetPrefabName(prefab);
            if (prefabName.Length == 0)
            {
                continue;
            }

            if (key.Equals(prefabName, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(prefabName + "_", StringComparison.OrdinalIgnoreCase))
            {
                return prefab.GetComponent<ItemDrop>();
            }
        }

        return null;
    }

    private static CraftingStation? ResolveCraftingStation(string? stationName)
    {
        if (IsNone(stationName))
        {
            return null;
        }

        if (ZNetScene.instance == null)
        {
            return null;
        }

        GameObject prefab = ZNetScene.instance.GetPrefab(stationName);
        if (prefab == null)
        {
            DataForgeLogContext.Warning($"Could not resolve crafting station '{stationName}'.");
            return null;
        }

        CraftingStation station = prefab.GetComponent<CraftingStation>();
        if (station == null)
        {
            DataForgeLogContext.Warning($"Prefab '{stationName}' does not have a CraftingStation component.");
        }

        return station;
    }

    private static void WriteGeneratedArtifacts()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles)
        {
            return;
        }

        WriteReferenceArtifact();
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        path = Path.Combine(ConfigDirectory, FullScaffoldFileName);
        return GeneratedArtifactWriter.TryWriteFullScaffoldIfReady(
            path,
            DomainName,
            CanBuildGeneratedArtifacts(),
            $"{DomainName} game data is not ready yet.",
            () =>
            {
                EnsureConfigDirectoryAndDefaultOverride();
                CaptureAllBaselinesIfNeeded();
                Dictionary<string, string> referenceKeys = BuildReferenceKeyMap(Baselines);
                var fullEntries = Baselines
                    .Select(pair => new
                    {
                        Entry = RecipeFullEntry.From(pair.Key, pair.Value, referenceKeys),
                        OwnerKey = pair.Value.Item ?? ToRecipeKey(pair.Key),
                        SortKey = DataForgeResourceMap.BuildItemSortKey(
                            pair.Value.Item ?? ToRecipeKey(pair.Key),
                            DataForgeResourceMap.GetResourceTierSortValue(pair.Value.Resources?.Select(resource => resource.Item) ?? Array.Empty<string?>()),
                            ToReferenceRecipeKey(pair.Key, pair.Value, referenceKeys))
                    })
                    .OrderBy(pair => pair.SortKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return GeneratedArtifactWriter.GeneratedHeader(DomainName, OverrideFileName, "full scaffold") +
                       DataForgeReferenceSections.SerializeReferenceSections(
                           fullEntries,
                           entry => entry.SortKey,
                           entry => DataForgeOwnerResolver.GetPrefabOwnerName(entry.OwnerKey),
                           entry => entry.Entry,
                           FullSerializer);
            },
            out error);
    }

    private static void WriteReferenceArtifact()
    {
        if (!CanBuildGeneratedArtifacts())
        {
            return;
        }

        EnsureConfigDirectoryAndDefaultOverride();
        CaptureAllBaselinesIfNeeded();
        string sourceSignature = ComputeReferenceSourceSignature();
        string referencePath = Path.Combine(ConfigDirectory, ReferenceFileName);
        if (ShouldSkipReferenceUpdate(referencePath, sourceSignature))
        {
            return;
        }

        bool wrote = GeneratedArtifactWriter.WriteReferenceIfReady(
            Baselines.Count > 0,
            ConfigDirectory,
            ReferenceFileName,
            DomainName,
            OverrideFileName,
            BuildReferenceArtifactContent);
        if (wrote || File.Exists(referencePath))
        {
            RecordReferenceUpdateState(referencePath, sourceSignature);
        }
    }

    private static string BuildReferenceArtifactContent()
    {
        Dictionary<string, string> referenceKeys = BuildReferenceKeyMap(Baselines);
        var referenceEntries = Baselines
            .Select(pair => new
            {
                Entry = RecipeReferenceEntry.From(pair.Key, pair.Value, referenceKeys),
                OwnerKey = pair.Value.Item ?? ToRecipeKey(pair.Key),
                SortKey = DataForgeResourceMap.BuildItemSortKey(
                    pair.Value.Item ?? ToRecipeKey(pair.Key),
                    DataForgeResourceMap.GetResourceTierSortValue(pair.Value.Resources?.Select(resource => resource.Item) ?? Array.Empty<string?>()),
                    ToReferenceRecipeKey(pair.Key, pair.Value, referenceKeys))
            })
            .ToList();

        return DataForgeReferenceSections.SerializeReferenceSections(
            referenceEntries,
            entry => entry.SortKey,
            entry => DataForgeOwnerResolver.GetPrefabOwnerName(entry.OwnerKey),
            entry => entry.Entry,
            SparseSerializer);
    }

    private static bool CanBuildGeneratedArtifacts()
    {
        return ObjectDbReady && ObjectDB.instance != null;
    }

    private static string ComputeReferenceSourceSignature()
    {
        StringBuilder builder = new();
        builder.AppendLine(ReferenceLogicVersion);
        builder.AppendLine(BuildFileStamp(Path.Combine(ConfigDirectory, "z_resourcemap.txt")));
        if (ObjectDB.instance != null)
        {
            foreach (Recipe recipe in ObjectDB.instance.m_recipes
                         .Where(recipe => recipe != null && !string.IsNullOrWhiteSpace(recipe.name))
                         .OrderBy(recipe => recipe.name, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(recipe.name.Trim());
                builder.Append('|');
                builder.AppendLine(SparseSerializer.Serialize(RecipeDefinition.From(recipe)));
            }
        }

        return ComputeStableHash(builder.ToString());
    }

    private static bool ShouldSkipReferenceUpdate(string referencePath, string sourceSignature)
    {
        return DataForgeReferenceState.ShouldSkip(ReferenceStateKey, referencePath, sourceSignature, ReferenceLogicVersion);
    }

    private static void RecordReferenceUpdateState(string referencePath, string sourceSignature)
    {
        DataForgeReferenceState.Record(ReferenceStateKey, referencePath, sourceSignature, ReferenceLogicVersion);
    }

    private static string BuildFileStamp(string path)
    {
        return DataForgeReferenceState.BuildFileStamp(path);
    }

    private static string ComputeStableHash(string value)
    {
        return DataForgeReferenceState.ComputeStableHash(value);
    }

    private static string GetItemName(ItemDrop? item)
    {
        return item != null ? GetPrefabName(item.gameObject) : "";
    }

    private static string GetStationName(CraftingStation? station)
    {
        return station != null ? GetPrefabName(station.gameObject) : "none";
    }

    private static string GetPrefabName(GameObject gameObject)
    {
        return gameObject.name.Replace("(Clone)", "").Trim();
    }

    private sealed class RecipeKeyCandidate
    {
        internal string RecipeName { get; set; } = "";
        internal string RecipeKey { get; set; } = "";
        internal string ItemName { get; set; } = "";
    }

    private static Dictionary<string, string> BuildReferenceKeyMap(IEnumerable<KeyValuePair<string, RecipeDefinition>> definitions)
    {
        return BuildReferenceKeyMap(definitions.Select(pair => new RecipeKeyCandidate
        {
            RecipeName = pair.Key,
            RecipeKey = ToRecipeKey(pair.Key),
            ItemName = pair.Value.Item?.Trim() ?? ""
        }));
    }

    private static Dictionary<string, string> BuildReferenceKeyMap(IEnumerable<Recipe> recipes)
    {
        return BuildReferenceKeyMap(recipes
            .Where(recipe => recipe != null)
            .Select(recipe =>
            {
                RecipeDefinition definition = RecipeDefinition.From(recipe);
                return new RecipeKeyCandidate
                {
                    RecipeName = recipe.name ?? "",
                    RecipeKey = ToRecipeKey(recipe.name ?? ""),
                    ItemName = definition.Item?.Trim() ?? ""
                };
            }));
    }

    private static Dictionary<string, string> BuildReferenceKeyMap(IEnumerable<RecipeKeyCandidate> candidates)
    {
        Dictionary<string, string> referenceKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, RecipeKeyCandidate> group in candidates
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate.RecipeName))
                     .GroupBy(candidate => candidate.ItemName, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                foreach (RecipeKeyCandidate candidate in group)
                {
                    referenceKeys[candidate.RecipeName] = candidate.RecipeKey;
                }

                continue;
            }

            List<RecipeKeyCandidate> ordered = group
                .OrderBy(GetRecipeVariantSortRank)
                .ThenBy(candidate => candidate.RecipeKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ordered.Count == 1 && !TryGetCreatedRecipeVariant(ordered[0], out _))
            {
                referenceKeys[ordered[0].RecipeName] = group.Key;
                continue;
            }

            HashSet<int> assignedIndices = new();
            List<RecipeKeyCandidate> unassigned = new();

            foreach (RecipeKeyCandidate candidate in ordered)
            {
                if (TryGetCreatedRecipeVariant(candidate, out string createdVariant))
                {
                    referenceKeys[candidate.RecipeName] = $"{group.Key};{createdVariant}";
                    continue;
                }

                if (TryGetExplicitRecipeIndex(candidate, out int explicitIndex) && assignedIndices.Add(explicitIndex))
                {
                    referenceKeys[candidate.RecipeName] = $"{group.Key};{explicitIndex.ToString(CultureInfo.InvariantCulture)}";
                    continue;
                }

                unassigned.Add(candidate);
            }

            int nextIndex = 1;
            foreach (RecipeKeyCandidate candidate in unassigned)
            {
                while (assignedIndices.Contains(nextIndex))
                {
                    nextIndex++;
                }

                referenceKeys[candidate.RecipeName] = $"{group.Key};{nextIndex.ToString(CultureInfo.InvariantCulture)}";
                assignedIndices.Add(nextIndex);
            }
        }

        return referenceKeys;
    }

    private static bool TryGetCreatedRecipeVariant(RecipeKeyCandidate candidate, out string variant)
    {
        variant = "";
        if (!CreatedRecipes.Contains(candidate.RecipeName) ||
            string.IsNullOrWhiteSpace(candidate.ItemName) ||
            string.IsNullOrWhiteSpace(candidate.RecipeKey))
        {
            return false;
        }

        foreach (string prefix in new[] { $"{candidate.ItemName}_Recipe_", $"{candidate.ItemName}_" })
        {
            if (!candidate.RecipeKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            variant = candidate.RecipeKey.Substring(prefix.Length).Trim();
            return variant.Length > 0;
        }

        return false;
    }

    private static bool TryGetExplicitRecipeIndex(RecipeKeyCandidate candidate, out int recipeIndex)
    {
        recipeIndex = 0;
        if (string.IsNullOrWhiteSpace(candidate.ItemName) || string.IsNullOrWhiteSpace(candidate.RecipeKey))
        {
            return false;
        }

        foreach (string prefix in new[] { $"{candidate.ItemName}_Recipe_", $"{candidate.ItemName}_" })
        {
            if (!candidate.RecipeKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string suffix = candidate.RecipeKey.Substring(prefix.Length).Trim();
            return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out recipeIndex) &&
                   recipeIndex > 0;
        }

        return false;
    }

    private static int GetRecipeVariantSortRank(RecipeKeyCandidate candidate)
    {
        string recipeKey = candidate.RecipeKey;
        string itemName = candidate.ItemName;
        if (recipeKey.Equals(itemName, StringComparison.OrdinalIgnoreCase) ||
            recipeKey.Equals($"{itemName}_Default", StringComparison.OrdinalIgnoreCase) ||
            recipeKey.Equals($"{itemName}_Recipe_Default", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return 10;
    }

    private static string ToRecipeName(string recipeKey)
    {
        string trimmed = ToRecipeKey(recipeKey);
        string internalKey = trimmed.Replace(';', '_');
        return internalKey.StartsWith("Recipe_", StringComparison.OrdinalIgnoreCase)
            ? internalKey
            : "Recipe_" + internalKey;
    }

    private static string ToRecipeKey(string recipeName)
    {
        string trimmed = recipeName.Split(new[] { ',' }, 2, StringSplitOptions.None)[0].Trim();
        return trimmed.StartsWith("Recipe_", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring("Recipe_".Length)
            : trimmed;
    }

    private static string ToRecipeItemKey(string recipeName)
    {
        string key = ToRecipeKey(recipeName);
        int separator = key.IndexOf(';');
        return separator >= 0 ? key.Substring(0, separator).Trim() : key;
    }

    private static bool HasRecipeVariant(string recipeName)
    {
        return ToRecipeKey(recipeName).IndexOf(';') >= 0;
    }

    private static string ToReferenceRecipeKey(
        string recipeName,
        RecipeDefinition definition,
        Dictionary<string, string>? referenceKeys = null)
    {
        if (referenceKeys != null && referenceKeys.TryGetValue(recipeName, out string mappedKey))
        {
            return mappedKey;
        }

        string recipeKey = ToRecipeKey(recipeName);
        if (string.IsNullOrWhiteSpace(definition.Item))
        {
            return recipeKey;
        }

        string itemKey = definition.Item!.Trim();
        return itemKey;
    }

    private static bool TryNormalizeRecipeHeader(string recipeHeader, out string normalizedRecipe, out string error)
    {
        normalizedRecipe = "";
        error = "";

        string[] parts = recipeHeader.Split(new[] { ',' }, 2, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        string key = ToRecipeKey(parts[0]);
        if (!TryParseRecipeKey(key, out string itemPrefab, out string? variant, out error))
        {
            return false;
        }

        string normalizedKey = variant == null ? itemPrefab : $"{itemPrefab};{variant}";
        normalizedRecipe = parts.Length > 1 && parts[1].Length > 0
            ? $"{normalizedKey}, {parts[1]}"
            : normalizedKey;
        return true;
    }

    private static bool TryParseRecipeKey(string recipeKey, out string itemPrefab, out string? variant, out string error)
    {
        itemPrefab = "";
        variant = null;
        error = "";

        string[] parts = recipeKey.Split(new[] { ';' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length == 1)
        {
            if (parts[0].Length == 0)
            {
                error = "Recipe key must include an item prefab.";
                return false;
            }

            itemPrefab = parts[0];
            return true;
        }

        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            error = "Recipe keys must use 'ItemPrefab' or 'ItemPrefab;variant' format.";
            return false;
        }

        if (parts[1].IndexOfAny(new[] { ',', ';' }) >= 0)
        {
            error = "Recipe variant must not contain ',' or ';'.";
            return false;
        }

        itemPrefab = parts[0];
        variant = parts[1];
        return true;
    }

    private static int? ParseRecipeAmount(string recipeHeader)
    {
        string[] parts = recipeHeader.Split(new[] { ',' }, 2, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            return null;
        }

        if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
        {
            return Math.Max(1, amount);
        }

        DataForgeLogContext.Warning($"Could not parse recipe amount '{parts[1]}' in '{recipeHeader}'. Expected 'recipe: Prefab, amount'.");
        return null;
    }

    private static string FormatRecipeHeader(string recipeKey, int? amount, bool includeDefaultAmount)
    {
        if (!amount.HasValue || (!includeDefaultAmount && amount.Value == 1))
        {
            return recipeKey;
        }

        return $"{recipeKey}, {Math.Max(1, amount.Value).ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? FormatStation(string? station, int? minStationLevel)
    {
        if (string.IsNullOrWhiteSpace(station) || IsNone(station))
        {
            return null;
        }

        return minStationLevel.HasValue && minStationLevel.Value > 1
            ? $"{station}, {minStationLevel.Value.ToString(CultureInfo.InvariantCulture)}"
            : station;
    }

    private static string FormatRequireOnlyOneIngredient(bool? requireOnlyOneIngredient, float? qualityResultAmountMultiplier)
    {
        return $"{(requireOnlyOneIngredient ?? false).ToString().ToLowerInvariant()}, {(qualityResultAmountMultiplier ?? 1f).ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    internal sealed class RecipeEntry
    {
        internal string LogContext { get; private set; } = "";
        public string Recipe { get; set; } = "";
        public bool Override { get; set; } = true;
        public bool Remove { get; set; }
        public string? CraftingStation { get; set; }
        public string? RequireOnlyOneIngredient { get; set; }
        public int? ListSortWeight { get; set; }
        public List<RequirementDefinition>? Resources { get; set; }
        public List<QualityBonusDefinition>? QualityBonus { get; set; }

        internal void SetLogContext(string value)
        {
            LogContext = value;
        }

        internal bool HasDefinition =>
            ParseRecipeAmount(Recipe).HasValue ||
            CraftingStation != null ||
            RequireOnlyOneIngredient != null ||
            ListSortWeight.HasValue ||
            Resources != null ||
            QualityBonus != null;
    }

    internal sealed class RecipeFullEntry
    {
        public string Recipe { get; set; } = "";
        public bool Override { get; set; } = true;
        public bool Remove { get; set; }
        public string? CraftingStation { get; set; }
        public string? RequireOnlyOneIngredient { get; set; }
        public int? ListSortWeight { get; set; }
        public List<RequirementDefinition>? Resources { get; set; }
        public List<QualityBonusDefinition>? QualityBonus { get; set; }

        internal static RecipeFullEntry From(string name, RecipeDefinition definition, Dictionary<string, string> referenceKeys)
        {
            return new RecipeFullEntry
            {
                Recipe = FormatRecipeHeader(ToReferenceRecipeKey(name, definition, referenceKeys), definition.Amount, includeDefaultAmount: true),
                Override = true,
                Remove = false,
                CraftingStation = FormatStation(definition.CraftingStation, definition.MinStationLevel),
                RequireOnlyOneIngredient = definition.RequireOnlyOneIngredient,
                ListSortWeight = definition.ListSortWeight,
                Resources = definition.Resources,
                QualityBonus = definition.QualityBonus
            };
        }
    }

    internal sealed class RecipeReferenceEntry
    {
        public string Recipe { get; set; } = "";
        public string? CraftingStation { get; set; }
        public string? RequireOnlyOneIngredient { get; set; }
        public int? ListSortWeight { get; set; }
        public List<ResourceReferenceDefinition>? Resources { get; set; }

        internal static RecipeReferenceEntry From(string name, RecipeDefinition definition, Dictionary<string, string> referenceKeys)
        {
            bool includeAmountPerLevel = IsResultItemUpgradeable(definition.Item);
            bool includeExtraAmountOnlyOneIngredient = IsRequireOnlyOneIngredient(definition.RequireOnlyOneIngredient);
            return ReferenceValue.ClonePruned(new RecipeReferenceEntry
            {
                Recipe = FormatRecipeHeader(ToReferenceRecipeKey(name, definition, referenceKeys), definition.Amount, includeDefaultAmount: false),
                CraftingStation = FormatStation(definition.CraftingStation, definition.MinStationLevel),
                RequireOnlyOneIngredient = definition.RequireOnlyOneIngredient,
                ListSortWeight = definition.ListSortWeight,
                Resources = definition.Resources?
                    .Select(resource => ResourceReferenceDefinition.From(resource, includeAmountPerLevel, includeExtraAmountOnlyOneIngredient))
                    .ToList()
            })!;
        }
    }

    internal sealed class ResourceReferenceDefinition : Dictionary<string, string>
    {
        internal static ResourceReferenceDefinition From(RequirementDefinition definition, bool includeAmountPerLevel, bool includeExtraAmountOnlyOneIngredient)
        {
            ResourceReferenceDefinition resource = new();
            string item = definition.Item ?? "";
            List<string> values = new();
            if (definition.Amount.HasValue)
            {
                values.Add(definition.Amount.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (includeAmountPerLevel &&
                definition.AmountPerLevel.HasValue &&
                definition.AmountPerLevel.Value != 0)
            {
                values.Add(definition.AmountPerLevel.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (includeExtraAmountOnlyOneIngredient &&
                definition.ExtraAmountOnlyOneIngredient.HasValue &&
                definition.ExtraAmountOnlyOneIngredient.Value != 0)
            {
                if (!definition.Amount.HasValue)
                {
                    values.Add("0");
                }

                if (!includeAmountPerLevel ||
                    !definition.AmountPerLevel.HasValue ||
                    definition.AmountPerLevel.Value == 0)
                {
                    values.Add("0");
                }

                values.Add(definition.ExtraAmountOnlyOneIngredient.Value.ToString(CultureInfo.InvariantCulture));
            }

            resource[item] = string.Join(", ", values);
            return resource;
        }
    }

    private static bool IsResultItemUpgradeable(string? itemName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        GameObject? prefab = ObjectDB.instance.GetItemPrefab(itemName);
        ItemDrop? itemDrop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
        return itemDrop != null && itemDrop.m_itemData.m_shared.m_maxQuality > 1;
    }

    private static bool IsRequireOnlyOneIngredient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string first = value!.Split(new[] { ',' }, 2, StringSplitOptions.None)[0].Trim();
        return bool.TryParse(first, out bool parsed) && parsed;
    }

    internal sealed class RecipeDefinition
    {
        public string? Item { get; set; }
        public int? Amount { get; set; }
        public string? CraftingStation { get; set; }
        public int? MinStationLevel { get; set; }
        public string? RequireOnlyOneIngredient { get; set; }
        public int? ListSortWeight { get; set; }
        public List<RequirementDefinition>? Resources { get; set; }
        public List<QualityBonusDefinition>? QualityBonus { get; set; }

        internal static RecipeDefinition From(RecipeEntry entry)
        {
            return new RecipeDefinition
            {
                Amount = ParseRecipeAmount(entry.Recipe),
                CraftingStation = entry.CraftingStation,
                RequireOnlyOneIngredient = entry.RequireOnlyOneIngredient,
                ListSortWeight = entry.ListSortWeight,
                Resources = entry.Resources,
                QualityBonus = entry.QualityBonus
            };
        }

        internal static RecipeDefinition From(Recipe recipe)
        {
            return new RecipeDefinition
            {
                Item = GetItemName(recipe.m_item),
                Amount = recipe.m_amount,
                CraftingStation = GetStationName(recipe.m_craftingStation),
                MinStationLevel = recipe.m_minStationLevel,
                RequireOnlyOneIngredient = FormatRequireOnlyOneIngredient(recipe.m_requireOnlyOneIngredient, recipe.m_qualityResultAmountMultiplier),
                ListSortWeight = recipe.m_listSortWeight,
                Resources = recipe.m_resources?.Select(RequirementDefinition.From).ToList() ?? new List<RequirementDefinition>(),
                QualityBonus = null
            };
        }
    }

    internal sealed class RequirementDefinition
    {
        public string? Item { get; set; }
        public int? Amount { get; set; }
        public int? AmountPerLevel { get; set; }
        public int? ExtraAmountOnlyOneIngredient { get; set; }

        internal static RequirementDefinition From(Piece.Requirement requirement)
        {
            return new RequirementDefinition
            {
                Item = GetItemName(requirement.m_resItem),
                Amount = requirement.m_amount,
                AmountPerLevel = requirement.m_amountPerLevel,
                ExtraAmountOnlyOneIngredient = requirement.m_extraAmountOnlyOneIngredient
            };
        }
    }

    internal sealed class QualityBonusDefinition
    {
        public string? Item { get; set; }
        public float? AmountPerLevel { get; set; }
    }

    private sealed class QualityBonusRule
    {
        internal QualityBonusRule(string input, string prefabName, string sharedName, float amountPerLevel)
        {
            Input = input;
            PrefabName = prefabName;
            SharedName = sharedName;
            AmountPerLevel = amountPerLevel;
        }

        internal string Input { get; }
        internal string PrefabName { get; }
        internal string SharedName { get; }
        internal float AmountPerLevel { get; }
    }

    private sealed class RequirementDefinitionYamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            return type == typeof(RequirementDefinition);
        }

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<MappingStart>(out _))
            {
                List<KeyValuePair<string, string>> pairs = new();

                while (!parser.Accept<MappingEnd>(out _))
                {
                    Scalar key = parser.Consume<Scalar>();
                    if (parser.Accept<MappingStart>(out _) || parser.Accept<SequenceStart>(out _))
                    {
                        throw new YamlException(key.Start, key.End, $"Unsupported nested resource shorthand for '{key.Value}'.");
                    }

                    Scalar value = parser.Consume<Scalar>();
                    pairs.Add(new KeyValuePair<string, string>(key.Value, value.Value));
                }

                parser.Consume<MappingEnd>();

                if (pairs.Count == 1 && !IsRequirementProperty(pairs[0].Key))
                {
                    return ParseShorthandRequirement(pairs[0].Key, pairs[0].Value);
                }

                throw new YamlException("Recipe resources must use shorthand, for example '- Iron: 20, 10, 0'.");
            }

            Scalar scalar = parser.Consume<Scalar>();
            throw new YamlException(scalar.Start, scalar.End, "Recipe resources must use mapping shorthand, for example '- Iron: 20, 10, 0'.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            RequirementDefinition requirement = (RequirementDefinition)value!;
            emitter.Emit(new MappingStart());
            emitter.Emit(new Scalar(requirement.Item ?? ""));
            emitter.Emit(new Scalar(FormatShorthandRequirementValue(requirement)));
            emitter.Emit(new MappingEnd());
        }

        private static RequirementDefinition ParseShorthandRequirement(string item, string value)
        {
            string[] parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .ToArray();

            RequirementDefinition requirement = new()
            {
                Item = item
            };
            if (parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
            {
                requirement.Amount = amount;
            }

            if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amountPerLevel))
            {
                requirement.AmountPerLevel = amountPerLevel;
            }

            if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int extraAmountOnlyOneIngredient))
            {
                requirement.ExtraAmountOnlyOneIngredient = extraAmountOnlyOneIngredient;
            }

            return requirement;
        }

        private static string FormatShorthandRequirementValue(RequirementDefinition requirement)
        {
            List<string> values = new()
            {
                (requirement.Amount ?? 0).ToString(CultureInfo.InvariantCulture),
                (requirement.AmountPerLevel ?? 0).ToString(CultureInfo.InvariantCulture),
                (requirement.ExtraAmountOnlyOneIngredient ?? 0).ToString(CultureInfo.InvariantCulture)
            };

            return string.Join(", ", values);
        }

        private static bool IsRequirementProperty(string key)
        {
            return key.Equals("item", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("amount", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("amountPerLevel", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("extraAmountOnlyOneIngredient", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class QualityBonusDefinitionYamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            return type == typeof(QualityBonusDefinition);
        }

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (!parser.TryConsume<MappingStart>(out _))
            {
                Scalar scalar = parser.Consume<Scalar>();
                throw new YamlException(scalar.Start, scalar.End, "Recipe qualityBonus entries must use shorthand, for example '- Fish1: 1'.");
            }

            List<KeyValuePair<string, string>> pairs = new();
            while (!parser.Accept<MappingEnd>(out _))
            {
                Scalar key = parser.Consume<Scalar>();
                if (parser.Accept<MappingStart>(out _) || parser.Accept<SequenceStart>(out _))
                {
                    throw new YamlException(key.Start, key.End, $"Unsupported nested qualityBonus shorthand for '{key.Value}'.");
                }

                Scalar value = parser.Consume<Scalar>();
                pairs.Add(new KeyValuePair<string, string>(key.Value, value.Value));
            }

            parser.Consume<MappingEnd>();

            if (pairs.Count != 1)
            {
                throw new YamlException("Recipe qualityBonus entries must use shorthand, for example '- Fish1: 1'.");
            }

            return ParseQualityBonus(pairs[0].Key, pairs[0].Value);
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            QualityBonusDefinition bonus = (QualityBonusDefinition)value!;
            emitter.Emit(new MappingStart());
            emitter.Emit(new Scalar(bonus.Item ?? ""));
            emitter.Emit(new Scalar((bonus.AmountPerLevel ?? 0f).ToString("0.###", CultureInfo.InvariantCulture)));
            emitter.Emit(new MappingEnd());
        }

        private static QualityBonusDefinition ParseQualityBonus(string item, string value)
        {
            QualityBonusDefinition bonus = new()
            {
                Item = item
            };

            if (float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float amountPerLevel))
            {
                bonus.AmountPerLevel = amountPerLevel;
            }

            return bonus;
        }
    }
}

[HarmonyPatch(typeof(Recipe), nameof(Recipe.GetAmount))]
internal static class DataForgeRecipeGetAmountPatch
{
    private static void Postfix(Recipe __instance, int quality, ref int __result, ref ItemDrop.ItemData singleReqItem, int craftMultiplier)
    {
        __result += RecipeOverrideManager.GetQualityBonusAmount(__instance, quality, singleReqItem, craftMultiplier);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
internal static class DataForgeRecipeConsumeResourcesPatch
{
    private static bool Prefix(Player __instance, Piece.Requirement[] requirements, int qualityLevel, int itemQuality, int multiplier)
    {
        return !RecipeOverrideManager.TryConsumeQualityBonusResources(__instance, requirements, qualityLevel, itemQuality, multiplier);
    }
}
