using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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

    private static readonly object StateLock = new();
    private static readonly Dictionary<string, RecipeSlot> RecipeSlots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Recipe, RecipeSlot> RecipeSlotsByObject = new(ReferenceComparer<Recipe>.Instance);
    private static readonly Dictionary<string, Recipe> CreatedRecipeObjects = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<RecipeSlot> RuntimeAppliedRecipeSlots = new();
    private static readonly Dictionary<Recipe, List<QualityBonusRule>> ActiveQualityBonuses = new(ReferenceComparer<Recipe>.Instance);
    private static readonly List<QualityBonusRule> EmptyQualityBonusRules = new();
    private static readonly List<Recipe> ObservedRecipeObjects = new();
    private static readonly ConditionalWeakTable<Piece.Requirement, ExactQualityRequirement> ExactQualityRequirements = new();
    private static Dictionary<string, List<Recipe>>? RecipeLookupCache;
    private static readonly MethodInfo? UpdateKnownRecipesListMethod =
        AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
    private static readonly MethodInfo? InventoryGuiUpdateRecipeMethod =
        AccessTools.Method(typeof(InventoryGui), "UpdateRecipe", new[] { typeof(Player), typeof(float) });
    private static readonly MethodInfo? InventoryGuiUpdateCraftingPanelMethod =
        AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel", new[] { typeof(bool) });
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
    private static bool ApplyingConfiguration;
    private static int ActiveQualityBonusRecipeCount;
    private static bool RecipeLookupCacheDirty = true;
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
        Watcher = DataForgeFileWatcher.Create(
            ConfigDirectory,
            "*.*",
            includeSubdirectories: false,
            ReadYamlValues,
            OnWatcherError);
    }

    internal static void ReloadFromDiskAndSync()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles)
        {
            ApplySyncedPayload(SyncedPayload?.Value ?? "");
            return;
        }

        EnsureConfigDirectoryAndDefaultOverride();
        if (!TryLoadEntriesFromDisk(out List<RecipeEntry> entries))
        {
            return;
        }
        lock (StateLock)
        {
            SetActiveEntries(entries);
        }

        PublishPayload(SerializeEntries(entries));
        ApplyCurrentConfiguration();
    }

    internal static void OnObjectDBReady(bool writeGeneratedArtifacts)
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

        SynchronizeRecipeSlots();
        if (writeGeneratedArtifacts)
        {
            WriteReferenceArtifact();
        }
        ApplyCurrentConfiguration();
        RememberCurrentRecipeObjects();
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

        WriteReferenceArtifact();
        ApplyCurrentConfiguration();
    }

    internal static void ApplyCurrentConfiguration()
    {
        if (!ObjectDbReady ||
            !ZNetSceneReady ||
            !DataForgeWorldLifecycle.IsGameStarted ||
            ObjectDB.instance == null ||
            ZNetScene.instance == null ||
            ApplyingConfiguration)
        {
            return;
        }

        if (ShouldSkipRemoteClientBaselineWork())
        {
            return;
        }

        List<RecipeEntry> entries;
        lock (StateLock)
        {
            entries = ActiveEntries.ToList();
        }

        ApplyingConfiguration = true;
        try
        {
            SynchronizeRecipeSlots();
            foreach (RecipeSlot restored in RestoreBaselineRecipes(RuntimeAppliedRecipeSlots.ToArray()))
            {
                RuntimeAppliedRecipeSlots.Remove(restored);
            }
            HashSet<string> cleanedCreatedRecipes = CleanupCreatedRecipes(entries);
            ClearActiveQualityBonuses();

            if (DataForgePlugin.RecipeOverridesEnabled)
            {
                foreach (RecipeEntry entry in entries)
                {
                    ApplyEntry(entry, cleanedCreatedRecipes);
                }
            }
        }
        finally
        {
            try
            {
                InvalidateRecipeLookupCache();
                DataForgeResourceMap.InvalidateObjectDbCaches();
                RefreshLiveRecipeState();
                RememberCurrentRecipeObjects();
                VneiRefreshManager.RequestRefresh(DomainName);
            }
            finally
            {
                ApplyingConfiguration = false;
            }
        }
    }

    private static void ApplyEntry(RecipeEntry entry, HashSet<string> cleanedCreatedRecipes)
    {
        using (DataForgeLogContext.Push(entry.LogContext))
        {
            if (!entry.Override)
            {
                return;
            }

            string key = ToRecipeKey(entry.Recipe);
            string recipeName = ToRecipeName(entry.Recipe);
            RecipeSlot? slot = null;
            try
            {
                slot = ResolveRecipeSlot(entry.Recipe);
                Recipe? recipe = slot?.Current ?? ResolveCreatedRecipe(key);
                if (entry.Remove)
                {
                    if (slot != null && recipe != null)
                    {
                        recipe.m_enabled = false;
                        RuntimeAppliedRecipeSlots.Add(slot);
                    }
                    else if (recipe != null)
                    {
                        RemoveCreatedRecipe(key);
                    }
                    else if (!cleanedCreatedRecipes.Contains(key))
                    {
                        DataForgeLogContext.Warning($"Could not remove recipe '{key}': recipe was not found.");
                    }

                    return;
                }

                if (recipe == null)
                {
                    recipe = TryCreateRecipe(entry);
                    if (recipe == null)
                    {
                        if (!CanCreateRecipe(entry) && !IsAmbiguousRecipeKey(key))
                        {
                            DataForgeLogContext.Warning($"Could not find recipe '{recipeName}'.");
                        }

                        return;
                    }
                }
                else
                {
                    if (entry.HasDefinition && !TryApplyDefinition(recipe, RecipeDefinition.From(entry)))
                    {
                        return;
                    }

                    if (slot != null)
                    {
                        recipe.m_enabled = slot.BaselineEnabled;
                    }
                }

                if (slot != null)
                {
                    RuntimeAppliedRecipeSlots.Add(slot);
                }

                ApplyQualityBonuses(recipe, entry.QualityBonus);
            }
            catch (Exception ex)
            {
                if (slot != null)
                {
                    if (RestoreBaselineRecipes(new[] { slot }).Contains(slot))
                    {
                        RuntimeAppliedRecipeSlots.Remove(slot);
                    }
                }

                DataForgeLogContext.Warning($"Could not apply recipe '{key}': {ex.Message}");
            }
        }
    }

    private static void RefreshLiveRecipeState()
    {
        RefreshKnownRecipes();
        RefreshInventoryGuiRecipes();
    }

    internal static void RefreshComputedRequirementState()
    {
        if (!ObjectDbReady || ObjectDB.instance == null)
        {
            return;
        }

        RefreshLiveRecipeState();
        VneiRefreshManager.RequestRefresh(DomainName);
    }

    internal static void RebindItemPrefabReferences(ItemDrop previous, ItemDrop replacement)
    {
        if (ObjectDB.instance == null || previous == null || replacement == null)
        {
            return;
        }

        foreach (Recipe recipe in ObjectDB.instance.m_recipes)
        {
            if (recipe == null)
            {
                continue;
            }

            if (ReferenceEquals(recipe.m_item, previous))
            {
                recipe.m_item = replacement;
            }

            foreach (Piece.Requirement requirement in recipe.m_resources ?? Array.Empty<Piece.Requirement>())
            {
                if (requirement != null && ReferenceEquals(requirement.m_resItem, previous))
                {
                    requirement.m_resItem = replacement;
                }
            }
        }

        InvalidateRecipeLookupCache();
    }

    internal static void OnItemPrefabsChanged()
    {
        ApplyCurrentConfiguration();
    }

    internal static void OnObjectDBRegistersUpdated(ObjectDB objectDb)
    {
        if (ApplyingConfiguration ||
            DataForgeWorldLifecycle.IsShuttingDown ||
            !ObjectDbReady ||
            !ReferenceEquals(objectDb, ObjectDB.instance) ||
            !HasRecipeTopologyChanged())
        {
            return;
        }

        WriteReferenceArtifact();
        ApplyCurrentConfiguration();
    }

    private static bool ShouldSkipRemoteClientBaselineWork()
    {
        if (!DataForgePlugin.IsRemoteServerClient)
        {
            return false;
        }

        lock (StateLock)
        {
            return LastAppliedSyncedPayload == null &&
                   ActiveEntries.Count == 0 &&
                   CreatedRecipeObjects.Count == 0 &&
                   RuntimeAppliedRecipeSlots.Count == 0 &&
                   ActiveQualityBonusRecipeCount == 0;
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
        Player player = Player.m_localPlayer;
        if (gui == null || player == null)
        {
            return;
        }

        InvokeInventoryGuiRefresh(
            gui,
            InventoryGuiUpdateCraftingPanelMethod,
            "UpdateCraftingPanel",
            new object[] { false });
        InvokeInventoryGuiRefresh(
            gui,
            InventoryGuiUpdateRecipeMethod,
            "UpdateRecipe",
            new object[] { player, 0f });
    }

    private static void InvokeInventoryGuiRefresh(
        InventoryGui gui,
        MethodInfo? method,
        string methodName,
        object[] arguments)
    {
        if (method == null)
        {
            return;
        }

        try
        {
            method.Invoke(gui, arguments);
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

    private static void OnWatcherError(object sender, ErrorEventArgs e)
    {
        DataForgePlugin.Log.LogWarning($"Recipe file watcher lost events; scheduling a full reload: {e.GetException().Message}");
        if (!DataForgeFileWatcher.TryRecreate(
                "recipe",
                () =>
                {
                    SetupFileWatcher();
                    ReloadDebouncer?.Schedule();
                }))
        {
            ReloadYamlValues();
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
        ApplySyncedPayload(payload);
    }

    private static void ApplySyncedPayload(string payload)
    {
        if (string.Equals(LastAppliedSyncedPayload, payload, StringComparison.Ordinal))
        {
            return;
        }

        if (!DataForgeOverrideFiles.TryDeserializeEntries(payload, "synced recipe payload", DeserializeEntries, out List<RecipeEntry> entries))
        {
            return;
        }

        LastAppliedSyncedPayload = payload;
        lock (StateLock)
        {
            SetActiveEntries(entries);
        }

        ApplyCurrentConfiguration();
    }

    private static void SetActiveEntries(List<RecipeEntry> entries)
    {
        ActiveEntries = entries;
    }

    private static void PublishPayload(string payload)
    {
        DataForgeSync.PublishPayload(SyncedPayload, DomainName, payload);
    }

    private static bool TryLoadEntriesFromDisk(out List<RecipeEntry> entries)
    {
        return DataForgeOverrideFiles.TryLoadEntries(GetOverrideFiles(), DeserializeEntries, out entries);
    }

    private static List<RecipeEntry> DeserializeEntries(string yaml, string source)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new List<RecipeEntry>();
        }

        try
        {
            IReadOnlyList<long> entryLines = DataForgeLogContext.GetLocalTopLevelEntryLines(yaml, source);
            List<RecipeEntry>? entries = Deserializer.Deserialize<List<RecipeEntry>>(yaml);
            return NormalizeEntries(entries, source, entryLines);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse {source}: {ex.Message}", ex);
        }
    }

    private static List<RecipeEntry> NormalizeEntries(
        List<RecipeEntry>? entries,
        string source,
        IReadOnlyList<long> entryLines)
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
            string sourceContext = DataForgeLogContext.FormatSource(
                source,
                entryIndex,
                DataForgeLogContext.GetEntryLine(entryLines, entryIndex));
            if (string.IsNullOrWhiteSpace(entry.Recipe))
            {
                DataForgeLogContext.Warning($"{sourceContext}: Skipping recipe entry without recipe.");
                continue;
            }

            if (!TryNormalizeRecipeHeader(entry.Recipe, out string normalizedRecipe, out string error))
            {
                DataForgeLogContext.Warning($"{sourceContext}: Skipping recipe entry '{entry.Recipe}'. {error}");
                continue;
            }

            entry.Recipe = normalizedRecipe;
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
        return DataForgeOverrideFiles.IsDomainOverrideFile(path, OverrideFileName, DomainName);
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
            "# - recipe: Sausages, 4                   # result item prefab, result amount. Use Sausages;1, 4 / Sausages;2, 4 when reference lists multiple recipes. Custom additions can use Sausages;myVariant, 4.",
            "#                                          # Variant ids after ';' should be one word; use letters, numbers, '_' or '-' rather than spaces.",
            "#   override: true                        # default true; false skips this entire entry, including remove.",
            "#   remove: false                         # default false; true disables this exact recipe; false or deleting the entry restores it live.",
            "#   craftingStation: forge, 2              # station prefab and optional min station level. Use none for hand craft.",
            "#   requireOnlyOneIngredient: false, 1     # true, 1 => any one listed ingredient can craft; selected ingredient quality increases output by ceil((quality - 1) * amount * 1). If false, the multiplier is effectively unused.",
            "#   listSortWeight: 100                    # UI sort weight.",
            "#   resources:                            # required when adding a new recipe. Use [] only for intentional free crafting.",
            "#   - Iron: 20, 10                         # itemPrefab: craft amount, upgrade amount. Upgrade amount uses vanilla (quality - 1) scaling.",
            "#   - SurtlingCore: 0, 5, 2               # itemPrefab: craft amount, upgrade amount, exact quality. Requires 5 only when upgrading to quality 2.",
            "#   - Wood: 5                              # shorthand: itemPrefab: amount.",
            "#   qualityBonus:",
            "#   - Fish1: 1                             # DataForge extension: if this resource is consumed at quality 3, add ceil((3 - 1) * 1) result items per craft.",
            "#",
            "# Example:",
            "# - recipe: Sausages, 4",
            "#   craftingStation: forge, 2",
            "#   resources:",
            "#   - Iron: 20, 10",
            "#   - Wood: 5"
        }) + Environment.NewLine;
    }

    private static void CaptureAllBaselinesIfNeeded()
    {
        int added = SynchronizeRecipeSlots();
        if (added > 0)
        {
            DataForgePlugin.Log.LogInfo($"Captured {added} new recipe baselines. Tracking {RecipeSlots.Count} total.");
        }
    }

    private static int SynchronizeRecipeSlots()
    {
        if (ObjectDB.instance == null)
        {
            return 0;
        }

        List<Recipe> currentRecipes = ObjectDB.instance.m_recipes
            .Where(recipe => recipe != null)
            .Distinct(ReferenceComparer<Recipe>.Instance)
            .ToList();
        HashSet<Recipe> currentSet = new(currentRecipes, ReferenceComparer<Recipe>.Instance);
        foreach (RecipeSlot slot in RecipeSlots.Values)
        {
            if (ReferenceEquals(slot.Current, null) || currentSet.Contains(slot.Current))
            {
                continue;
            }

            RecipeSlotsByObject.Remove(slot.Current);
            slot.Current = null;
        }

        List<Recipe> newRecipes = currentRecipes
            .Where(recipe =>
                !RecipeSlotsByObject.ContainsKey(recipe) &&
                !IsCreatedRecipe(recipe) &&
                !string.IsNullOrWhiteSpace(recipe.name))
            .ToList();
        Dictionary<Recipe, RecipeDefinition> definitions = new(ReferenceComparer<Recipe>.Instance);
        Dictionary<Recipe, string> fingerprints = new(ReferenceComparer<Recipe>.Instance);
        foreach (Recipe recipe in newRecipes.ToList())
        {
            try
            {
                RecipeDefinition definition = RecipeDefinition.From(recipe);
                definitions[recipe] = definition;
                fingerprints[recipe] = BuildRecipeFingerprint(recipe.name, definition);
            }
            catch (Exception ex)
            {
                DataForgePlugin.Log.LogWarning(
                    $"Skipping invalid runtime recipe while capturing baselines: {ex.Message}");
                newRecipes.Remove(recipe);
            }
        }

        foreach (Recipe recipe in newRecipes.ToList())
        {
            RecipeDefinition definition = definitions[recipe];
            List<RecipeSlot> exact = RecipeSlots.Values
                .Where(slot =>
                    slot.Current == null &&
                    RecipeStemEquals(slot, recipe.name, definition.Item) &&
                    slot.BaselineFingerprint.Equals(fingerprints[recipe], StringComparison.Ordinal))
                .ToList();
            if (exact.Count != 1)
            {
                continue;
            }

            BindRecipeSlot(exact[0], recipe, definition, replaceBaseline: false);
            newRecipes.Remove(recipe);
        }

        foreach (IGrouping<string, Recipe> group in newRecipes
                     .GroupBy(recipe => BuildRecipeStem(recipe.name, definitions[recipe].Item), StringComparer.OrdinalIgnoreCase)
                     .ToList())
        {
            List<Recipe> replacements = group
                .OrderBy(recipe => fingerprints[recipe], StringComparer.Ordinal)
                .ThenBy(recipe => currentRecipes.FindIndex(current => ReferenceEquals(current, recipe)))
                .ToList();
            List<RecipeSlot> stale = RecipeSlots.Values
                .Where(slot => slot.Current == null && BuildRecipeStem(slot.OriginalName, slot.ItemName)
                    .Equals(group.Key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(slot => slot.BaselineFingerprint, StringComparer.Ordinal)
                .ThenBy(slot => slot.PublicKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (replacements.Count == 0 || replacements.Count != stale.Count)
            {
                continue;
            }

            if (replacements.Count > 1)
            {
                DataForgePlugin.Log.LogWarning(
                    $"Rebinding {replacements.Count} externally rebuilt recipes named '{replacements[0].name}' by deterministic baseline order.");
            }

            for (int index = 0; index < replacements.Count; index++)
            {
                Recipe replacement = replacements[index];
                bool clonedAppliedState = stale[index].LastAppliedFingerprint.Equals(
                    fingerprints[replacement],
                    StringComparison.Ordinal);
                BindRecipeSlot(
                    stale[index],
                    replacement,
                    definitions[replacement],
                    replaceBaseline: !clonedAppliedState);
                newRecipes.Remove(replacement);
            }
        }

        foreach (RecipeSlot stale in RecipeSlots.Values.Where(slot => slot.Current == null).ToList())
        {
            RecipeSlots.Remove(stale.PublicKey);
            RuntimeAppliedRecipeSlots.Remove(stale);
        }

        int added = AddNewRecipeSlots(newRecipes, definitions, fingerprints, currentRecipes);
        InvalidateRecipeLookupCache();

        return added;
    }

    private static int AddNewRecipeSlots(
        List<Recipe> recipes,
        Dictionary<Recipe, RecipeDefinition> definitions,
        Dictionary<Recipe, string> fingerprints,
        List<Recipe> sourceOrder)
    {
        int added = 0;
        foreach (IGrouping<string, Recipe> group in recipes
                     .GroupBy(recipe => definitions[recipe].Item?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
        {
            string itemName = group.Key;
            List<Recipe> ordered = group
                .OrderBy(recipe => GetRecipeVariantSortRank(new RecipeKeyCandidate
                {
                    RecipeName = recipe.name ?? "",
                    RecipeKey = ToRecipeKey(recipe.name ?? ""),
                    ItemName = itemName
                }))
                .ThenBy(recipe => ToRecipeKey(recipe.name ?? ""), StringComparer.OrdinalIgnoreCase)
                .ThenBy(recipe => fingerprints[recipe], StringComparer.Ordinal)
                .ThenBy(recipe => sourceOrder.FindIndex(current => ReferenceEquals(current, recipe)))
                .ToList();
            List<RecipeSlot> existing = RecipeSlots.Values
                .Where(slot => slot.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (itemName.Length > 0 && existing.Count == 0 && ordered.Count == 1)
            {
                AddRecipeSlot(itemName, ordered[0], definitions[ordered[0]], fingerprints[ordered[0]]);
                added++;
                continue;
            }

            if (itemName.Length == 0)
            {
                foreach (Recipe recipe in ordered)
                {
                    string key = MakeUniqueRecipeKey(ToRecipeKey(recipe.name ?? "Recipe"));
                    AddRecipeSlot(key, recipe, definitions[recipe], fingerprints[recipe]);
                    added++;
                }

                continue;
            }

            RecipeSlot? nakedSlot = existing.FirstOrDefault(slot =>
                slot.PublicKey.Equals(itemName, StringComparison.OrdinalIgnoreCase));
            if (nakedSlot != null)
            {
                RekeyRecipeSlot(nakedSlot, MakeUniqueRecipeKey($"{itemName};1", nakedSlot));
            }

            HashSet<int> assignedIndices = new(
                RecipeSlots.Values
                    .Where(slot => slot.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                    .Select(slot => TryGetVariantIndex(slot.PublicKey, out int index) ? index : 0)
                    .Where(index => index > 0));
            List<Recipe> unassigned = new();
            foreach (Recipe recipe in ordered)
            {
                RecipeKeyCandidate candidate = new()
                {
                    RecipeName = recipe.name ?? "",
                    RecipeKey = ToRecipeKey(recipe.name ?? ""),
                    ItemName = itemName
                };
                if (TryGetExplicitRecipeIndex(candidate, out int explicitIndex) && assignedIndices.Add(explicitIndex))
                {
                    AddRecipeSlot($"{itemName};{explicitIndex.ToString(CultureInfo.InvariantCulture)}", recipe, definitions[recipe], fingerprints[recipe]);
                    added++;
                }
                else
                {
                    unassigned.Add(recipe);
                }
            }

            int nextIndex = 1;
            foreach (Recipe recipe in unassigned)
            {
                while (assignedIndices.Contains(nextIndex))
                {
                    nextIndex++;
                }

                AddRecipeSlot($"{itemName};{nextIndex.ToString(CultureInfo.InvariantCulture)}", recipe, definitions[recipe], fingerprints[recipe]);
                assignedIndices.Add(nextIndex);
                added++;
            }
        }

        return added;
    }

    private static void AddRecipeSlot(
        string publicKey,
        Recipe recipe,
        RecipeDefinition definition,
        string fingerprint)
    {
        RecipeSlot slot = new(
            publicKey,
            recipe.name ?? "",
            definition.Item?.Trim() ?? "",
            fingerprint,
            definition,
            recipe,
            recipe.m_enabled,
            fingerprint);
        RecipeSlots[publicKey] = slot;
        RecipeSlotsByObject[recipe] = slot;
    }

    private static void BindRecipeSlot(
        RecipeSlot slot,
        Recipe recipe,
        RecipeDefinition definition,
        bool replaceBaseline)
    {
        slot.Current = recipe;
        RecipeSlotsByObject[recipe] = slot;
        if (replaceBaseline)
        {
            slot.OriginalName = recipe.name ?? "";
            slot.ItemName = definition.Item?.Trim() ?? "";
            slot.Baseline = definition;
            slot.BaselineFingerprint = BuildRecipeFingerprint(slot.OriginalName, definition);
            slot.BaselineEnabled = recipe.m_enabled;
        }
    }

    private static void RekeyRecipeSlot(RecipeSlot slot, string newKey)
    {
        RecipeSlots.Remove(slot.PublicKey);
        slot.PublicKey = newKey;
        RecipeSlots[newKey] = slot;
    }

    private static string MakeUniqueRecipeKey(string preferred, RecipeSlot? ignored = null)
    {
        if (!RecipeSlots.TryGetValue(preferred, out RecipeSlot? existing) || ReferenceEquals(existing, ignored))
        {
            return preferred;
        }

        string itemName = ToRecipeItemKey(preferred);
        int index = 1;
        string candidate;
        do
        {
            candidate = $"{itemName};{index.ToString(CultureInfo.InvariantCulture)}";
            index++;
        }
        while (RecipeSlots.TryGetValue(candidate, out existing) && !ReferenceEquals(existing, ignored));

        return candidate;
    }

    private static bool TryGetVariantIndex(string key, out int index)
    {
        index = 0;
        int separator = key.LastIndexOf(';');
        return separator >= 0 &&
               int.TryParse(key.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) &&
               index > 0;
    }

    private static bool RecipeStemEquals(RecipeSlot slot, string? recipeName, string? itemName)
    {
        return slot.OriginalName.Equals(recipeName ?? "", StringComparison.OrdinalIgnoreCase) &&
               slot.ItemName.Equals(itemName?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRecipeStem(string? recipeName, string? itemName)
    {
        return $"{itemName?.Trim() ?? ""}\n{recipeName?.Trim() ?? ""}";
    }

    private static string BuildRecipeFingerprint(string? recipeName, RecipeDefinition definition)
    {
        return ComputeStableHash(
            $"{recipeName?.Trim() ?? ""}\n{SparseSerializer.Serialize(definition)}");
    }

    private static string ComputeStableHash(string value)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
        StringBuilder builder = new(hash.Length * 2);
        foreach (byte part in hash)
        {
            builder.Append(part.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool IsCreatedRecipe(Recipe recipe)
    {
        return CreatedRecipeObjects.Values.Any(created => ReferenceEquals(created, recipe));
    }

    private static bool HasRecipeTopologyChanged()
    {
        if (ObjectDB.instance == null)
        {
            return false;
        }

        List<Recipe> current = ObjectDB.instance.m_recipes.Where(recipe => recipe != null).ToList();
        return current.Count != ObservedRecipeObjects.Count ||
               current.Where((recipe, index) => !ReferenceEquals(recipe, ObservedRecipeObjects[index])).Any();
    }

    private static void RememberCurrentRecipeObjects()
    {
        ObservedRecipeObjects.Clear();
        if (ObjectDB.instance != null)
        {
            ObservedRecipeObjects.AddRange(ObjectDB.instance.m_recipes.Where(recipe => recipe != null));
            foreach (RecipeSlot slot in RecipeSlots.Values)
            {
                Recipe? recipe = slot.Current;
                if (recipe == null || !ObservedRecipeObjects.Any(current => ReferenceEquals(current, recipe)))
                {
                    continue;
                }

                try
                {
                    slot.LastAppliedFingerprint = BuildRecipeFingerprint(recipe.name, RecipeDefinition.From(recipe));
                }
                catch (Exception ex)
                {
                    DataForgePlugin.Log.LogDebug(
                        $"Could not remember applied recipe state '{slot.PublicKey}': {ex.Message}");
                }
            }
        }
    }

    private static HashSet<string> CleanupCreatedRecipes(List<RecipeEntry> entries)
    {
        HashSet<string> removedCreatedKeys = new(StringComparer.OrdinalIgnoreCase);
        if (ObjectDB.instance == null)
        {
            return removedCreatedKeys;
        }

        HashSet<string> activeCreatedKeys = new(
            DataForgePlugin.RecipeOverridesEnabled
                ? entries
                    .Where(entry => entry.Override && !entry.Remove)
                    .Select(entry => ToRecipeKey(entry.Recipe))
                : Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (string key in CreatedRecipeObjects.Keys.ToList())
        {
            if (activeCreatedKeys.Contains(key) && !RecipeSlots.ContainsKey(key))
            {
                continue;
            }

            RemoveCreatedRecipe(key);
            removedCreatedKeys.Add(key);
        }

        return removedCreatedKeys;
    }

    internal static void CleanupCreatedRecipesForWorldTransition()
    {
        foreach (string key in CreatedRecipeObjects.Keys.ToList())
        {
            RemoveCreatedRecipe(key);
        }
    }

    internal static void OnWorldShutdown()
    {
        try
        {
            RestoreBaselineRecipes(RuntimeAppliedRecipeSlots.ToArray());
        }
        finally
        {
            try
            {
                CleanupCreatedRecipesForWorldTransition();
            }
            finally
            {
                ObjectDbReady = false;
                ZNetSceneReady = false;
                RuntimeAppliedRecipeSlots.Clear();
                ObservedRecipeObjects.Clear();
                ClearActiveQualityBonuses();
                lock (StateLock)
                {
                    if (DataForgePlugin.IsRemoteServerClient)
                    {
                        SetActiveEntries(new List<RecipeEntry>());
                        LastAppliedSyncedPayload = null;
                    }
                }
            }
        }
    }

    private static void RemoveCreatedRecipe(string key)
    {
        CreatedRecipeObjects.TryGetValue(key, out Recipe? recipe);
        if (recipe != null)
        {
            if (ObjectDB.instance != null)
            {
                ObjectDB.instance.m_recipes.Remove(recipe);
            }

            UnityEngine.Object.Destroy(recipe);
        }

        CreatedRecipeObjects.Remove(key);
        InvalidateRecipeLookupCache();
    }

    private static Recipe? TryCreateRecipe(RecipeEntry entry)
    {
        if (ObjectDB.instance == null || !CanCreateRecipe(entry))
        {
            return null;
        }

        string key = ToRecipeKey(entry.Recipe);
        string recipeName = ToRecipeName(entry.Recipe);
        if (entry.Resources == null)
        {
            DataForgeLogContext.Warning(
                $"Could not add recipe '{recipeName}': new recipes must declare resources. Use 'resources: []' only for intentional free crafting.");
            return null;
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
            return null;
        }

        recipe.m_item = item;
        if (!TryApplyDefinition(recipe, RecipeDefinition.From(entry)))
        {
            UnityEngine.Object.Destroy(recipe);
            return null;
        }

        UnityEngine.Object.DontDestroyOnLoad(recipe);
        ObjectDB.instance.m_recipes.Add(recipe);
        InvalidateRecipeLookupCache();
        CreatedRecipeObjects[key] = recipe;
        DataForgePlugin.Log.LogInfo($"Added recipe '{recipeName}'.");
        return recipe;
    }

    private static HashSet<RecipeSlot> RestoreBaselineRecipes(IReadOnlyCollection<RecipeSlot> slots)
    {
        HashSet<RecipeSlot> restored = new();
        if (ObjectDB.instance == null)
        {
            return restored;
        }

        foreach (RecipeSlot slot in slots)
        {
            try
            {
                Recipe? recipe = slot.Current;
                if (recipe == null || !ObjectDB.instance.m_recipes.Any(current => ReferenceEquals(current, recipe)))
                {
                    continue;
                }

                if (TryApplyDefinition(recipe, slot.Baseline))
                {
                    recipe.m_enabled = slot.BaselineEnabled;
                    restored.Add(slot);
                }
                else
                {
                    DataForgePlugin.Log.LogWarning(
                        $"Could not restore recipe baseline '{slot.PublicKey}'; it will be retried on the next recipe reconciliation.");
                }
            }
            catch (Exception ex)
            {
                DataForgePlugin.Log.LogWarning(
                    $"Could not restore recipe baseline '{slot.PublicKey}': {ex.Message}");
            }
        }

        return restored;
    }

    private static RecipeSlot? ResolveRecipeSlot(string? recipeName)
    {
        Recipe? recipe = ResolveRecipe(recipeName);
        if (recipe == null)
        {
            return null;
        }

        return RecipeSlotsByObject.TryGetValue(recipe, out RecipeSlot? slot) ? slot : null;
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

        List<Recipe> matches = ObjectDB.instance.m_recipes
            .Where(recipe =>
                recipe != null &&
                recipe.name != null &&
                recipe.name.Equals(recipeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
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

    private static Recipe? ResolveCreatedRecipe(string key)
    {
        return CreatedRecipeObjects.TryGetValue(key, out Recipe? recipe) ? recipe : null;
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

        foreach (RecipeSlot slot in RecipeSlots.Values)
        {
            Recipe? recipe = slot.Current;
            if (recipe == null || !ObjectDB.instance.m_recipes.Any(current => ReferenceEquals(current, recipe)))
            {
                continue;
            }

            if (!lookup.TryGetValue(slot.PublicKey, out List<Recipe> matches))
            {
                matches = new List<Recipe>();
                lookup[slot.PublicKey] = matches;
            }

            matches.Add(recipe);
        }

        foreach (KeyValuePair<string, Recipe> pair in CreatedRecipeObjects)
        {
            if (!lookup.TryGetValue(pair.Key, out List<Recipe> matches))
            {
                matches = new List<Recipe>();
                lookup[pair.Key] = matches;
            }

            matches.Add(pair.Value);
        }

        RecipeLookupCache = lookup;
        RecipeLookupCacheDirty = false;
        return lookup;
    }

    private static void InvalidateRecipeLookupCache()
    {
        RecipeLookupCacheDirty = true;
    }

    private static bool CanCreateRecipe(RecipeEntry entry)
    {
        if (!entry.HasDefinition)
        {
            return false;
        }

        string key = ToRecipeKey(entry.Recipe);
        if (CreatedRecipeObjects.ContainsKey(key) || GetRecipeLookup().ContainsKey(key))
        {
            return false;
        }

        return HasRecipeVariant(key) || CountRecipesByResultItem(key) == 0;
    }

    private static bool IsAmbiguousRecipeKey(string key)
    {
        return GetRecipeLookup().TryGetValue(key, out List<Recipe>? matches)
            ? matches.Count > 1
            : !HasRecipeVariant(key) && CountRecipesByResultItem(key) > 1;
    }

    private static bool TryApplyDefinition(Recipe recipe, RecipeDefinition definition)
    {
        ItemDrop? item = null;
        if (definition.Item != null)
        {
            item = ResolveItem(definition.Item);
            if (item == null)
            {
                return false;
            }
        }

        if (!TryParseStation(definition.CraftingStation, out string? craftingStation, out int? stationLevel))
        {
            return false;
        }

        CraftingStation? station = null;
        if (definition.CraftingStation != null &&
            !TryResolveCraftingStation(craftingStation, out station))
        {
            return false;
        }

        if (!TryParseRequireOnlyOneIngredient(
                definition.RequireOnlyOneIngredient,
                out bool requireOnlyOneIngredient,
                out float qualityResultAmountMultiplier))
        {
            return false;
        }

        Piece.Requirement[]? resources = null;
        if (definition.Resources != null && !TryBuildRequirements(definition.Resources, out resources))
        {
            return false;
        }

        if (item != null)
        {
            recipe.m_item = item;
        }

        Copy(definition.Amount, value => recipe.m_amount = Math.Max(1, value));
        if (definition.CraftingStation != null)
        {
            recipe.m_craftingStation = station;
        }

        Copy(stationLevel ?? definition.MinStationLevel, value => recipe.m_minStationLevel = Math.Max(1, value));
        if (definition.RequireOnlyOneIngredient != null)
        {
            recipe.m_requireOnlyOneIngredient = requireOnlyOneIngredient;
            recipe.m_qualityResultAmountMultiplier = qualityResultAmountMultiplier;
        }

        Copy(definition.ListSortWeight, value => recipe.m_listSortWeight = value);

        if (resources != null)
        {
            recipe.m_resources = resources;
        }

        return true;
    }

    private static bool TryBuildRequirements(
        List<RequirementDefinition> definitions,
        out Piece.Requirement[] requirements)
    {
        List<Piece.Requirement> resolved = new();
        foreach (RequirementDefinition definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Item))
            {
                DataForgeLogContext.Warning("Could not apply recipe: a resource entry has no item.");
                requirements = Array.Empty<Piece.Requirement>();
                return false;
            }

            ItemDrop? item = ResolveItem(definition.Item);
            if (item == null)
            {
                DataForgeLogContext.Warning(
                    $"Could not apply recipe resources because item '{definition.Item}' is unavailable. The existing resource list was kept.");
                requirements = Array.Empty<Piece.Requirement>();
                return false;
            }

            Piece.Requirement requirement = new()
            {
                m_resItem = item,
                m_amount = Math.Max(0, definition.Amount ?? 0),
                m_amountPerLevel = Math.Max(0, definition.AmountPerLevel ?? 0),
                m_recover = true
            };
            if (definition.ExactQuality is >= 2)
            {
                ExactQualityRequirements.Add(requirement, new ExactQualityRequirement(definition.ExactQuality.Value));
            }

            resolved.Add(requirement);
        }

        requirements = resolved.ToArray();
        return true;
    }

    internal static bool TryGetExactQualityAmount(Piece.Requirement requirement, int qualityLevel, ref int result)
    {
        if (!ExactQualityRequirements.TryGetValue(requirement, out ExactQualityRequirement? exactQuality))
        {
            return false;
        }

        result = qualityLevel <= 1
            ? requirement.m_amount
            : qualityLevel == exactQuality.Quality
                ? requirement.m_amountPerLevel
                : 0;
        return true;
    }

    internal static void ApplyUpgradeMaterialScaling(Piece.Requirement requirement, int qualityLevel, ref int result)
    {
        DataForgePlugin.UpgradeMaterialScalingMode mode = DataForgePlugin.UpgradeMaterialScaling;
        if (mode == DataForgePlugin.UpgradeMaterialScalingMode.Vanilla ||
            qualityLevel <= 1 ||
            result <= 0 ||
            requirement.m_amountPerLevel <= 0 ||
            ExactQualityRequirements.TryGetValue(requirement, out _))
        {
            return;
        }

        long amountPerLevel = requirement.m_amountPerLevel;
        long vanillaAmount = amountPerLevel * (qualityLevel - 1L);
        if (vanillaAmount > int.MaxValue || result != (int)vanillaAmount)
        {
            return;
        }

        long scaledAmount = mode switch
        {
            DataForgePlugin.UpgradeMaterialScalingMode.Flat => amountPerLevel,
            DataForgePlugin.UpgradeMaterialScalingMode.Reduced => (amountPerLevel * qualityLevel + 1L) / 2L,
            _ => result
        };
        result = (int)Math.Min(int.MaxValue, scaledAmount);
    }

    private static int? DetectExactQuality(Piece.Requirement requirement, int maxQuality)
    {
        if (ExactQualityRequirements.TryGetValue(requirement, out ExactQualityRequirement? known))
        {
            return known.Quality;
        }

        if (requirement.m_amountPerLevel <= 0)
        {
            return null;
        }

        int detectedQuality = 0;
        int probeMaximum = Math.Max(3, maxQuality + 1);
        for (int quality = 2; quality <= probeMaximum; quality++)
        {
            int amount;
            try
            {
                amount = requirement.GetAmount(quality);
            }
            catch
            {
                return null;
            }

            if (amount == 0)
            {
                continue;
            }

            if (amount != requirement.m_amountPerLevel || detectedQuality != 0)
            {
                return null;
            }

            detectedQuality = quality;
        }

        return detectedQuality >= 2 ? detectedQuality : null;
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
            ActiveQualityBonuses[recipe] = rules;
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
            ItemDrop.ItemData? item = FindQualifyingItemForBonus(recipe, rule, inventory, qualityLevel, multiplier);
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
            return ActiveQualityBonuses.TryGetValue(recipe, out List<QualityBonusRule>? rules)
                ? rules
                : EmptyQualityBonusRules;
        }
    }

    private static ItemDrop.ItemData? FindQualifyingItemForBonus(
        Recipe recipe,
        QualityBonusRule rule,
        Inventory inventory,
        int qualityLevel,
        int craftMultiplier)
    {
        foreach (Piece.Requirement requirement in recipe.m_resources ?? Array.Empty<Piece.Requirement>())
        {
            if (!requirement.m_resItem || !RuleMatchesItemDrop(rule, requirement.m_resItem))
            {
                continue;
            }

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

    private static bool TryParseRequireOnlyOneIngredient(
        string? value,
        out bool requireOnlyOneIngredient,
        out float qualityResultAmountMultiplier)
    {
        requireOnlyOneIngredient = false;
        qualityResultAmountMultiplier = 1f;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length == 0 || parts[0].Length == 0)
        {
            return true;
        }

        if (parts.Length > 2 || !bool.TryParse(parts[0], out requireOnlyOneIngredient))
        {
            DataForgeLogContext.Warning($"Could not parse requireOnlyOneIngredient value '{parts[0]}'. Expected true or false.");
            return false;
        }

        if (parts.Length > 1 && parts[1].Length > 0 &&
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out qualityResultAmountMultiplier))
        {
            DataForgeLogContext.Warning($"Could not parse requireOnlyOneIngredient multiplier '{parts[1]}'. Expected a number.");
            return false;
        }

        qualityResultAmountMultiplier = Math.Max(0f, qualityResultAmountMultiplier);
        return true;
    }

    private static bool TryParseStation(string? value, out string? station, out int? minStationLevel)
    {
        station = null;
        minStationLevel = null;
        if (value == null)
        {
            return true;
        }

        string[] parts = value.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length == 0 || parts[0].Length == 0 || parts.Length > 2)
        {
            DataForgeLogContext.Warning($"Could not parse craftingStation value '{value}'. Expected 'station' or 'station, level'.");
            return false;
        }

        station = parts[0];
        if (parts.Length > 1)
        {
            if (parts[1].Length == 0 ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLevel))
            {
                DataForgeLogContext.Warning($"Could not parse crafting station level '{parts[1]}' in '{value}'.");
                return false;
            }

            minStationLevel = parsedLevel;
        }

        return true;
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

        ItemDrop item = prefab.GetComponent<ItemDrop>();
        if (item == null)
        {
            DataForgeLogContext.Warning($"Prefab '{itemName}' does not have an ItemDrop component.");
        }

        return item;
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

    private static bool TryResolveCraftingStation(string? stationName, out CraftingStation? station)
    {
        station = null;
        if (IsNone(stationName))
        {
            return true;
        }

        if (ZNetScene.instance == null)
        {
            DataForgeLogContext.Warning($"Could not resolve crafting station '{stationName}': ZNetScene is not ready.");
            return false;
        }

        GameObject prefab = ZNetScene.instance.GetPrefab(stationName);
        if (prefab == null)
        {
            DataForgeLogContext.Warning($"Could not resolve crafting station '{stationName}'.");
            return false;
        }

        station = prefab.GetComponent<CraftingStation>();
        if (station == null)
        {
            DataForgeLogContext.Warning($"Prefab '{stationName}' does not have a CraftingStation component.");
            return false;
        }

        return true;
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
                var fullEntries = RecipeSlots.Values
                    .Where(slot => slot.Current != null && slot.BaselineEnabled)
                    .Select(pair => new
                    {
                        Entry = RecipeEntry.From(pair.PublicKey, pair.Baseline),
                        OwnerKey = pair.Baseline.Item ?? ToRecipeItemKey(pair.PublicKey),
                        SortKey = DataForgeResourceMap.BuildItemSortKey(
                            pair.Baseline.Item ?? ToRecipeItemKey(pair.PublicKey),
                            DataForgeResourceMap.GetResourceTierSortValue(pair.Baseline.Resources?.Select(resource => resource.Item) ?? Array.Empty<string?>()),
                            pair.PublicKey)
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

    internal static bool TryRegenerateReferenceFile(out string path, out bool changed, out string error)
    {
        path = Path.Combine(ConfigDirectory, ReferenceFileName);
        return GeneratedArtifactWriter.TryWriteReferenceIfReady(
            path,
            DomainName,
            OverrideFileName,
            CanBuildGeneratedArtifacts(),
            $"{DomainName} game data is not ready yet.",
            BuildReferenceFileContent,
            out changed,
            out error);
    }

    private static void WriteReferenceArtifact()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles ||
            DataForgeWorldLifecycle.IsShuttingDown ||
            !CanBuildGeneratedArtifacts())
        {
            return;
        }

        if (!TryRegenerateReferenceFile(out _, out _, out string error))
        {
            DataForgePlugin.Log.LogWarning(error);
        }
    }

    private static string? BuildReferenceFileContent()
    {
        EnsureConfigDirectoryAndDefaultOverride();
        CaptureAllBaselinesIfNeeded();
        if (!RecipeSlots.Values.Any(slot => slot.Current != null && slot.BaselineEnabled))
        {
            return null;
        }

        return BuildReferenceArtifactContent();
    }

    private static string BuildReferenceArtifactContent()
    {
        var referenceEntries = RecipeSlots.Values
            .Where(slot => slot.Current != null && slot.BaselineEnabled)
            .Select(slot => new
            {
                Entry = RecipeReferenceEntry.From(slot.PublicKey, slot.Baseline),
                OwnerKey = slot.Baseline.Item ?? ToRecipeItemKey(slot.PublicKey),
                SortKey = DataForgeResourceMap.BuildItemSortKey(
                    slot.Baseline.Item ?? ToRecipeItemKey(slot.PublicKey),
                    DataForgeResourceMap.GetResourceTierSortValue(slot.Baseline.Resources?.Select(resource => resource.Item) ?? Array.Empty<string?>()),
                    slot.PublicKey)
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
        return ObjectDbReady &&
               ZNetScene.instance != null &&
               ObjectDB.instance != null;
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
        if (parts.Length > 1)
        {
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) ||
                amount < 1)
            {
                error = $"Recipe amount '{parts[1]}' must be a positive integer.";
                return false;
            }

            normalizedRecipe = $"{normalizedKey}, {amount.ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            normalizedRecipe = normalizedKey;
        }

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

    private sealed class RecipeSlot
    {
        internal RecipeSlot(
            string publicKey,
            string originalName,
            string itemName,
            string baselineFingerprint,
            RecipeDefinition baseline,
            Recipe current,
            bool baselineEnabled,
            string lastAppliedFingerprint)
        {
            PublicKey = publicKey;
            OriginalName = originalName;
            ItemName = itemName;
            BaselineFingerprint = baselineFingerprint;
            Baseline = baseline;
            Current = current;
            BaselineEnabled = baselineEnabled;
            LastAppliedFingerprint = lastAppliedFingerprint;
        }

        internal string PublicKey { get; set; }
        internal string OriginalName { get; set; }
        internal string ItemName { get; set; }
        internal string BaselineFingerprint { get; set; }
        internal RecipeDefinition Baseline { get; set; }
        internal Recipe? Current { get; set; }
        internal bool BaselineEnabled { get; set; }
        internal string LastAppliedFingerprint { get; set; }
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

        internal static RecipeEntry From(string publicKey, RecipeDefinition definition)
        {
            return new RecipeEntry
            {
                Recipe = FormatRecipeHeader(publicKey, definition.Amount, includeDefaultAmount: true),
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

        internal static RecipeReferenceEntry From(string publicKey, RecipeDefinition definition)
        {
            bool includeAmountPerLevel = IsResultItemUpgradeable(definition.Item);
            return ReferenceValue.ClonePruned(new RecipeReferenceEntry
            {
                Recipe = FormatRecipeHeader(publicKey, definition.Amount, includeDefaultAmount: false),
                CraftingStation = FormatStation(definition.CraftingStation, definition.MinStationLevel),
                RequireOnlyOneIngredient = definition.RequireOnlyOneIngredient,
                ListSortWeight = definition.ListSortWeight,
                Resources = definition.Resources?
                    .Select(resource => ResourceReferenceDefinition.From(resource, includeAmountPerLevel))
                    .ToList()
            })!;
        }
    }

    internal sealed class ResourceReferenceDefinition : Dictionary<string, string>
    {
        internal static ResourceReferenceDefinition From(RequirementDefinition definition, bool includeAmountPerLevel)
        {
            ResourceReferenceDefinition resource = new();
            string item = definition.Item ?? "";
            List<string> values = new();
            if (definition.Amount.HasValue)
            {
                values.Add(definition.Amount.Value.ToString(CultureInfo.InvariantCulture));
            }

            if ((includeAmountPerLevel || definition.ExactQuality.HasValue) &&
                definition.AmountPerLevel.HasValue &&
                definition.AmountPerLevel.Value != 0)
            {
                values.Add(definition.AmountPerLevel.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (definition.ExactQuality is >= 2)
            {
                if (!definition.Amount.HasValue)
                {
                    values.Add("0");
                }

                if (!definition.AmountPerLevel.HasValue ||
                    definition.AmountPerLevel.Value == 0)
                {
                    values.Add("0");
                }

                values.Add(definition.ExactQuality.Value.ToString(CultureInfo.InvariantCulture));
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
            int maxQuality = recipe.m_item != null
                ? Math.Max(1, recipe.m_item.m_itemData.m_shared.m_maxQuality)
                : 1;
            return new RecipeDefinition
            {
                Item = GetItemName(recipe.m_item),
                Amount = recipe.m_amount,
                CraftingStation = GetStationName(recipe.m_craftingStation),
                MinStationLevel = recipe.m_minStationLevel,
                RequireOnlyOneIngredient = FormatRequireOnlyOneIngredient(recipe.m_requireOnlyOneIngredient, recipe.m_qualityResultAmountMultiplier),
                ListSortWeight = recipe.m_listSortWeight,
                Resources = recipe.m_resources?
                    .Where(requirement => requirement != null)
                    .Select(requirement => RequirementDefinition.From(requirement, maxQuality))
                    .ToList() ?? new List<RequirementDefinition>(),
                QualityBonus = null
            };
        }
    }

    internal sealed class RequirementDefinition
    {
        public string? Item { get; set; }
        public int? Amount { get; set; }
        public int? AmountPerLevel { get; set; }
        public int? ExactQuality { get; set; }

        internal static RequirementDefinition From(Piece.Requirement requirement, int maxQuality)
        {
            return new RequirementDefinition
            {
                Item = GetItemName(requirement.m_resItem),
                Amount = requirement.m_amount,
                AmountPerLevel = requirement.m_amountPerLevel,
                ExactQuality = DetectExactQuality(requirement, maxQuality)
            };
        }
    }

    private sealed class ExactQualityRequirement
    {
        internal ExactQualityRequirement(int quality)
        {
            Quality = quality;
        }

        internal int Quality { get; }
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

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T? left, T? right)
        {
            return ReferenceEquals(left, right);
        }

        public int GetHashCode(T value)
        {
            return RuntimeHelpers.GetHashCode(value);
        }
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
                Mark shorthandStart = Mark.Empty;
                Mark shorthandEnd = Mark.Empty;

                while (!parser.Accept<MappingEnd>(out _))
                {
                    Scalar key = parser.Consume<Scalar>();
                    if (parser.Accept<MappingStart>(out _) || parser.Accept<SequenceStart>(out _))
                    {
                        throw new YamlException(key.Start, key.End, $"Unsupported nested resource shorthand for '{key.Value}'.");
                    }

                    Scalar value = parser.Consume<Scalar>();
                    if (pairs.Count == 0)
                    {
                        shorthandStart = key.Start;
                    }

                    shorthandEnd = value.End;
                    pairs.Add(new KeyValuePair<string, string>(key.Value, value.Value));
                }

                parser.Consume<MappingEnd>();

                if (pairs.Count == 1 && !IsRequirementProperty(pairs[0].Key))
                {
                    return ParseShorthandRequirement(pairs[0].Key, pairs[0].Value, shorthandStart, shorthandEnd);
                }

                throw new YamlException("Recipe resources must use shorthand, for example '- Iron: 20, 10' or '- SurtlingCore: 0, 5, 2'.");
            }

            Scalar scalar = parser.Consume<Scalar>();
            throw new YamlException(scalar.Start, scalar.End, "Recipe resources must use mapping shorthand, for example '- Iron: 20, 10' or '- SurtlingCore: 0, 5, 2'.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            RequirementDefinition requirement = (RequirementDefinition)value!;
            emitter.Emit(new MappingStart());
            emitter.Emit(new Scalar(requirement.Item ?? ""));
            emitter.Emit(new Scalar(FormatShorthandRequirementValue(requirement)));
            emitter.Emit(new MappingEnd());
        }

        private static RequirementDefinition ParseShorthandRequirement(string item, string value, Mark start, Mark end)
        {
            string[] parts = value.Split(new[] { ',' }, StringSplitOptions.None)
                .Select(part => part.Trim())
                .ToArray();
            if (parts.Length > 3)
            {
                throw new YamlException(start, end, $"Recipe resource '{item}' accepts at most amount, upgradeAmount, exactQuality.");
            }

            RequirementDefinition requirement = new()
            {
                Item = item
            };
            if (parts.Length == 0 ||
                parts[0].Length == 0 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
            {
                throw new YamlException(start, end, $"Recipe resource '{item}' has an invalid craft amount '{parts.ElementAtOrDefault(0)}'.");
            }

            requirement.Amount = amount;

            if (parts.Length > 1)
            {
                if (parts[1].Length == 0 ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amountPerLevel))
                {
                    throw new YamlException(start, end, $"Recipe resource '{item}' has an invalid upgrade amount '{parts[1]}'.");
                }

                requirement.AmountPerLevel = amountPerLevel;
            }

            if (parts.Length > 2)
            {
                if (parts[2].Length == 0 ||
                    !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int exactQuality))
                {
                    throw new YamlException(start, end, $"Recipe resource '{item}' has an invalid exact quality '{parts[2]}'.");
                }

                if (exactQuality < 2)
                {
                    throw new YamlException(start, end, $"Recipe resource '{item}' exact quality must be 2 or greater.");
                }

                requirement.ExactQuality = exactQuality;
            }

            return requirement;
        }

        private static string FormatShorthandRequirementValue(RequirementDefinition requirement)
        {
            List<string> values = new()
            {
                (requirement.Amount ?? 0).ToString(CultureInfo.InvariantCulture)
            };

            if (requirement.AmountPerLevel.HasValue || requirement.ExactQuality.HasValue)
            {
                values.Add((requirement.AmountPerLevel ?? 0).ToString(CultureInfo.InvariantCulture));
            }

            if (requirement.ExactQuality.HasValue)
            {
                values.Add(requirement.ExactQuality.Value.ToString(CultureInfo.InvariantCulture));
            }

            return string.Join(", ", values);
        }

        private static bool IsRequirementProperty(string key)
        {
            return key.Equals("item", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("amount", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("amountPerLevel", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("exactQuality", StringComparison.OrdinalIgnoreCase);
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

            if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float amountPerLevel))
            {
                throw new YamlException($"Recipe qualityBonus amount '{value}' is not a valid number.");
            }

            bonus.AmountPerLevel = amountPerLevel;
            return bonus;
        }
    }
}

[HarmonyPatch(typeof(Piece.Requirement), nameof(Piece.Requirement.GetAmount))]
internal static class DataForgeRequirementGetAmountPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Piece.Requirement __instance, int qualityLevel, ref int __result)
    {
        return !RecipeOverrideManager.TryGetExactQualityAmount(__instance, qualityLevel, ref __result);
    }

    private static void Postfix(Piece.Requirement __instance, int qualityLevel, ref int __result)
    {
        RecipeOverrideManager.ApplyUpgradeMaterialScaling(__instance, qualityLevel, ref __result);
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
