using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using HarmonyLib;
using ServerSync;
using UnityEngine;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static DataForge.DataForgeValue;

namespace DataForge;

internal static class PieceOverrideManager
{
    private const string DomainName = "pieces";
    private const string ReferenceFileName = "pieces.reference.yml";
    private const string FullScaffoldFileName = "pieces.full.yml";
    private const string PieceCategoryFileName = "pieceCategory.yml";
    private const string PieceCategoryReferenceFileName = "pieceCategory.reference.yml";
    private const string SyncedPayloadKey = "pieces";
    private const string PieceCategorySyncedPayloadKey = "pieceCategories";
    private const string HammerPrefabName = "Hammer";
    private const int HammerAvailabilityRefreshAttemptLimit = 3;
    private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;
    private const int DefaultPieceSortOrder = 100;
    private const string HomesteadPluginGuid = "sighsorry.Homestead";
    private const string HomesteadCategoryName = "Homestead";
    private static readonly HashSet<string> IgnoredCategoryNames = new(StringComparer.Ordinal)
    {
        "Feasts",
        "Food",
        "Meads"
    };
    private static readonly HashSet<string> IgnoredPieceTableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Feaster",
        "FeasterPieceTable",
        "_FeasterPieceTable",
        "ServingTray"
    };

    private static readonly object StateLock = new();
    private static readonly Dictionary<string, PieceBaseline> Baselines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<PieceTable, List<GameObject>> PieceTableOrderBaselines = new(ReferenceComparer<PieceTable>.Instance);
    private static readonly Dictionary<GameObject, Piece.PieceCategory> PieceCategoryMoveBaselines = new(ReferenceComparer<GameObject>.Instance);
    private static readonly Dictionary<PieceTable, HashSet<Piece.PieceCategory>> InsertedPieceTableCategories = new(ReferenceComparer<PieceTable>.Instance);
    private static readonly Dictionary<GameObject, List<StationExtensionSnapshot>> StationExtensionRemovalSnapshots =
        new(ReferenceComparer<GameObject>.Instance);
    private static readonly HashSet<string> RuntimeAppliedPieceKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PieceTableAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hammer"] = "_HammerPieceTable",
        ["Hoe"] = "_HoePieceTable",
        ["Cultivator"] = "_CultivatorPieceTable",
        ["ServingTray"] = "_FeasterPieceTable"
    };
    private static readonly Dictionary<Piece.PieceCategory, string> KnownPieceCategoryNames = new();
    private static readonly Dictionary<string, Piece.PieceCategory> KnownPieceCategoryValues = new(StringComparer.Ordinal);
    private static readonly Dictionary<Piece.PieceCategory, int> KnownPieceCategoryNamePriorities = new();
    private static readonly Dictionary<string, int> KnownPieceCategoryValuePriorities = new(StringComparer.Ordinal);
    private static readonly Dictionary<Piece.PieceCategory, string> KnownPieceCategoryNameSources = new();
    private static readonly Dictionary<string, string> KnownPieceCategoryValueSources = new(StringComparer.Ordinal);
    private static readonly HashSet<Piece.PieceCategory> OwnerManagedPieceCategories = new();
    private static readonly HashSet<string> ReportedPieceCategoryConflicts = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ReportedPieceCategoryConfigurationIssues = new(StringComparer.Ordinal);
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
    private static readonly ISerializer SparseSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases()
        .Build();
    private static readonly ISerializer FullSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static List<PieceEntry> ActiveEntries = new();
    private static Dictionary<string, List<PieceEntry>> ActiveRuntimeEntriesByPiece = new(StringComparer.OrdinalIgnoreCase);
    private static PieceCategoryConfiguration ActivePieceCategoryConfiguration = new();
    private static string ActivePieceCategoryConfigurationSignature = "";
    private static int PieceCategoryConfigurationVersion;
    private static int AppliedPieceCategoryConfigurationVersion = -1;
    private static readonly DomainEntryChangeTracker<PieceEntry> EntryChanges = new(
        entry => entry.Piece,
        entries => SparseSerializer.Serialize(entries));
    private static CustomSyncedValue<string>? SyncedPayload;
    private static CustomSyncedValue<string>? SyncedPieceCategoryPayload;
    private static string? LastAppliedSyncedPayload;
    private static string? LastAppliedPieceCategorySyncedPayload;
    private static FileSystemWatcher? Watcher;
    private static DataForgeFileWatcher.DebouncedAction? ReloadDebouncer;
    private static bool GameDataReady;
    private static bool ObjectDbReady;
    private static bool PieceTablesReady;
    private static bool PieceTableSortWasApplied;
    private static bool PieceTableMembershipWasApplied;
    private static bool PieceCategoryConfigurationWasApplied;
    private static bool CraftingStationTopologyChanged;
    private static bool StationExtensionTopologyChanged;
    private static readonly Dictionary<Piece, HammerCategoryClaim> ExpectedHammerCategories =
        new(ReferenceComparer<Piece>.Instance);
    private static int HammerCategoryReconciliationFailures;
    private static int HammerAvailabilityRefreshAttemptsRemaining;
    private static bool HammerCategoryIdentityResolutionPending;

    private static string ConfigDirectory => Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName);

    internal static void Initialize(ConfigSync configSync)
    {
        SyncedPayload = new CustomSyncedValue<string>(configSync, SyncedPayloadKey, "");
        SyncedPayload.ValueChanged += OnSyncedPayloadChanged;
        SyncedPieceCategoryPayload = new CustomSyncedValue<string>(configSync, PieceCategorySyncedPayloadKey, "");
        SyncedPieceCategoryPayload.ValueChanged += OnSyncedPieceCategoryPayloadChanged;
    }

    internal static void Dispose()
    {
        if (SyncedPayload != null)
        {
            SyncedPayload.ValueChanged -= OnSyncedPayloadChanged;
        }

        if (SyncedPieceCategoryPayload != null)
        {
            SyncedPieceCategoryPayload.ValueChanged -= OnSyncedPieceCategoryPayloadChanged;
        }

        Watcher?.Dispose();
        Watcher = null;
        ReloadDebouncer?.Dispose();
        ReloadDebouncer = null;
        ResetHammerCategoryReconciliation();
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
            includeSubdirectories: true,
            ReadYamlValues,
            OnWatcherError);
    }

    internal static void ReloadFromDiskAndSync()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles)
        {
            ApplySyncedPayload(SyncedPayload?.Value ?? "");
            ApplySyncedPieceCategoryPayload(SyncedPieceCategoryPayload?.Value ?? "");
            return;
        }

        EnsureConfigDirectoryAndDefaultOverride();
        if (!TryLoadEntriesFromDisk(out List<PieceEntry> entries))
        {
            return;
        }

        if (!TryLoadPieceCategoryConfigurationFromDisk(out PieceCategoryConfiguration pieceCategoryConfiguration))
        {
            return;
        }

        lock (StateLock)
        {
            SetActiveEntries(entries);
            SetActivePieceCategoryConfiguration(pieceCategoryConfiguration);
        }

        PublishPayload(SerializeEntries(entries));
        PublishPieceCategoryPayload(SerializePieceCategoryConfiguration(pieceCategoryConfiguration));
        ApplyCurrentConfiguration();
        DataForgeLifecycleStep.Run("piece category generated-artifact write", WritePieceCategoryReferenceArtifact);
    }

    internal static void ApplyCurrentConfiguration()
    {
        if (!GameDataReady ||
            !ObjectDbReady ||
            !PieceTablesReady ||
            !DataForgeWorldLifecycle.IsGameStarted ||
            ZNetScene.instance == null ||
            ObjectDB.instance == null)
        {
            return;
        }

        bool pieceOverridesEnabled = DataForgePlugin.PieceOverridesEnabled;
        List<PieceEntry> activeEntries;
        PieceCategoryConfiguration activePieceCategoryConfiguration;
        HashSet<string>? changedPieceKeys;
        int pieceCategoryConfigurationVersion;
        lock (StateLock)
        {
            activeEntries = ActiveEntries.ToList();
            activePieceCategoryConfiguration = ActivePieceCategoryConfiguration;
            changedPieceKeys = EntryChanges.ConsumeChangedKeys();
            pieceCategoryConfigurationVersion = PieceCategoryConfigurationVersion;
        }

        Dictionary<string, List<PieceEntry>> activeRuntimeEntriesByPiece = pieceOverridesEnabled
            ? BuildActiveRuntimeEntriesByPiece(activeEntries)
            : new Dictionary<string, List<PieceEntry>>(StringComparer.OrdinalIgnoreCase);
        bool hasActiveRuntimeDefinitions = activeRuntimeEntriesByPiece.Count > 0;
        Dictionary<string, int> activeSortOrders = GetActiveSortOrders(activeEntries, pieceOverridesEnabled);
        Dictionary<string, PieceTableAssignment> activePieceTableAssignments =
            GetActivePieceTableAssignments(activeEntries, pieceOverridesEnabled);
        HashSet<string> activeRemovedPieces = GetActiveRemovedPieces(activeEntries, pieceOverridesEnabled);
        List<PieceCategoryMoveRule> activePieceCategoryMoves =
            GetActivePieceCategoryMoves(activePieceCategoryConfiguration, pieceOverridesEnabled);
        bool hasActivePieceCategoryConfiguration =
            pieceOverridesEnabled && activePieceCategoryConfiguration.Tables.Count > 0;

        bool pieceCategoryConfigurationNeedsApply =
            pieceCategoryConfigurationVersion != AppliedPieceCategoryConfigurationVersion ||
            hasActivePieceCategoryConfiguration != PieceCategoryConfigurationWasApplied;
        if (changedPieceKeys is { Count: 0 } && !pieceCategoryConfigurationNeedsApply)
        {
            return;
        }

        bool hasActiveSortOrders = activeSortOrders.Count > 0;
        bool hasActivePieceTableAssignments = activePieceTableAssignments.Count > 0;
        bool hasActiveRemovedPieces = activeRemovedPieces.Count > 0;
        bool hasActivePieceCategoryMoves = activePieceCategoryMoves.Count > 0;
        if (!hasActiveRuntimeDefinitions &&
            !hasActiveSortOrders &&
            !hasActivePieceTableAssignments &&
            !hasActiveRemovedPieces &&
            !hasActivePieceCategoryConfiguration &&
            RuntimeAppliedPieceKeys.Count == 0 &&
            !PieceTableSortWasApplied &&
            !PieceTableMembershipWasApplied &&
            !PieceCategoryConfigurationWasApplied)
        {
            AppliedPieceCategoryConfigurationVersion = pieceCategoryConfigurationVersion;
            return;
        }

        PieceTableCategoryGuard.RestoreTemporarilyPrunedCategories();
        RestorePieceCategoryMoveBaselines();
        RefreshPieceCategoryRegistry();

        bool shouldTouchRuntime = hasActiveRuntimeDefinitions || RuntimeAppliedPieceKeys.Count > 0;
        HashSet<string>? runtimePieceKeys = shouldTouchRuntime
            ? GetRuntimeApplyKeys(changedPieceKeys, activeRuntimeEntriesByPiece.Keys)
            : null;
        if (shouldTouchRuntime)
        {
            CaptureBaselinesForPiecesIfNeeded(runtimePieceKeys);
            ApplyToPrefabDefinitions(activeRuntimeEntriesByPiece, pieceOverridesEnabled, runtimePieceKeys);
        }

        List<ResolvedPieceCategoryMove> appliedPieceCategoryMoves = ApplyPieceTableStructure(
            activePieceTableAssignments,
            activeSortOrders,
            activeRemovedPieces,
            activePieceCategoryMoves);

        if (shouldTouchRuntime)
        {
            ApplyToLoadedInstances(activeRuntimeEntriesByPiece, pieceOverridesEnabled, runtimePieceKeys);
            if (CraftingStationTopologyChanged || StationExtensionTopologyChanged)
            {
                InvalidateCraftingStationExtensionCaches();
                StationExtensionTopologyChanged = false;
            }

        }

        PieceTableCategoryGuard.PruneUnusedCustomCategories();
        ApplyPieceCategoryConfiguration(activePieceCategoryConfiguration, pieceOverridesEnabled);
        foreach (ResolvedPieceCategoryMove move in appliedPieceCategoryMoves)
        {
            PieceTableCategoryGuard.PruneCategoryIfUnused(move.Source, move.SourceCategory);
        }
        PruneUnusedInsertedPieceTableCategories();
        if (shouldTouchRuntime ||
            hasActivePieceCategoryConfiguration ||
            PieceCategoryConfigurationWasApplied)
        {
            RefreshLocalBuildPieces();
        }

        ReapplyRecipesIfCraftingStationTopologyChanged();

        RuntimeAppliedPieceKeys.Clear();
        foreach (string key in activeRuntimeEntriesByPiece.Keys)
        {
            RuntimeAppliedPieceKeys.Add(key);
        }
        PieceTableSortWasApplied = hasActiveSortOrders;
        PieceTableMembershipWasApplied =
            hasActivePieceTableAssignments ||
            hasActiveRemovedPieces ||
            hasActivePieceCategoryMoves;
        PieceCategoryConfigurationWasApplied = hasActivePieceCategoryConfiguration;
        AppliedPieceCategoryConfigurationVersion = pieceCategoryConfigurationVersion;
        CaptureExpectedHammerCategories(activeEntries, pieceOverridesEnabled, activeRemovedPieces);
        DataForgeLifecycleStep.Run("piece category generated-artifact write", WritePieceCategoryReferenceArtifact);
        VneiRefreshManager.RequestRefresh(DomainName);
    }

    private static void ApplyPieceCategoryConfiguration(
        PieceCategoryConfiguration configuration,
        bool pieceOverridesEnabled)
    {
        Dictionary<PieceTable, IReadOnlyList<PieceTableCategoryGuard.ConfiguredCategory>> configuredOrders =
            new(ReferenceComparer<PieceTable>.Instance);
        if (pieceOverridesEnabled)
        {
            foreach (KeyValuePair<string, List<PieceCategoryOrderEntry>> tableConfiguration in configuration.Tables)
            {
                PieceTable? pieceTable = ResolvePieceTable(tableConfiguration.Key);
                if (pieceTable == null)
                {
                    string context = configuration.TableContexts.TryGetValue(
                        tableConfiguration.Key,
                        out string? tableContext)
                        ? tableContext
                        : PieceCategoryFileName;
                    ReportPieceCategoryConfigurationIssue(
                        $"table:{tableConfiguration.Key}",
                        $"{context}: Unknown piece table '{tableConfiguration.Key}'.");
                    continue;
                }

                if (configuredOrders.ContainsKey(pieceTable))
                {
                    string context = configuration.TableContexts.TryGetValue(
                        tableConfiguration.Key,
                        out string? tableContext)
                        ? tableContext
                        : PieceCategoryFileName;
                    ReportPieceCategoryConfigurationIssue(
                        $"duplicate-table:{tableConfiguration.Key}:{RuntimeHelpers.GetHashCode(pieceTable)}",
                        $"{context}: Piece table '{tableConfiguration.Key}' resolves to a piece table already configured by another section.");
                    continue;
                }

                List<PieceTableCategoryGuard.ConfiguredCategory> categories = new();
                HashSet<Piece.PieceCategory> configuredCategories = new();
                foreach (IGrouping<string, PieceCategoryOrderEntry> categoryEntries in tableConfiguration.Value
                             .GroupBy(static entry => entry.Category, StringComparer.Ordinal))
                {
                    PieceCategoryOrderEntry entry = categoryEntries.First();
                    if (!TryResolvePieceTableCategory(pieceTable, entry.Category, out Piece.PieceCategory category) &&
                        !TryResolvePieceCategory(entry.Category, out category))
                    {
                        ReportPieceCategoryConfigurationIssue(
                            $"category:{tableConfiguration.Key}:{entry.Category}",
                            $"{entry.LogContext}: Piece table '{tableConfiguration.Key}' has unknown exact category '{entry.Category}'.");
                        continue;
                    }

                    if (IsOwnerManagedHomesteadCategory(category))
                    {
                        ReportPieceCategoryConfigurationIssue(
                            $"owner-managed-category:{tableConfiguration.Key}:{entry.Category}",
                            $"{entry.LogContext}: Piece table '{tableConfiguration.Key}' cannot configure the owner-managed Homestead category; the entry was ignored.");
                        continue;
                    }

                    if (!configuredCategories.Add(category))
                    {
                        continue;
                    }

                    string? label = categoryEntries
                        .Select(static categoryEntry => categoryEntry.Label)
                        .FirstOrDefault(static categoryLabel => categoryLabel != null);
                    categories.Add(new PieceTableCategoryGuard.ConfiguredCategory(category, label));
                }

                configuredOrders[pieceTable] = categories;
            }
        }

        PieceTableCategoryGuard.ReplaceConfiguredOrders(configuredOrders);
    }

    private static void ReportPieceCategoryConfigurationIssue(string key, string message)
    {
        if (ReportedPieceCategoryConfigurationIssues.Add(key))
        {
            DataForgePlugin.Log.LogWarning(message);
        }
    }

    private static bool ShouldSkipRemoteClientBaselineWork()
    {
        if (!DataForgePlugin.IsRemoteServerClient ||
            RuntimeAppliedPieceKeys.Count > 0 ||
            PieceTableSortWasApplied ||
            PieceTableMembershipWasApplied ||
            PieceCategoryConfigurationWasApplied)
        {
            return false;
        }

        lock (StateLock)
        {
            return ActiveEntries.Count == 0 && ActivePieceCategoryConfiguration.Tables.Count == 0;
        }
    }

    internal static void OnGameDataReady()
    {
        if (ZNetScene.instance == null)
        {
            return;
        }

        GameDataReady = true;
        if (ShouldSkipRemoteClientBaselineWork())
        {
            return;
        }

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

        if (writeGeneratedArtifacts)
        {
            DataForgeLifecycleStep.Run("piece generated-artifact write", WriteGeneratedArtifacts);
        }
        ApplyCurrentConfiguration();
    }

    internal static void RebindItemPrefabReferences(GameObject previousPrefab, GameObject replacementPrefab)
    {
        if (previousPrefab == null || replacementPrefab == null)
        {
            return;
        }

        ItemDrop? previousItem = previousPrefab.GetComponent<ItemDrop>();
        ItemDrop? replacementItem = replacementPrefab.GetComponent<ItemDrop>();
        if (previousItem == null || replacementItem == null)
        {
            return;
        }

        Piece? previousPiece = previousPrefab.GetComponent<Piece>();
        Piece? replacementPiece = replacementPrefab.GetComponent<Piece>();

        foreach (Piece piece in Resources.FindObjectsOfTypeAll<Piece>())
        {
            if (piece == null)
            {
                continue;
            }

            foreach (Piece.Requirement requirement in piece.m_resources ?? Array.Empty<Piece.Requirement>())
            {
                if (requirement != null && ReferenceEquals(requirement.m_resItem, previousItem))
                {
                    requirement.m_resItem = replacementItem;
                }
            }
        }

        foreach (PieceTable pieceTable in GetAllPieceTables(includeIgnored: true))
        {
            ReplacePrefabReferences(pieceTable.m_pieces, previousPrefab, replacementPrefab);
        }

        foreach (List<GameObject> baseline in PieceTableOrderBaselines.Values)
        {
            ReplacePrefabReferences(baseline, previousPrefab, replacementPrefab);
        }

        bool hadMovedCategoryBaseline = PieceCategoryMoveBaselines.ContainsKey(previousPrefab);
        Piece.PieceCategory activeCategory = previousPiece != null
            ? previousPiece.m_category
            : default;
        PieceCategoryMoveBaselines.Remove(previousPrefab);
        if (hadMovedCategoryBaseline && replacementPiece != null)
        {
            PieceCategoryMoveBaselines[replacementPrefab] = replacementPiece.m_category;
        }

        if (hadMovedCategoryBaseline && replacementPiece != null)
        {
            replacementPiece.m_category = activeCategory;
        }

        lock (StateLock)
        {
            EntryChanges.RequireFullApply();
        }
    }

    internal static void OnItemPrefabsChanged()
    {
        lock (StateLock)
        {
            EntryChanges.RequireFullApply();
        }

        ApplyCurrentConfiguration();
    }

    private static void ReplacePrefabReferences(
        List<GameObject>? prefabs,
        GameObject previousPrefab,
        GameObject replacementPrefab)
    {
        if (prefabs == null)
        {
            return;
        }

        for (int index = 0; index < prefabs.Count; index++)
        {
            if (ReferenceEquals(prefabs[index], previousPrefab))
            {
                prefabs[index] = replacementPrefab;
            }
        }
    }

    internal static bool OnWorldShutdown()
    {
        bool cleanupSucceeded = true;
        cleanupSucceeded &= RunWorldShutdownStep("visual state", RestoreAllPieceVisualStates);
        cleanupSucceeded &= RunWorldShutdownStep("moved piece categories", RestorePieceCategoryMoveBaselines);
        bool prefabRestoreSucceeded = RunWorldShutdownStep(
            "prefab definitions",
            RestoreAppliedPrefabDefinitionsForWorldShutdown);
        cleanupSucceeded &= prefabRestoreSucceeded;
        cleanupSucceeded &= RunWorldShutdownStep("piece tables", RestoreCapturedPieceTablesForWorldShutdown);
        cleanupSucceeded &= RunWorldShutdownStep("piece categories", PieceTableCategoryGuard.ResetWorldState);
        ResetPieceCategoryRegistry();
        GameDataReady = false;
        ObjectDbReady = false;
        PieceTablesReady = false;
        RuntimeAppliedPieceKeys.Clear();
        StationExtensionRemovalSnapshots.Clear();
        PieceTableOrderBaselines.Clear();
        PieceCategoryMoveBaselines.Clear();
        InsertedPieceTableCategories.Clear();
        CraftingStationTopologyChanged = false;
        StationExtensionTopologyChanged = false;
        PieceTableSortWasApplied = false;
        PieceTableMembershipWasApplied = false;
        PieceCategoryConfigurationWasApplied = false;
        AppliedPieceCategoryConfigurationVersion = -1;
        ResetHammerCategoryReconciliation();
        if (prefabRestoreSucceeded)
        {
            Baselines.Clear();
        }
        lock (StateLock)
        {
            if (DataForgePlugin.IsRemoteServerClient)
            {
                SetActiveEntries(new List<PieceEntry>());
                LastAppliedSyncedPayload = null;
                SetActivePieceCategoryConfiguration(new PieceCategoryConfiguration());
                LastAppliedPieceCategorySyncedPayload = null;
            }

            EntryChanges.RequireFullApply();
        }

        return cleanupSucceeded;
    }

    private static bool RunWorldShutdownStep(string name, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"Failed to restore DataForge piece {name} during world shutdown: {ex}");
            return false;
        }
    }

    private static void RestoreAppliedPrefabDefinitionsForWorldShutdown()
    {
        if (ZNetScene.instance == null || RuntimeAppliedPieceKeys.Count == 0)
        {
            return;
        }

        foreach ((string prefabName, Piece piece) in GetPrefabPieces(RuntimeAppliedPieceKeys))
        {
            GameObject gameObject = piece.gameObject;
            RestorePieceVisualState(gameObject);
            bool hasStationExtension = false;
            bool hasCraftingStation = false;
            if (Baselines.TryGetValue(prefabName, out PieceBaseline? baseline))
            {
                hasStationExtension = baseline.Definition.StationExtension != null;
                hasCraftingStation = baseline.Definition.CraftingStation != null;
                ApplyDefinition(gameObject, baseline.Definition, adjustHealthZdo: false, applyVisuals: true);
            }

            RemoveManagedComponentsIfAbsent(gameObject, hasStationExtension, hasCraftingStation);
        }
    }

    private static void RestoreCapturedPieceTablesForWorldShutdown()
    {
        PieceTable[] pieceTables = PieceTableOrderBaselines.Keys
            .Where(pieceTable => pieceTable != null)
            .ToArray();
        if (pieceTables.Length > 0)
        {
            RestorePieceTableMemberships(pieceTables);
        }
    }

    private static void RestoreAllPieceVisualStates()
    {
        if (ZNetScene.instance != null)
        {
            foreach ((string PrefabName, Piece Piece) pair in GetPrefabPieces().ToList())
            {
                RestorePieceVisualState(pair.Piece.gameObject);
            }
        }

        foreach (Piece piece in Piece.s_allPieces.ToList())
        {
            if (piece != null && piece.gameObject != null)
            {
                RestorePieceVisualState(piece.gameObject);
            }
        }
    }

    internal static void OnPieceTablesReady()
    {
        if (ZNetScene.instance == null)
        {
            return;
        }

        GameDataReady = true;
        PieceTablesReady = true;
        RefreshPieceCategoryRegistry();
        if (ShouldSkipRemoteClientBaselineWork())
        {
            return;
        }

        DataForgeLifecycleStep.Run("piece generated-artifact write", WriteGeneratedArtifacts);
        ApplyCurrentConfiguration();
        DataForgeLifecycleStep.Run("piece category generated-artifact write", WritePieceCategoryReferenceArtifact);
    }

    private static void ReadYamlValues(object sender, FileSystemEventArgs e)
    {
        if (!ShouldReloadForFileEvent(e))
        {
            return;
        }

        if (IsIconFileEvent(e))
        {
            if (ItemVisualOverrides.IsIconFile(e.FullPath))
            {
                ItemVisualOverrides.MarkIconFileChanged(e.FullPath);
            }

            if (e is RenamedEventArgs renamed &&
                ItemVisualOverrides.IsIconFile(renamed.OldFullPath))
            {
                ItemVisualOverrides.MarkIconFileChanged(renamed.OldFullPath);
            }

            lock (StateLock)
            {
                EntryChanges.RequireFullApply();
            }
        }

        ReloadDebouncer?.Schedule();
    }

    private static void ReloadYamlValues()
    {
        try
        {
            DataForgePlugin.Log.LogDebug("Reloading piece YAML files...");
            ReloadFromDiskAndSync();
            DataForgePlugin.Log.LogInfo("Piece YAML reload complete.");
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Error reloading piece YAML files: {ex}");
        }
    }

    private static void OnWatcherError(object sender, ErrorEventArgs e)
    {
        DataForgePlugin.Log.LogWarning($"Piece file watcher lost events; scheduling a full reload: {e.GetException().Message}");
        ItemVisualOverrides.MarkAllIconFilesChanged();
        lock (StateLock)
        {
            EntryChanges.RequireFullApply();
            PieceCategoryConfigurationVersion++;
        }

        if (!DataForgeFileWatcher.TryRecreate(
                "piece",
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

        if (IsOverrideFile(e.FullPath) || IsPieceCategoryOverrideFile(e.FullPath) || ItemVisualOverrides.IsIconFile(e.FullPath))
        {
            return true;
        }

        return e is RenamedEventArgs renamed &&
               (IsOverrideFile(renamed.OldFullPath) ||
                IsPieceCategoryOverrideFile(renamed.OldFullPath) ||
                ItemVisualOverrides.IsIconFile(renamed.OldFullPath));
    }

    private static bool IsIconFileEvent(FileSystemEventArgs e)
    {
        return ItemVisualOverrides.IsIconFile(e.FullPath) ||
               e is RenamedEventArgs renamed && ItemVisualOverrides.IsIconFile(renamed.OldFullPath);
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

        if (!DataForgeOverrideFiles.TryDeserializeEntries(payload, "synced piece payload", DeserializeEntries, out List<PieceEntry> entries))
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

    private static void OnSyncedPieceCategoryPayloadChanged()
    {
        if (DataForgePlugin.UsesLocalAuthorityFiles)
        {
            return;
        }

        string payload = SyncedPieceCategoryPayload?.Value ?? "";
        ApplySyncedPieceCategoryPayload(payload);
    }

    private static void ApplySyncedPieceCategoryPayload(string payload)
    {
        if (string.Equals(LastAppliedPieceCategorySyncedPayload, payload, StringComparison.Ordinal))
        {
            return;
        }

        PieceCategoryConfiguration configuration;
        try
        {
            configuration = DeserializePieceCategoryConfiguration(payload, "synced piece category payload");
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError(
                $"Synced piece category payload was rejected; keeping the last-known-good configuration. {ex.Message}");
            return;
        }

        LastAppliedPieceCategorySyncedPayload = payload;
        lock (StateLock)
        {
            SetActivePieceCategoryConfiguration(configuration);
        }

        ApplyCurrentConfiguration();
    }

    private static void SetActiveEntries(List<PieceEntry> entries)
    {
        EntryChanges.SetEntries(entries);
        ActiveEntries = entries;
        ActiveRuntimeEntriesByPiece = BuildActiveRuntimeEntriesByPiece(entries);
        DataForgeIconSync.ScheduleManifestRefresh();
    }

    internal static void CollectReferencedExplicitIconNames(ISet<string> names)
    {
        if (!DataForgePlugin.PieceOverridesEnabled)
        {
            return;
        }

        lock (StateLock)
        {
            foreach (PieceEntry entry in ActiveEntries)
            {
                string? icon = entry.Visual?.Icon;
                if (entry.Override &&
                    !entry.Remove &&
                    entry.HasRuntimeDefinition &&
                    !string.IsNullOrWhiteSpace(icon) &&
                    !ItemVisualOverrides.IsAutoIconValue(icon))
                {
                    names.Add(icon!);
                }
            }
        }
    }

    internal static void OnSyncedIconsChanged(ISet<string> changedNames)
    {
        bool referencesChangedIcon;
        lock (StateLock)
        {
            referencesChangedIcon = ActiveEntries.Any(entry =>
                entry.Override &&
                !entry.Remove &&
                entry.HasRuntimeDefinition &&
                DataForgeIconSync.ContainsLogicalIconName(changedNames, entry.Visual?.Icon, excludeAuto: true));
            if (referencesChangedIcon)
            {
                EntryChanges.RequireFullApply();
            }
        }

        if (referencesChangedIcon)
        {
            ApplyCurrentConfiguration();
        }
    }

    private static void SetActivePieceCategoryConfiguration(PieceCategoryConfiguration configuration)
    {
        string signature = SerializePieceCategoryConfiguration(configuration);
        if (string.Equals(ActivePieceCategoryConfigurationSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        ActivePieceCategoryConfiguration = configuration;
        ActivePieceCategoryConfigurationSignature = signature;
        PieceCategoryConfigurationVersion++;
        ReportedPieceCategoryConfigurationIssues.Clear();
    }

    private static Dictionary<string, List<PieceEntry>> BuildActiveRuntimeEntriesByPiece(List<PieceEntry> entries)
    {
        Dictionary<string, List<PieceEntry>> entriesByPiece = new(StringComparer.OrdinalIgnoreCase);
        foreach (PieceEntry entry in entries)
        {
            if (!entry.Override ||
                entry.Remove ||
                !entry.HasRuntimeDefinition ||
                string.IsNullOrWhiteSpace(entry.Piece))
            {
                continue;
            }

            if (!entriesByPiece.TryGetValue(entry.Piece, out List<PieceEntry> pieceEntries))
            {
                pieceEntries = new List<PieceEntry>();
                entriesByPiece[entry.Piece] = pieceEntries;
            }

            pieceEntries.Add(entry);
        }

        return entriesByPiece;
    }

    private static void PublishPayload(string payload)
    {
        DataForgeSync.PublishPayload(SyncedPayload, DomainName, payload);
    }

    private static void PublishPieceCategoryPayload(string payload)
    {
        DataForgeSync.PublishPayload(SyncedPieceCategoryPayload, "piece categories", payload);
    }

    private static bool TryLoadEntriesFromDisk(out List<PieceEntry> entries)
    {
        return DataForgeOverrideFiles.TryLoadEntries(GetOverrideFiles(), DeserializeEntries, out entries);
    }

    private static bool TryLoadPieceCategoryConfigurationFromDisk(out PieceCategoryConfiguration configuration)
    {
        string path = Path.Combine(ConfigDirectory, PieceCategoryFileName);
        try
        {
            string yaml = File.Exists(path) ? File.ReadAllText(path) : "";
            configuration = DeserializePieceCategoryConfiguration(yaml, path);
            return true;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError(
                $"Piece category reload failed; keeping the last-known-good configuration. {ex.Message}");
            configuration = new PieceCategoryConfiguration();
            return false;
        }
    }

    private static PieceCategoryConfiguration DeserializePieceCategoryConfiguration(string yaml, string source)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new PieceCategoryConfiguration();
        }

        try
        {
            YamlStream stream = new();
            using StringReader reader = new(yaml);
            stream.Load(reader);
            if (stream.Documents.Count == 0 ||
                stream.Documents[0].RootNode is YamlScalarNode { Value: null })
            {
                return new PieceCategoryConfiguration();
            }

            YamlNode root = stream.Documents[0].RootNode;
            if (stream.Documents.Count != 1 || root is not YamlMappingNode tables)
            {
                throw new InvalidDataException(
                    $"{DataForgeLogContext.FormatSourceLine(source, root.Start.Line)}: root must be a mapping of piece tables to category lists.");
            }

            return NormalizePieceCategoryConfiguration(tables, source);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Failed to parse {source}: {ex.Message}", ex);
        }
    }

    private static PieceCategoryConfiguration NormalizePieceCategoryConfiguration(
        YamlMappingNode tables,
        string source)
    {
        PieceCategoryConfiguration configuration = new();
        Dictionary<string, Dictionary<string, string>> moveTargetsBySourceTable =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<YamlNode, YamlNode> table in tables.Children)
        {
            string tableContext = DataForgeLogContext.FormatSourceLine(source, table.Key.Start.Line);
            if (table.Key is not YamlScalarNode tableNameNode)
            {
                throw new InvalidDataException($"{tableContext}: piece table names must be scalar values.");
            }

            string pieceTableName = tableNameNode.Value?.Trim() ?? "";
            if (pieceTableName.Length == 0)
            {
                throw new InvalidDataException($"{tableContext}: piece table name cannot be empty.");
            }

            if (configuration.TableContexts.ContainsKey(pieceTableName))
            {
                throw new InvalidDataException(
                    $"{tableContext}: piece table '{pieceTableName}' is defined more than once.");
            }

            configuration.TableContexts[pieceTableName] = tableContext;

            List<PieceCategoryOrderEntry> entries = new();
            HashSet<string> seenOrderEntries = new(StringComparer.Ordinal);
            Dictionary<string, string> labelsByCategory = new(StringComparer.Ordinal);
            if (table.Value is YamlScalarNode { Value: null })
            {
                continue;
            }

            if (table.Value is not YamlSequenceNode categoryEntries)
            {
                throw new InvalidDataException(
                    $"{DataForgeLogContext.FormatSourceLine(source, table.Value.Start.Line)}: piece table '{pieceTableName}' must contain a category list.");
            }

            int entryIndex = 0;
            foreach (YamlNode rawEntry in categoryEntries.Children)
            {
                entryIndex++;
                string entryContext = DataForgeLogContext.FormatSource(source, entryIndex, rawEntry.Start.Line);
                string value;
                string? sourcePieceTable = null;
                if (rawEntry is YamlScalarNode scalarEntry)
                {
                    value = scalarEntry.Value?.Trim() ?? "";
                }
                else if (rawEntry is YamlMappingNode mappingEntry && mappingEntry.Children.Count == 1)
                {
                    KeyValuePair<YamlNode, YamlNode> move = mappingEntry.Children.Single();
                    if (move.Key is not YamlScalarNode moveCategory || move.Value is not YamlScalarNode moveSource)
                    {
                        throw new InvalidDataException(
                            $"{entryContext}: {pieceTableName} category move must use scalar category and piece-table names.");
                    }

                    value = moveCategory.Value?.Trim() ?? "";
                    sourcePieceTable = moveSource.Value?.Trim() ?? "";
                    if (sourcePieceTable.Length == 0)
                    {
                        throw new InvalidDataException(
                            $"{entryContext}: {pieceTableName} category move has no source piece table.");
                    }
                }
                else
                {
                    throw new InvalidDataException(
                        $"{entryContext}: {pieceTableName} entry must be a category string or a single category-to-source mapping.");
                }

                ParsePieceCategoryDescriptor(
                    value,
                    entryContext,
                    pieceTableName,
                    out string categoryName,
                    out string? label);
                if (categoryName.Length == 0)
                {
                    throw new InvalidDataException(
                        $"{entryContext}: {pieceTableName} category cannot be empty.");
                }

                if (IsOwnerManagedHomesteadCategoryName(categoryName))
                {
                    DataForgeLogContext.Warning(
                        $"{entryContext}: {pieceTableName} category '{HomesteadCategoryName}' was ignored because Homestead owns its tab order and label.");
                    continue;
                }

                if (sourcePieceTable == null && !seenOrderEntries.Add(categoryName))
                {
                    throw new InvalidDataException(
                        $"{entryContext}: {pieceTableName} category '{categoryName}' has more than one order/label entry.");
                }

                if (label != null &&
                    labelsByCategory.TryGetValue(categoryName, out string existingLabel) &&
                    !existingLabel.Equals(label, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"{entryContext}: {pieceTableName} category '{categoryName}' has conflicting labels " +
                        $"'{existingLabel}' and '{label}'.");
                }

                if (label != null)
                {
                    labelsByCategory[categoryName] = label;
                }

                if (sourcePieceTable != null)
                {
                    if (!moveTargetsBySourceTable.TryGetValue(
                            sourcePieceTable,
                            out Dictionary<string, string>? targetsByCategory))
                    {
                        targetsByCategory = new Dictionary<string, string>(StringComparer.Ordinal);
                        moveTargetsBySourceTable[sourcePieceTable] = targetsByCategory;
                    }

                    if (targetsByCategory.TryGetValue(categoryName, out string previousTarget))
                    {
                        throw new InvalidDataException(
                            $"{entryContext}: source category '{sourcePieceTable}.{categoryName}' is already moved to " +
                            $"'{previousTarget}' and cannot also move to '{pieceTableName}'.");
                    }

                    targetsByCategory[categoryName] = pieceTableName;
                }

                entries.Add(new PieceCategoryOrderEntry(categoryName, label, sourcePieceTable, entryContext));
            }

            if (entries.Count > 0)
            {
                configuration.Tables[pieceTableName] = entries;
            }
        }

        return configuration;
    }

    private static void ParsePieceCategoryDescriptor(
        string value,
        string entryContext,
        string pieceTableName,
        out string categoryName,
        out string? label)
    {
        if (value.Length == 0)
        {
            throw new InvalidDataException(
                $"{entryContext}: {pieceTableName} category cannot be empty.");
        }

        int separatorIndex = value.IndexOf(',');
        categoryName = (separatorIndex >= 0 ? value.Substring(0, separatorIndex) : value).Trim();
        string rawLabel = separatorIndex >= 0 ? value.Substring(separatorIndex + 1).Trim() : "";
        label = rawLabel.Length > 0 ? rawLabel : null;
        if (categoryName.Length == 0)
        {
            throw new InvalidDataException(
                $"{entryContext}: {pieceTableName} entry has no category name.");
        }

        if (label != null && label.StartsWith("&", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{entryContext}: {pieceTableName} category '{categoryName}' uses '{label}'. " +
                "Valheim localization tokens start with '$', not '&'.");
        }
    }

    private static string SerializePieceCategoryConfiguration(PieceCategoryConfiguration configuration)
    {
        YamlMappingNode root = new();
        foreach (KeyValuePair<string, List<PieceCategoryOrderEntry>> table in configuration.Tables
                     .OrderBy(static pair => pair.Key, PieceTableNameComparer.Instance))
        {
            YamlSequenceNode entries = new();
            foreach (PieceCategoryOrderEntry entry in table.Value)
            {
                YamlScalarNode descriptor = new(entry.ToSerializedValue());
                if (entry.SourcePieceTable == null)
                {
                    entries.Add(descriptor);
                    continue;
                }

                YamlMappingNode move = new();
                move.Add(descriptor, new YamlScalarNode(entry.SourcePieceTable));
                entries.Add(move);
            }

            root.Add(new YamlScalarNode(table.Key), entries);
        }

        YamlStream stream = new(new YamlDocument(root));
        using StringWriter writer = new(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static List<PieceEntry> DeserializeEntries(string yaml, string source)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new List<PieceEntry>();
        }

        try
        {
            IReadOnlyList<long> entryLines = DataForgeLogContext.GetLocalTopLevelEntryLines(yaml, source);
            List<PieceEntry>? entries = Deserializer.Deserialize<List<PieceEntry>>(yaml);
            return NormalizeEntries(entries, source, entryLines);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse {source}: {ex.Message}", ex);
        }
    }

    private static List<PieceEntry> NormalizeEntries(
        List<PieceEntry>? entries,
        string source,
        IReadOnlyList<long> entryLines)
    {
        List<PieceEntry> normalized = new();
        if (entries == null)
        {
            return normalized;
        }

        int entryIndex = 0;
        foreach (PieceEntry entry in entries)
        {
            entryIndex++;
            string sourceContext = DataForgeLogContext.FormatSource(
                source,
                entryIndex,
                DataForgeLogContext.GetEntryLine(entryLines, entryIndex));
            if (string.IsNullOrWhiteSpace(entry.Piece))
            {
                DataForgeLogContext.Warning($"{sourceContext}: Skipping piece entry without piece.");
                continue;
            }

            entry.Piece = NormalizePrefabName(entry.Piece);
            entry.SetLogContext($"{sourceContext} piece={entry.Piece}");
            if (IsOwnerManagedHomesteadCategoryName(entry.Category))
            {
                DataForgeLogContext.Warning(
                    $"{entry.LogContext}: category '{HomesteadCategoryName}' is owned by Homestead and cannot be assigned by DataForge; the field was ignored.");
                entry.Category = null;
            }
            else if (IsIgnoredCategoryName(entry.Category))
            {
                entry.Category = null;
            }

            if (IsIgnoredPieceTableName(entry.PieceTable))
            {
                entry.PieceTable = null;
            }

            normalized.Add(entry);
        }

        return normalized;
    }

    private static string SerializeEntries(List<PieceEntry> entries)
    {
        return SparseSerializer.Serialize(entries);
    }

    private static IEnumerable<string> GetOverrideFiles()
    {
        return DataForgeOverrideFiles.GetOverrideFiles(ConfigDirectory, IsOverrideFile);
    }

    private static bool IsOverrideFile(string path)
    {
        return DataForgeOverrideFiles.IsDomainOverrideFile(path, $"{DomainName}.yml", DomainName);
    }

    private static bool IsPieceCategoryOverrideFile(string path)
    {
        return Path.GetFileName(path).Equals(PieceCategoryFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureConfigDirectoryAndDefaultOverride()
    {
        DataForgeOverrideFiles.EnsureDefaultOverride(ConfigDirectory, $"{DomainName}.yml", GetOverrideFiles, DefaultOverrideTemplate);
        Directory.CreateDirectory(ConfigDirectory);
        string pieceCategoryPath = Path.Combine(ConfigDirectory, PieceCategoryFileName);
        if (!File.Exists(pieceCategoryPath))
        {
            File.WriteAllText(pieceCategoryPath, DefaultPieceCategoryTemplate());
        }
    }

    private static string DefaultPieceCategoryTemplate()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# DataForge piece category order, display labels, and category moves.",
            "# Copy piece-table sections from pieceCategory.reference.yml and reorder their category entries here.",
            "# Piece-table keys use the build tool prefab name, such as Hammer, rather than an internal _HammerPieceTable name.",
            "# Category names are exact and case-sensitive. Leading and trailing whitespace is ignored.",
            "# An optional second value overrides the displayed tab label. Use a $token from DataForge/localization for per-language text.",
            "# Use '- Furniture: GB_Parchment_Tool' to move every Furniture piece from that source tool into this section's tool.",
            "# Use '- Furniture, $token: GB_Parchment_Tool' to move and set the destination tab's localized label together.",
            "# An exact category already present on the destination is merged; otherwise the source category is added there.",
            "# Keep one plain category entry for its order/label, then repeat that category as mappings to merge one or more source tools.",
            "# A source tool/category pair can move to only one destination tool.",
            "# A pieces.yml pieceTable value has final priority over a category move. Removing a move restores its baseline membership.",
            "# After moving all categories out of a source tool, write 'GB_Parchment_Tool: []' to leave its section explicitly empty.",
            "# Categories omitted from a configured table keep their relative order after the listed categories.",
            "# A listed category with no matching pieces does not keep or create an empty build tab.",
            "# When Homestead is installed, its owner-managed category is omitted here and always remains last.",
            "#",
            "# Hammer:",
            "# - Misc",
            "# - Furniture, $hud_furniture",
            "# - Furniture: GB_Parchment_Tool",
            "# - Stone Building: GB_Parchment_Tool",
            "# GB_Parchment_Tool: []"
        }) + Environment.NewLine;
    }

    private static string DefaultOverrideTemplate()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# DataForge piece overrides.",
            "# Copy entries from pieces.reference.yml, or run `dataforge:full piece` to generate pieces.full.yml for exhaustive field examples.",
            "# You can also create additional override files like pieces_asdf.yml; DataForge loads pieces.yml and pieces_*.yml together.",
            "# Omitted fields keep the current piece value. Values below are common defaults or examples.",
            "#",
            "# Schema:",
            "# - piece: wood_wall                    # required; piece prefab id.",
            "#   override: true                      # default true; false skips this entire piece entry, including remove.",
            "#   remove: false                       # default false; true hides this piece from build tables without deleting placed pieces.",
            "#   name: $piece_woodwall               # Piece.m_name localization token or text.",
            "#   description: $piece_woodwall_desc   # Piece.m_description localization token or text.",
            "#   pieceTable: Hammer                  # build tool/table to show this piece in. Hammer is the reference default and is omitted there.",
            "#   category: Building                  # exact case-sensitive tab name; an unknown name creates a new tab. Feasts, Food, and Meads are ignored by DataForge.",
            "#                                        # configure tab order and display labels in pieceCategory.yml.",
            "#                                        # Homestead is owner-managed and cannot be assigned through this field while that mod is installed.",
            "#   sortOrder: 100                      # lower appears earlier in the same build tab; omitted keeps original order.",
            "#   needStation: None                   # station prefab/name needed to build; None clears the station requirement.",
            "#   canBeRemoved: true                  # false prevents removing the placed piece with a hammer.",
            "#   health: 100                         # max structural health when the prefab is damageable.",
            "#   comfort: 0, None                    # comfort amount, comfort group: None, Fire, Bed, Banner, Chair, Table, Carpet.",
            "#   visual:",
            "#     scale: 1                          # uniform prefab scale for newly placed pieces; larger values also affect collider/support behavior.",
            "#     material: wood                    # material name from z_materials.reference.txt; replaces piece renderer material slots.",
            "#     icon: auto                        # auto snapshots the piece icon after visual changes; or use a 256*256 png name from DataForge/icon.",
            "#     iconRotation: 23, 51, 25.8        # x, y, z rotation used by icon: auto.",
            "#   resources:",
            "#   - Wood: 2                           # item: amount; add ', false' to disable build resource recovery.",
            "#   sapCollector: Sap, 60, 10           # produced item, seconds per unit, max stored units.",
            "#   beehive: 1200, 4                    # seconds per produced honey, max stored honey.",
            "#   fermenter:",
            "#     duration: 2400                    # fermentation duration in seconds.",
            "#     requiresRoof: true                # false ignores the vanilla roof requirement.",
            "#     requiresCover: true               # false ignores the vanilla 70% cover requirement.",
            "#     conversions:",
            "#     - MeadBaseHealthMedium: MeadHealthMedium, 6 # from item: to item, produced amount.",
            "#   cookingStation:",
            "#     fuel: Wood, true, 10, 60          # fuel item prefab, require external fire, maxFuel, seconds per fuel. None clears fuel.",
            "#     conversions:",
            "#     - BreadDough: Bread, 50           # from item: to item, cook time seconds.",
            "#   smelter:",
            "#     input: Coal, 20, 10               # fuel item prefab; None clears it, maxFuel, maxOre.",
            "#     output: 2, 30                     # fuelPerProduct, seconds per product.",
            "#     requiresRoof: true                # true requires roof cover.",
            "#     conversions:",
            "#     - CopperOre: Copper               # from item: to item.",
            "#   container: 10, 4                    # width, height.",
            "#   stationExtension: forge, 5           # target station prefab/name, max distance. Use None to remove/disable StationExtension.",
            "#   craftingStation:                    # edits an existing CraftingStation, or adds one if missing; omit later to remove DataForge-added station.",
            "#     name: $piece_forge                # CraftingStation.m_name localization token or text.",
            "#     discoveryRange: 4                 # station discovery range.",
            "#     buildRange: 20, 0                 # base build range, extra range per extension level.",
            "#     craftRequiresRoof: false          # true requires roof to use the station.",
            "#     craftRequiresFire: false          # true requires fire to use the station.",
            "#     showBasicRecipes: true            # CraftingStation.m_showBasicRecipies.",
            "#     useDistance: 2                    # interaction distance.",
            "#     useAnimation: 2                   # player crafting animation id.",
            "#     craftingSkill: Crafting           # Skills.SkillType used for craft speed/bonus/raise.",
            "#",
            "# Example:",
            "# - piece: wood_wall",
            "#   health: 250",
            "#   visual:",
            "#     scale: 2",
            "#     material: amber",
            "#     icon: auto",
            "#   stationExtension: None",
            "#   resources:",
            "#   - Wood: 4"
        }) + Environment.NewLine;
    }

    private static void CaptureAllBaselinesIfNeeded()
    {
        if (!ObjectDbReady || ZNetScene.instance == null || ObjectDB.instance == null)
        {
            return;
        }

        int added = 0;
        foreach ((string prefabName, Piece piece) in GetPrefabPieces())
        {
            if (CaptureBaseline(prefabName, piece))
            {
                added++;
            }
        }

        if (added > 0)
        {
            DataForgePlugin.Log.LogInfo($"Captured {added} piece prefab baselines. Tracking {Baselines.Count} total.");
        }
    }

    private static void CaptureBaselinesForPiecesIfNeeded(IEnumerable<string>? prefabNames)
    {
        if (!ObjectDbReady || ZNetScene.instance == null || ObjectDB.instance == null || prefabNames == null)
        {
            return;
        }

        int added = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string prefabName in prefabNames)
        {
            string normalizedName = NormalizePrefabName(prefabName);
            if (normalizedName.Length == 0 || !seen.Add(normalizedName))
            {
                continue;
            }

            GameObject? prefab = ResolvePiecePrefab(normalizedName);
            if (prefab == null || !prefab.TryGetComponent(out Piece piece))
            {
                continue;
            }

            if (CaptureBaseline(GetPrefabName(prefab), piece))
            {
                added++;
            }
        }

        if (added > 0)
        {
            DataForgePlugin.Log.LogInfo($"Captured {added} targeted piece prefab baselines. Tracking {Baselines.Count} total.");
        }
    }

    private static bool CaptureBaseline(string prefabName, Piece piece)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        bool hasBaseline = Baselines.TryGetValue(prefabName, out PieceBaseline? baseline);
        if (hasBaseline && ReferenceEquals(baseline!.Piece, piece))
        {
            return false;
        }

        Baselines[prefabName] = new PieceBaseline(piece, PieceDefinition.From(piece));
        return !hasBaseline;
    }

    private static IEnumerable<(string PrefabName, Piece Piece)> GetPrefabPieces()
    {
        if (ZNetScene.instance == null)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
        {
            if (prefab == null)
            {
                continue;
            }

            Piece piece = prefab.GetComponent<Piece>();
            if (!IsManagedPiece(prefab, piece))
            {
                continue;
            }

            string prefabName = GetPrefabName(prefab);
            if (seen.Add(prefabName))
            {
                yield return (prefabName, piece);
            }
        }
    }

    private static HashSet<string> GetCurrentPrefabPieceNameSet()
    {
        return new HashSet<string>(
            GetPrefabPieces().Select(pair => pair.PrefabName),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyToPrefabDefinitions(
        Dictionary<string, List<PieceEntry>> activeRuntimeEntriesByPiece,
        bool pieceOverridesEnabled,
        HashSet<string>? pieceKeys = null)
    {
        IEnumerable<(string PrefabName, Piece Piece)> pieces = pieceKeys == null
            ? GetPrefabPieces()
            : GetPrefabPieces(pieceKeys);

        foreach ((string prefabName, Piece piece) in pieces)
        {
            ApplyConfiguredState(
                piece.gameObject,
                prefabName,
                activeRuntimeEntriesByPiece,
                pieceOverridesEnabled,
                adjustHealthZdo: false,
                applyVisuals: true);
        }
    }

    private static IEnumerable<(string PrefabName, Piece Piece)> GetPrefabPieces(IEnumerable<string> prefabNames)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string prefabName in prefabNames)
        {
            if (!seen.Add(prefabName))
            {
                continue;
            }

            GameObject? prefab = ResolvePiecePrefab(prefabName);
            if (prefab != null && prefab.TryGetComponent(out Piece piece))
            {
                yield return (GetPrefabName(prefab), piece);
            }
        }
    }

    private static void ApplyToLoadedInstances(
        Dictionary<string, List<PieceEntry>> activeRuntimeEntriesByPiece,
        bool pieceOverridesEnabled,
        HashSet<string>? pieceKeys = null)
    {
        foreach (Piece piece in Piece.s_allPieces.ToList())
        {
            if (piece == null ||
                piece.gameObject == null ||
                !piece.gameObject.scene.IsValid() ||
                !IsManagedPiece(piece.gameObject, piece))
            {
                continue;
            }

            string prefabName = GetPrefabName(piece.gameObject);
            if (pieceKeys != null && !pieceKeys.Contains(prefabName))
            {
                continue;
            }

            ApplyConfiguredState(
                piece.gameObject,
                prefabName,
                activeRuntimeEntriesByPiece,
                pieceOverridesEnabled,
                adjustHealthZdo: true,
                applyVisuals: false);
            RefreshLoadedFermenterEnvironmentState(piece.gameObject);
        }
    }

    private static void RefreshLoadedFermenterEnvironmentState(GameObject gameObject)
    {
        Fermenter? fermenter = gameObject.GetComponent<Fermenter>();
        if (fermenter == null ||
            fermenter.m_nview == null ||
            !fermenter.m_nview.IsValid() ||
            fermenter.m_roofCheckPoint == null)
        {
            return;
        }

        fermenter.UpdateCover(0f, forceUpdate: true);
    }

    private static bool IsManagedPiece(GameObject gameObject, Piece? piece = null)
    {
        return gameObject != null &&
               (piece ?? gameObject.GetComponent<Piece>()) != null &&
               gameObject.GetComponent<WearNTear>() != null;
    }

    private static void ApplyConfiguredState(
        GameObject gameObject,
        string prefabName,
        Dictionary<string, List<PieceEntry>> activeRuntimeEntriesByPiece,
        bool pieceOverridesEnabled,
        bool adjustHealthZdo,
        bool applyVisuals)
    {
        if (applyVisuals)
        {
            RestorePieceVisualState(gameObject);
        }

        bool finalHasStationExtension = false;
        bool finalHasCraftingStation = false;
        if (Baselines.TryGetValue(prefabName, out PieceBaseline? baseline))
        {
            finalHasStationExtension = baseline.Definition.StationExtension != null;
            finalHasCraftingStation = baseline.Definition.CraftingStation != null;
            ApplyDefinition(gameObject, baseline.Definition, adjustHealthZdo, applyVisuals);
        }

        if (!pieceOverridesEnabled)
        {
            RemoveManagedComponentsIfAbsent(gameObject, finalHasStationExtension, finalHasCraftingStation);
            return;
        }

        if (!activeRuntimeEntriesByPiece.TryGetValue(prefabName, out List<PieceEntry>? entries))
        {
            RemoveManagedComponentsIfAbsent(gameObject, finalHasStationExtension, finalHasCraftingStation);
            return;
        }

        foreach (PieceEntry entry in entries)
        {
            PieceDefinition definition = PieceDefinition.From(entry);
            finalHasStationExtension = finalHasStationExtension || definition.StationExtension != null;
            finalHasCraftingStation = finalHasCraftingStation || definition.CraftingStation != null;
            using (DataForgeLogContext.Push(entry.LogContext))
            {
                ApplyDefinition(gameObject, definition, adjustHealthZdo, applyVisuals);
            }
        }

        RemoveManagedComponentsIfAbsent(gameObject, finalHasStationExtension, finalHasCraftingStation);
    }

    private static void RemoveManagedComponentsIfAbsent(GameObject gameObject, bool hasStationExtension, bool hasCraftingStation)
    {
        if (!hasStationExtension)
        {
            RemoveManagedStationExtensionIfPresent(gameObject);
        }

        if (!hasCraftingStation)
        {
            RemoveManagedCraftingStationIfPresent(gameObject);
        }
    }

    private static HashSet<string> GetRuntimeApplyKeys(
        HashSet<string>? changedPieceKeys,
        IEnumerable<string> activeRuntimeKeys)
    {
        if (changedPieceKeys != null)
        {
            return new HashSet<string>(changedPieceKeys, StringComparer.OrdinalIgnoreCase);
        }

        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string key in activeRuntimeKeys)
        {
            keys.Add(key);
        }

        if (RuntimeAppliedPieceKeys.Count > 0)
        {
            foreach (string key in RuntimeAppliedPieceKeys)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static Dictionary<string, int> GetActiveSortOrders(
        IEnumerable<PieceEntry> entries,
        bool pieceOverridesEnabled)
    {
        Dictionary<string, int> sortOrders = new(StringComparer.OrdinalIgnoreCase);
        if (!pieceOverridesEnabled)
        {
            return sortOrders;
        }

        foreach (PieceEntry entry in entries)
        {
            if (entry.Override && !entry.Remove && entry.SortOrder.HasValue)
            {
                sortOrders[entry.Piece] = entry.SortOrder.Value;
            }
        }

        return sortOrders;
    }

    private static Dictionary<string, PieceTableAssignment> GetActivePieceTableAssignments(
        IEnumerable<PieceEntry> entries,
        bool pieceOverridesEnabled)
    {
        Dictionary<string, PieceTableAssignment> assignments = new(StringComparer.OrdinalIgnoreCase);
        if (!pieceOverridesEnabled)
        {
            return assignments;
        }

        foreach (PieceEntry entry in entries)
        {
            string? pieceTable = entry.PieceTable;
            if (entry.Override &&
                !entry.Remove &&
                !string.IsNullOrWhiteSpace(pieceTable) &&
                !IsIgnoredPieceTableName(pieceTable))
            {
                assignments[entry.Piece] = new PieceTableAssignment(pieceTable!.Trim(), entry.LogContext);
            }
        }

        return assignments;
    }

    private static List<PieceCategoryMoveRule> GetActivePieceCategoryMoves(
        PieceCategoryConfiguration configuration,
        bool pieceOverridesEnabled)
    {
        List<PieceCategoryMoveRule> moves = new();
        if (!pieceOverridesEnabled)
        {
            return moves;
        }

        foreach (KeyValuePair<string, List<PieceCategoryOrderEntry>> table in configuration.Tables)
        {
            foreach (PieceCategoryOrderEntry entry in table.Value)
            {
                if (entry.SourcePieceTable != null)
                {
                    moves.Add(new PieceCategoryMoveRule(
                        table.Key,
                        entry.SourcePieceTable,
                        entry.Category,
                        entry.LogContext));
                }
            }
        }

        return moves;
    }

    private static HashSet<string> GetActiveRemovedPieces(
        IEnumerable<PieceEntry> entries,
        bool pieceOverridesEnabled)
    {
        HashSet<string> removedPieces = new(StringComparer.OrdinalIgnoreCase);
        if (!pieceOverridesEnabled)
        {
            return removedPieces;
        }

        foreach (PieceEntry entry in entries)
        {
            if (entry.Override && entry.Remove && !string.IsNullOrWhiteSpace(entry.Piece))
            {
                removedPieces.Add(entry.Piece);
            }
        }

        return removedPieces;
    }

    private static void ApplyDefinition(
        GameObject gameObject,
        PieceDefinition definition,
        bool adjustHealthZdo,
        bool applyVisuals)
    {
        Piece piece = gameObject.GetComponent<Piece>();
        if (piece != null && definition.Piece != null)
        {
            ApplyPieceDefinition(piece, definition.Piece, adjustHealthZdo);
        }

        ApplySupportedComponentDefinitions(gameObject, definition, applyVisuals);
    }

    private static void ApplyPieceDefinition(Piece piece, PieceComponentDefinition definition, bool adjustHealthZdo)
    {
        Copy(definition.Name, value =>
        {
            piece.m_name = value;
            Door door = piece.GetComponent<Door>();
            if (door != null)
            {
                door.m_name = definition.BaselineDoorName ?? value;
            }
        });
        Copy(definition.Description, value => piece.m_description = value);
        if (definition.BaselineCategory.HasValue)
        {
            ApplyCategory(piece, definition.BaselineCategory.Value);
        }
        else
        {
            ApplyCategory(piece, definition.Category);
        }
        ApplyCraftingStation(piece, definition.NeedStation);
        Copy(definition.CanBeRemoved, value => piece.m_canBeRemoved = value);
        ApplyPieceHealth(piece, definition.Health, adjustHealthZdo);
        ApplyComfort(piece, definition.Comfort);
        ApplyResources(piece, definition.Resources);
    }

    private static void ApplyCategory(Piece piece, string? categoryName)
    {
        string trimmedCategoryName = categoryName?.Trim() ?? "";
        if (trimmedCategoryName.Length == 0)
        {
            return;
        }

        if (IsIgnoredCategoryName(trimmedCategoryName))
        {
            return;
        }

        Piece.PieceCategory category;
        if (TryResolvePieceCategory(trimmedCategoryName, out Piece.PieceCategory resolvedCategory))
        {
            if (IsOwnerManagedHomesteadCategory(resolvedCategory))
            {
                return;
            }

            category = resolvedCategory;
        }
        else
        {
            category = PieceTableCategoryGuard.GetOrCreateCustomCategory(trimmedCategoryName);
        }

        ApplyCategory(piece, category);
    }

    private static void ApplyCategory(Piece piece, Piece.PieceCategory category)
    {
        piece.m_category = category;
        foreach (PieceTable pieceTable in GetPieceTablesContaining(piece.gameObject))
        {
            EnsurePieceTableCategory(pieceTable, category);
        }
    }

    private static void ApplyCraftingStation(Piece piece, string? value)
    {
        if (value == null)
        {
            return;
        }

        string stationName = value.Trim();
        string prefabName = GetPrefabName(piece.gameObject);
        if (stationName.Length == 0)
        {
            return;
        }

        if (IsNone(stationName))
        {
            piece.m_craftingStation = null;
            return;
        }

        CraftingStation? station = ResolveCraftingStation(stationName);
        if (station == null)
        {
            DataForgeLogContext.Warning($"{prefabName} has unknown craftingStation '{stationName}'.");
            return;
        }

        piece.m_craftingStation = station;
    }

    private static void ApplyResources(Piece piece, List<PieceResourceDefinition>? resources)
    {
        if (resources == null)
        {
            return;
        }

        List<Piece.Requirement> requirements = new();
        foreach (PieceResourceDefinition resource in resources)
        {
            foreach (KeyValuePair<string, string> pair in resource)
            {
                ItemDrop? itemDrop = ResolveItemDrop(pair.Key);
                if (itemDrop == null)
                {
                    DataForgeLogContext.Warning($"{GetPrefabName(piece.gameObject)} has unknown build resource '{pair.Key}'.");
                    return;
                }

                string[] parts = SplitTuple(pair.Value);
                Piece.Requirement requirement = new()
                {
                    m_resItem = itemDrop,
                    m_amount = Math.Max(0, GetIntPart(parts, 0, 1)),
                    m_amountPerLevel = 0,
                    m_recover = GetBoolPart(parts, 1, true)
                };
                requirements.Add(requirement);
            }
        }

        piece.m_resources = requirements.ToArray();
    }

    private static void ApplyComfort(Piece piece, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        bool wasComfortPiece = piece.m_comfort > 0;
        string[] parts = SplitTuple(value);
        CopyIntPart(parts, 0, parsed => piece.m_comfort = Math.Max(0, parsed));
        if (parts.Length > 1 && parts[1].Length > 0)
        {
            if (Enum.TryParse(parts[1], ignoreCase: true, out Piece.ComfortGroup comfortGroup))
            {
                piece.m_comfortGroup = comfortGroup;
            }
            else
            {
                DataForgeLogContext.Warning($"{GetPrefabName(piece.gameObject)} has unknown comfort group '{parts[1]}'. Expected: None, Fire, Bed, Banner, Chair, Table, Carpet.");
            }
        }

        UpdateComfortPieceRegistration(piece, wasComfortPiece);
    }

    private static void UpdateComfortPieceRegistration(Piece piece, bool wasComfortPiece)
    {
        bool isComfortPiece = piece.m_comfort > 0;
        if (isComfortPiece)
        {
            Piece.s_allComfortPieces.Add(piece);
        }
        else if (wasComfortPiece)
        {
            Piece.s_allComfortPieces.Remove(piece);
        }
    }

    private static void ApplyPieceHealth(Piece piece, float? health, bool adjustHealthZdo)
    {
        if (!health.HasValue)
        {
            return;
        }

        WearNTear wearNTear = piece.GetComponent<WearNTear>();
        if (wearNTear == null)
        {
            return;
        }

        if (health.Value >= 0f)
        {
            ApplyHealth(wearNTear, health.Value, adjustHealthZdo);
            return;
        }

        DataForgeLogContext.Warning($"{GetPrefabName(piece.gameObject)} has invalid negative health; keeping previous value.");
    }

    private static void ApplySupportedComponentDefinitions(
        GameObject gameObject,
        PieceDefinition definition,
        bool applyVisuals)
    {
        if (definition.SapCollector != null)
        {
            SapCollector sapCollector = gameObject.GetComponent<SapCollector>();
            if (sapCollector != null)
            {
                ApplySapCollectorDefinition(sapCollector, definition.SapCollector);
            }
        }

        if (definition.Beehive != null)
        {
            Beehive beehive = gameObject.GetComponent<Beehive>();
            if (beehive != null)
            {
                ApplyBeehiveDefinition(beehive, definition.Beehive);
            }
        }

        if (definition.Fermenter != null)
        {
            Fermenter fermenter = gameObject.GetComponent<Fermenter>();
            if (fermenter != null)
            {
                ApplyFermenterDefinition(fermenter, definition.Fermenter);
            }
        }

        if (definition.CookingStation != null)
        {
            CookingStation cookingStation = gameObject.GetComponent<CookingStation>();
            if (cookingStation != null)
            {
                ApplyCookingStationDefinition(cookingStation, definition.CookingStation);
            }
        }

        if (definition.Smelter != null)
        {
            Smelter smelter = gameObject.GetComponent<Smelter>();
            if (smelter != null)
            {
                ApplySmelterDefinition(smelter, definition.Smelter);
            }
        }

        if (definition.Container != null)
        {
            Container container = gameObject.GetComponent<Container>();
            if (container != null)
            {
                ApplyContainerDefinition(container, definition.Container);
            }
        }

        if (definition.StationExtension != null)
        {
            ApplyStationExtensionDefinition(gameObject, definition.StationExtension);
        }

        if (definition.CraftingStation != null)
        {
            CraftingStation? craftingStation = gameObject.GetComponent<CraftingStation>();
            if (craftingStation == null)
            {
                craftingStation = AddManagedCraftingStation(gameObject);
            }

            if (craftingStation != null)
            {
                ApplyCraftingStationComponentDefinition(craftingStation, definition.CraftingStation);
                EnsureCraftingStationRuntimeRegistration(craftingStation);
            }
        }

        if (applyVisuals && definition.Visual != null)
        {
            ApplyPieceVisualDefinition(gameObject, definition.Visual);
        }
    }

    private static void ApplySapCollectorDefinition(SapCollector sapCollector, string definition)
    {
        if (!string.IsNullOrWhiteSpace(definition))
        {
            string[] parts = SplitTuple(definition);
            if (parts.Length > 0 && parts[0].Length > 0)
            {
                ItemDrop? itemDrop = ResolveItemDrop(parts[0]);
                if (itemDrop == null)
                {
                    DataForgeLogContext.Warning($"{GetPrefabName(sapCollector.gameObject)} has unknown sapCollector production item '{parts[0]}'.");
                }
                else
                {
                    sapCollector.m_spawnItem = itemDrop;
                }
            }

            CopyFloatPart(parts, 1, parsed => sapCollector.m_secPerUnit = Math.Max(0f, parsed));
            CopyIntPart(parts, 2, parsed => sapCollector.m_maxLevel = Math.Max(0, parsed));
        }
    }

    private static void ApplyBeehiveDefinition(Beehive beehive, string definition)
    {
        if (string.IsNullOrWhiteSpace(definition))
        {
            return;
        }

        string[] parts = SplitTuple(definition);
        CopyFloatPart(parts, 0, parsed => beehive.m_secPerUnit = Math.Max(0f, parsed));
        CopyIntPart(parts, 1, parsed => beehive.m_maxHoney = Math.Max(0, parsed));
    }

    private static void ApplyFermenterDefinition(Fermenter fermenter, FermenterDefinition definition)
    {
        Copy(definition.Duration, value => fermenter.m_fermentationDuration = Math.Max(0f, value));
        if (definition.Conversions != null &&
            TryBuildFermenterConversions(fermenter, definition.Conversions, out List<Fermenter.ItemConversion> conversions))
        {
            fermenter.m_conversion = conversions;
        }
    }

    private static bool TryGetFermenterEnvironmentRequirements(
        Fermenter fermenter,
        out bool requiresRoof,
        out bool requiresCover)
    {
        requiresRoof = true;
        requiresCover = true;
        if (!DataForgePlugin.PieceOverridesEnabled || fermenter == null || fermenter.gameObject == null)
        {
            return false;
        }

        lock (StateLock)
        {
            if (!ActiveRuntimeEntriesByPiece.TryGetValue(
                    GetPrefabName(fermenter.gameObject),
                    out List<PieceEntry>? entries))
            {
                return false;
            }

            foreach (PieceEntry entry in entries)
            {
                FermenterDefinition? definition = entry.Fermenter;
                if (definition?.RequiresRoof is bool entryRequiresRoof)
                {
                    requiresRoof = entryRequiresRoof;
                }

                if (definition?.RequiresCover is bool entryRequiresCover)
                {
                    requiresCover = entryRequiresCover;
                }
            }
        }

        return !requiresRoof || !requiresCover;
    }

    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.ResetFermentationTimer))]
    private static class FermenterResetFermentationTimerPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Fermenter __instance)
        {
            if (!TryGetFermenterEnvironmentRequirements(
                    __instance,
                    out bool requiresRoof,
                    out bool requiresCover))
            {
                return true;
            }

            bool vanillaEnvironmentFailed = __instance.m_exposed || !__instance.m_hasRoof;
            bool configuredEnvironmentFailed =
                (requiresCover && __instance.m_exposed) ||
                (requiresRoof && !__instance.m_hasRoof);
            return !vanillaEnvironmentFailed || configuredEnvironmentFailed;
        }
    }

    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.UpdateCover), typeof(float), typeof(bool))]
    private static class FermenterUpdateCoverPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Fermenter __instance)
        {
            if (!TryGetFermenterEnvironmentRequirements(
                    __instance,
                    out bool requiresRoof,
                    out bool requiresCover))
            {
                return;
            }

            if (!requiresRoof)
            {
                __instance.m_hasRoof = true;
            }

            if (!requiresCover)
            {
                __instance.m_exposed = false;
            }
        }
    }

    private static bool TryBuildFermenterConversions(
        Fermenter fermenter,
        List<FermenterConversionDefinition> definitions,
        out List<Fermenter.ItemConversion> conversions)
    {
        conversions = new List<Fermenter.ItemConversion>();
        string prefabName = GetPrefabName(fermenter.gameObject);
        foreach (FermenterConversionDefinition definition in definitions)
        {
            foreach (KeyValuePair<string, string> pair in definition)
            {
                string[] parts = SplitTuple(pair.Value);
                if (parts.Length == 0 || parts[0].Length == 0)
                {
                    DataForgeLogContext.Warning($"{prefabName} has fermenter conversion '{pair.Key}' without output item.");
                    return false;
                }

                ItemDrop? from = ResolveItemDrop(pair.Key);
                ItemDrop? to = ResolveItemDrop(parts[0]);
                if (from == null || to == null)
                {
                    DataForgeLogContext.Warning($"{prefabName} has unknown fermenter conversion '{pair.Key}: {pair.Value}'.");
                    return false;
                }

                conversions.Add(new Fermenter.ItemConversion
                {
                    m_from = from,
                    m_to = to,
                    m_producedItems = Math.Max(1, GetIntPart(parts, 1, 4))
                });
            }
        }

        return true;
    }

    private static void ApplyCookingStationDefinition(CookingStation cookingStation, CookingStationDefinition definition)
    {
        bool applyFuel = !string.IsNullOrWhiteSpace(definition.Fuel);
        bool fuelValid = true;
        ItemDrop? fuelItem = cookingStation.m_fuelItem;
        bool useFuel = cookingStation.m_useFuel;
        bool requireFire = cookingStation.m_requireFire;
        int maxFuel = cookingStation.m_maxFuel;
        int secondsPerFuel = cookingStation.m_secPerFuel;
        if (applyFuel)
        {
            string[] parts = SplitTuple(definition.Fuel);
            if (parts.Length > 0 && parts[0].Length > 0)
            {
                if (IsNone(parts[0]))
                {
                    fuelItem = null;
                    useFuel = false;
                }
                else
                {
                    ItemDrop? resolvedFuelItem = ResolveItemDrop(parts[0]);
                    if (resolvedFuelItem == null)
                    {
                        DataForgeLogContext.Warning($"{GetPrefabName(cookingStation.gameObject)} has unknown cookingStation fuel item '{parts[0]}'.");
                        fuelValid = false;
                    }
                    else
                    {
                        fuelItem = resolvedFuelItem;
                        useFuel = true;
                    }
                }
            }

            if (parts.Length > 1 && parts[1].Length > 0 && bool.TryParse(parts[1], out bool parsedRequireFire))
            {
                requireFire = parsedRequireFire;
            }

            if (TryGetIntPart(parts, 2, out int parsedMaxFuel))
            {
                maxFuel = Math.Max(0, parsedMaxFuel);
            }

            if (TryGetIntPart(parts, 3, out int parsedSecondsPerFuel))
            {
                secondsPerFuel = Math.Max(0, parsedSecondsPerFuel);
            }
        }

        if (applyFuel && fuelValid)
        {
            cookingStation.m_fuelItem = fuelItem;
            cookingStation.m_useFuel = useFuel;
            cookingStation.m_requireFire = requireFire;
            cookingStation.m_maxFuel = maxFuel;
            cookingStation.m_secPerFuel = secondsPerFuel;
        }

        if (definition.Conversions != null &&
            TryBuildCookingStationConversions(
                cookingStation,
                definition.Conversions,
                out List<CookingStation.ItemConversion> conversions))
        {
            cookingStation.m_conversion = conversions;
        }
    }

    private static bool TryBuildCookingStationConversions(
        CookingStation cookingStation,
        List<CookingStationConversionDefinition> definitions,
        out List<CookingStation.ItemConversion> conversions)
    {
        conversions = new List<CookingStation.ItemConversion>();
        string prefabName = GetPrefabName(cookingStation.gameObject);
        foreach (CookingStationConversionDefinition definition in definitions)
        {
            foreach (KeyValuePair<string, string> pair in definition)
            {
                string[] parts = SplitTuple(pair.Value);
                if (parts.Length == 0 || parts[0].Length == 0)
                {
                    DataForgeLogContext.Warning($"{prefabName} has cookingStation conversion '{pair.Key}' without output item.");
                    return false;
                }

                ItemDrop? from = ResolveItemDrop(pair.Key);
                ItemDrop? to = ResolveItemDrop(parts[0]);
                if (from == null || to == null)
                {
                    DataForgeLogContext.Warning($"{prefabName} has unknown cookingStation conversion '{pair.Key}: {pair.Value}'.");
                    return false;
                }

                conversions.Add(new CookingStation.ItemConversion
                {
                    m_from = from,
                    m_to = to,
                    m_cookTime = Math.Max(0f, GetFloatPart(parts, 1, 10f))
                });
            }
        }

        return true;
    }

    private static void ApplySmelterDefinition(Smelter smelter, SmelterDefinition definition)
    {
        bool applyInput = !string.IsNullOrWhiteSpace(definition.Input);
        bool inputValid = true;
        ItemDrop? fuelItem = smelter.m_fuelItem;
        int maxFuel = smelter.m_maxFuel;
        int maxOre = smelter.m_maxOre;
        if (applyInput)
        {
            string[] parts = SplitTuple(definition.Input);
            if (parts.Length > 0 && parts[0].Length > 0)
            {
                string fuelItemName = parts[0];
                if (IsNone(fuelItemName))
                {
                    fuelItem = null;
                }
                else
                {
                    ItemDrop? resolvedFuelItem = ResolveItemDrop(fuelItemName);
                    if (resolvedFuelItem == null)
                    {
                        DataForgeLogContext.Warning($"{GetPrefabName(smelter.gameObject)} has unknown smelter fuel item '{fuelItemName}'.");
                        inputValid = false;
                    }
                    else
                    {
                        fuelItem = resolvedFuelItem;
                    }
                }
            }

            if (TryGetIntPart(parts, 1, out int parsedMaxFuel))
            {
                maxFuel = Math.Max(0, parsedMaxFuel);
            }

            if (TryGetIntPart(parts, 2, out int parsedMaxOre))
            {
                maxOre = Math.Max(0, parsedMaxOre);
            }
        }

        bool applyOutput = !string.IsNullOrWhiteSpace(definition.Output);
        int fuelPerProduct = smelter.m_fuelPerProduct;
        float secondsPerProduct = smelter.m_secPerProduct;
        if (applyOutput)
        {
            string[] parts = SplitTuple(definition.Output);
            if (TryGetIntPart(parts, 0, out int parsedFuelPerProduct))
            {
                fuelPerProduct = Math.Max(0, parsedFuelPerProduct);
            }

            if (parts.Length > 1 && parts[1].Length > 0 &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedSecondsPerProduct))
            {
                secondsPerProduct = Math.Max(0f, parsedSecondsPerProduct);
            }
        }

        if (applyInput && inputValid)
        {
            smelter.m_fuelItem = fuelItem;
            smelter.m_maxFuel = maxFuel;
            smelter.m_maxOre = maxOre;
        }

        if (applyOutput)
        {
            smelter.m_fuelPerProduct = fuelPerProduct;
            smelter.m_secPerProduct = secondsPerProduct;
        }

        Copy(definition.RequiresRoof, value => smelter.m_requiresRoof = value);
        if (definition.Conversions != null &&
            TryBuildSmelterConversions(smelter, definition.Conversions, out List<Smelter.ItemConversion> conversions))
        {
            smelter.m_conversion = conversions;
        }
    }

    private static bool TryBuildSmelterConversions(
        Smelter smelter,
        List<SmelterConversionDefinition> definitions,
        out List<Smelter.ItemConversion> conversions)
    {
        conversions = new List<Smelter.ItemConversion>();
        string prefabName = GetPrefabName(smelter.gameObject);
        foreach (SmelterConversionDefinition definition in definitions)
        {
            foreach (KeyValuePair<string, string> pair in definition)
            {
                string output = pair.Value?.Trim() ?? "";
                if (output.Length == 0)
                {
                    DataForgeLogContext.Warning($"{prefabName} has smelter conversion '{pair.Key}' without output item.");
                    return false;
                }

                ItemDrop? from = ResolveItemDrop(pair.Key);
                ItemDrop? to = ResolveItemDrop(output);
                if (from == null || to == null)
                {
                    DataForgeLogContext.Warning($"{prefabName} has unknown smelter conversion '{pair.Key}: {pair.Value}'.");
                    return false;
                }

                conversions.Add(new Smelter.ItemConversion
                {
                    m_from = from,
                    m_to = to
                });
            }
        }

        return true;
    }

    private static void ApplyContainerDefinition(Container container, string definition)
    {
        if (string.IsNullOrWhiteSpace(definition))
        {
            return;
        }

        string[] parts = SplitTuple(definition);
        int width = Math.Max(1, container.m_width);
        int height = Math.Max(1, container.m_height);
        if (TryGetIntPart(parts, 0, out int parsedWidth))
        {
            width = Math.Max(1, parsedWidth);
        }

        if (TryGetIntPart(parts, 1, out int parsedHeight))
        {
            height = Math.Max(1, parsedHeight);
        }

        ApplyContainerSize(container, width, height);
    }

    private static void ApplyContainerSize(Container container, int requestedWidth, int requestedHeight)
    {
        requestedWidth = Math.Max(1, requestedWidth);
        requestedHeight = Math.Max(1, requestedHeight);

        Inventory inventory = container.GetInventory();
        if (inventory == null)
        {
            container.m_width = requestedWidth;
            container.m_height = requestedHeight;
            return;
        }

        int currentWidth = Math.Max(1, inventory.GetWidth());
        int currentHeight = Math.Max(1, inventory.GetHeight());
        int appliedWidth = requestedWidth;
        int appliedHeight = requestedHeight;
        bool reducingWidth = requestedWidth < currentWidth;
        bool reducingHeight = requestedHeight < currentHeight;
        bool guarded = false;

        if ((reducingWidth || reducingHeight) && (container.IsInUse() || inventory.NrOfItems() > 0))
        {
            if (reducingWidth)
            {
                appliedWidth = currentWidth;
            }

            if (reducingHeight)
            {
                appliedHeight = currentHeight;
            }

            guarded = true;
        }

        container.m_width = appliedWidth;
        container.m_height = appliedHeight;
        inventory.m_width = appliedWidth;
        inventory.m_height = appliedHeight;

        if (guarded)
        {
            DataForgePlugin.Log.LogDebug(
                $"{GetPrefabName(container.gameObject)} container resize requested {requestedWidth}x{requestedHeight}, applied {appliedWidth}x{appliedHeight} because loaded container is {(container.IsInUse() ? "open" : "not empty")}.");
        }
    }

    private static void ApplyPieceVisualDefinition(
        GameObject gameObject,
        PieceVisualDefinition definition)
    {
        ApplyVisualScale(gameObject, definition.Scale);
        if (string.IsNullOrWhiteSpace(definition.Material))
        {
            ApplyPieceIconDefinition(gameObject, definition);
            return;
        }

        string prefabName = GetPrefabName(gameObject);
        string materialName = (definition.Material ?? "").Trim();
        Material? material = ItemVisualOverrides.ResolveMaterial(materialName);
        if (material == null)
        {
            DataForgeLogContext.Warning($"{prefabName} has unknown visual material '{materialName}'. Check z_materials.reference.txt.");
            ApplyPieceIconDefinition(gameObject, definition);
            return;
        }

        List<Renderer> renderers = GetPieceVisualRenderers(gameObject);
        if (renderers.Count == 0)
        {
            DataForgeLogContext.Warning($"{prefabName} has no piece renderers for visual material override.");
            ApplyPieceIconDefinition(gameObject, definition);
            return;
        }

        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                continue;
            }

            Material[] updatedMaterials = materials.ToArray();
            for (int index = 0; index < updatedMaterials.Length; index++)
            {
                if (updatedMaterials[index] != null)
                {
                    updatedMaterials[index] = material;
                }
            }

            TrackPieceMaterialOverride(gameObject, renderer, materials, material);
            renderer.sharedMaterials = updatedMaterials;
        }

        ApplyPieceIconDefinition(gameObject, definition);
    }

    private static void ApplyPieceIconDefinition(
        GameObject gameObject,
        PieceVisualDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Icon))
        {
            return;
        }

        Piece piece = gameObject.GetComponent<Piece>();
        if (piece == null)
        {
            return;
        }

        string prefabName = GetPrefabName(gameObject);
        string iconName = definition.Icon!.Trim();
        Sprite? icon;
        if (ItemVisualOverrides.IsAutoIconValue(iconName))
        {
            if (!ItemVisualOverrides.CanRenderAutoIcons())
            {
                return;
            }

            icon = ItemVisualOverrides.ResolveAutoIconSpriteForPrefab(
                "piece",
                prefabName,
                gameObject,
                definition.Material,
                definition.IconRotation);
            if (icon == null)
            {
                DataForgeLogContext.Warning($"{prefabName} could not generate visual.icon auto. Keeping the current piece icon.");
                return;
            }
        }
        else
        {
            icon = ItemVisualOverrides.ResolveIconSpriteFromConfig(iconName);
            if (icon == null)
            {
                DataForgeLogContext.Warning($"{prefabName} has unknown visual icon '{iconName}'. Expected a png under DataForge/icon.");
                return;
            }
        }

        TrackPieceIconOverride(gameObject, piece, icon);
        piece.m_icon = icon;

        CraftingStation craftingStation = gameObject.GetComponent<CraftingStation>();
        if (craftingStation != null)
        {
            craftingStation.m_icon = icon;
        }
    }

    private static void ApplyVisualScale(GameObject gameObject, float? scale)
    {
        if (scale == null)
        {
            return;
        }

        float clampedScale = Math.Max(0.001f, scale.Value);
        gameObject.transform.localScale = new Vector3(clampedScale, clampedScale, clampedScale);
    }

    private static List<Renderer> GetPieceVisualRenderers(GameObject gameObject)
    {
        List<Renderer> renderers = gameObject
            .GetComponentsInChildren<Renderer>(includeInactive: true)
            .Where(renderer => renderer != null && renderer.sharedMaterials is { Length: > 0 } && renderer.receiveShadows)
            .ToList();

        if (renderers.Count > 0)
        {
            return renderers;
        }

        return gameObject
            .GetComponentsInChildren<Renderer>(includeInactive: true)
            .Where(renderer => renderer != null && renderer.sharedMaterials is { Length: > 0 })
            .ToList();
    }

    private static void RestorePieceVisualState(GameObject gameObject)
    {
        RestorePieceVisualIcon(gameObject);
        RestorePieceVisualMaterials(gameObject);
    }

    private static void RestorePieceVisualMaterials(GameObject gameObject)
    {
        DataForgePieceComponentOwnership? ownership =
            gameObject.GetComponent<DataForgePieceComponentOwnership>();
        if (ownership == null || !ownership.HasMaterialOwnership)
        {
            return;
        }

        if (string.Equals(
                ownership.VisualPrefabKey,
                GetPrefabName(gameObject),
                StringComparison.OrdinalIgnoreCase))
        {
            ownership.RestoreMaterialOwnership();
        }
        else
        {
            ownership.ClearMaterialOwnership();
        }

        RemoveOwnershipMarkerIfEmpty(ownership);
    }

    private static void RestorePieceVisualIcon(GameObject gameObject)
    {
        DataForgePieceComponentOwnership? ownership =
            gameObject.GetComponent<DataForgePieceComponentOwnership>();
        if (ownership == null || !ownership.HasIconOwnership)
        {
            return;
        }

        if (!string.Equals(
                ownership.VisualPrefabKey,
                GetPrefabName(gameObject),
                StringComparison.OrdinalIgnoreCase))
        {
            ownership.ClearIconOwnership();
            RemoveOwnershipMarkerIfEmpty(ownership);
            return;
        }

        Piece? piece = gameObject.GetComponent<Piece>();
        CraftingStation? craftingStation = gameObject.GetComponent<CraftingStation>();
        bool craftingStationIsManaged =
            craftingStation != null &&
            ReferenceEquals(ownership.CraftingStation, craftingStation);
        ownership.RestoreIconOwnership(piece, craftingStation, craftingStationIsManaged);
        RemoveOwnershipMarkerIfEmpty(ownership);
    }

    private static void TrackPieceMaterialOverride(
        GameObject gameObject,
        Renderer renderer,
        Material[] originalMaterials,
        Material appliedMaterial)
    {
        DataForgePieceComponentOwnership ownership = GetOrAddPieceOwnership(gameObject);
        ownership.TrackMaterialOwnership(
            GetPrefabName(gameObject),
            renderer,
            originalMaterials,
            appliedMaterial);
    }

    private static void TrackPieceIconOverride(GameObject gameObject, Piece piece, Sprite appliedIcon)
    {
        DataForgePieceComponentOwnership ownership = GetOrAddPieceOwnership(gameObject);
        ownership.TrackIconOwnership(
            GetPrefabName(gameObject),
            piece,
            gameObject.GetComponent<CraftingStation>(),
            appliedIcon);
    }

    private static void ApplyStationExtensionDefinition(GameObject gameObject, string definition)
    {
        string prefabName = GetPrefabName(gameObject);
        if (IsNone(GetStationExtensionStation(definition)))
        {
            RemoveStationExtensions(gameObject);
            return;
        }

        StationExtension extension = gameObject.GetComponent<StationExtension>();
        if (extension == null)
        {
            RestoreRemovedStationExtensions(gameObject);
            extension = gameObject.GetComponent<StationExtension>();
        }

        if (extension == null)
        {
            string stationName = GetStationExtensionStation(definition);
            if (string.IsNullOrWhiteSpace(stationName) || IsNone(stationName))
            {
                DataForgeLogContext.Warning($"{prefabName} stationExtension needs a station when adding a new StationExtension component.");
                return;
            }

            if (!CanAddStationExtension(gameObject, prefabName))
            {
                return;
            }

            extension = gameObject.AddComponent<StationExtension>();
            TrackManagedStationExtension(gameObject, extension);
            StationExtensionTopologyChanged = true;
            extension.m_piece = gameObject.GetComponent<Piece>();
            extension.m_continousConnection = false;
            extension.m_stack = false;
        }

        ApplyStationExtensionDefinition(extension, definition);
    }

    private static void ApplyStationExtensionDefinition(StationExtension extension, string definition)
    {
        CraftingStation? previousStation = extension.m_craftingStation;
        float previousDistance = extension.m_maxStationDistance;
        string[] parts = SplitTuple(definition);
        if (parts.Length > 0 && parts[0].Length > 0)
        {
            if (IsNone(parts[0]))
            {
                RemoveStationExtensions(extension.gameObject);
                return;
            }
            else
            {
                CraftingStation? station = ResolveCraftingStation(parts[0]);
                if (station == null)
                {
                    DataForgeLogContext.Warning($"{GetPrefabName(extension.gameObject)} has unknown stationExtension station '{parts[0]}'.");
                }
                else
                {
                    extension.m_craftingStation = station;
                    EnableStationExtension(extension);
                }
            }
        }

        CopyFloatPart(parts, 1, value => extension.m_maxStationDistance = Math.Max(0f, value));
        if (extension.m_craftingStation != previousStation ||
            Math.Abs(extension.m_maxStationDistance - previousDistance) > 0.0001f)
        {
            StationExtensionTopologyChanged = true;
        }
    }

    private static void RemoveStationExtensions(GameObject gameObject)
    {
        StationExtension[] extensions = gameObject.GetComponents<StationExtension>();
        if (extensions.Length == 0)
        {
            RemoveManagedStationExtensionIfPresent(gameObject);
            return;
        }

        if (!StationExtensionRemovalSnapshots.ContainsKey(gameObject))
        {
            StationExtensionRemovalSnapshots[gameObject] = extensions
                .Where(extension => extension != null)
                .Select(extension => StationExtensionSnapshot.From(
                    extension,
                    IsManagedStationExtension(gameObject, extension)))
                .ToList();
        }

        foreach (StationExtension extension in extensions)
        {
            RemoveStationExtensionComponent(extension);
        }

    }

    private static void RestoreRemovedStationExtensions(GameObject gameObject)
    {
        if (!StationExtensionRemovalSnapshots.TryGetValue(gameObject, out List<StationExtensionSnapshot> snapshots))
        {
            return;
        }

        StationExtensionRemovalSnapshots.Remove(gameObject);
        foreach (StationExtensionSnapshot snapshot in snapshots)
        {
            if (!CanAddStationExtension(gameObject, GetPrefabName(gameObject)))
            {
                continue;
            }

            StationExtension extension = gameObject.AddComponent<StationExtension>();
            snapshot.Apply(extension);
            EnableStationExtension(extension);
            if (snapshot.WasManaged)
            {
                TrackManagedStationExtension(gameObject, extension);
            }
            StationExtensionTopologyChanged = true;
        }
    }

    private static bool CanAddStationExtension(GameObject gameObject, string prefabName)
    {
        if (gameObject.GetComponent<Piece>() == null)
        {
            DataForgeLogContext.Warning($"{prefabName} cannot add stationExtension because it has no Piece component.");
            return false;
        }

        if (gameObject.GetComponent<ZNetView>() == null)
        {
            DataForgeLogContext.Warning($"{prefabName} cannot add stationExtension because StationExtension.Awake requires a ZNetView on the same object.");
            return false;
        }

        return true;
    }

    private static void RemoveStationExtensionComponent(StationExtension extension)
    {
        if (extension == null)
        {
            return;
        }

        GameObject gameObject = extension.gameObject;
        ForgetManagedStationExtension(gameObject, extension);

        StationExtension.m_allExtensions.Remove(extension);
        StationExtensionTopologyChanged = true;
        extension.CancelInvoke();
        extension.StopConnectionEffect();
        try
        {
            UnityEngine.Object.DestroyImmediate(extension);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not immediately remove StationExtension from '{GetPrefabName(extension.gameObject)}': {ex.Message}");
            UnityEngine.Object.Destroy(extension);
        }
    }

    private static void EnableStationExtension(StationExtension extension)
    {
        extension.enabled = true;
        extension.m_piece = extension.GetComponent<Piece>();
        ZNetView zNetView = extension.GetComponent<ZNetView>();
        if (zNetView != null && zNetView.GetZDO() != null && !StationExtension.m_allExtensions.Contains(extension))
        {
            StationExtension.m_allExtensions.Add(extension);
        }

        if (extension.m_continousConnection)
        {
            extension.CancelInvoke("UpdateConnection");
            extension.InvokeRepeating("UpdateConnection", 1f, 4f);
        }
    }

    private static void TrackManagedStationExtension(GameObject gameObject, StationExtension extension)
    {
        DataForgePieceComponentOwnership ownership = GetOrAddPieceOwnership(gameObject);
        ownership.StationExtension = extension;
    }

    private static bool IsManagedStationExtension(GameObject gameObject, StationExtension extension)
    {
        DataForgePieceComponentOwnership? ownership =
            gameObject.GetComponent<DataForgePieceComponentOwnership>();
        return ownership != null && ReferenceEquals(ownership.StationExtension, extension);
    }

    private static void ForgetManagedStationExtension(GameObject gameObject, StationExtension extension)
    {
        DataForgePieceComponentOwnership? ownership =
            gameObject.GetComponent<DataForgePieceComponentOwnership>();
        if (ownership == null || !ReferenceEquals(ownership.StationExtension, extension))
        {
            return;
        }

        ownership.StationExtension = null;
        RemoveOwnershipMarkerIfEmpty(ownership);
    }

    private static void TrackManagedCraftingStation(GameObject gameObject, CraftingStation craftingStation)
    {
        DataForgePieceComponentOwnership ownership = GetOrAddPieceOwnership(gameObject);
        ownership.CraftingStation = craftingStation;
    }

    private static bool IsManagedCraftingStation(GameObject gameObject, CraftingStation craftingStation)
    {
        DataForgePieceComponentOwnership? ownership =
            gameObject.GetComponent<DataForgePieceComponentOwnership>();
        return ownership != null && ReferenceEquals(ownership.CraftingStation, craftingStation);
    }

    private static void ForgetManagedCraftingStation(GameObject gameObject, CraftingStation craftingStation)
    {
        DataForgePieceComponentOwnership? ownership =
            gameObject.GetComponent<DataForgePieceComponentOwnership>();
        if (ownership == null || !ReferenceEquals(ownership.CraftingStation, craftingStation))
        {
            return;
        }

        ownership.CraftingStation = null;
        RemoveOwnershipMarkerIfEmpty(ownership);
    }

    private static void RemoveOwnershipMarkerIfEmpty(DataForgePieceComponentOwnership ownership)
    {
        if (ownership == null ||
            ownership.StationExtension != null ||
            ownership.CraftingStation != null ||
            ownership.HasVisualOwnership)
        {
            return;
        }

        try
        {
            UnityEngine.Object.DestroyImmediate(ownership);
        }
        catch
        {
            UnityEngine.Object.Destroy(ownership);
        }
    }

    private static DataForgePieceComponentOwnership GetOrAddPieceOwnership(GameObject gameObject)
    {
        return gameObject.GetComponent<DataForgePieceComponentOwnership>() ??
               gameObject.AddComponent<DataForgePieceComponentOwnership>();
    }

    private static void RemoveManagedStationExtensionIfPresent(GameObject gameObject)
    {
        StationExtension? extension = gameObject
            .GetComponent<DataForgePieceComponentOwnership>()
            ?.StationExtension;
        if (extension == null)
        {
            return;
        }

        RemoveStationExtensionComponent(extension);
    }

    private static CraftingStation? AddManagedCraftingStation(GameObject gameObject)
    {
        Piece piece = gameObject.GetComponent<Piece>();
        if (piece == null)
        {
            DataForgeLogContext.Warning($"{GetPrefabName(gameObject)} cannot become a craftingStation because it has no Piece component.");
            return null;
        }

        CraftingStation craftingStation = gameObject.AddComponent<CraftingStation>();
        TrackManagedCraftingStation(gameObject, craftingStation);
        CraftingStationTopologyChanged = true;
        StationExtensionTopologyChanged = true;

        craftingStation.name = GetPrefabName(gameObject);
        craftingStation.m_name = !string.IsNullOrWhiteSpace(piece.m_name) ? piece.m_name : craftingStation.name;
        craftingStation.m_icon = piece.m_icon;
        craftingStation.m_roofCheckPoint = gameObject.transform;
        craftingStation.m_connectionPoint = gameObject.transform;
        craftingStation.m_craftRequireRoof = false;
        craftingStation.m_craftRequireFire = false;
        craftingStation.m_useDistance = craftingStation.m_useDistance > 0f ? craftingStation.m_useDistance : 2f;
        craftingStation.m_useAnimation = craftingStation.m_useAnimation != 0 ? craftingStation.m_useAnimation : 2;
        craftingStation.m_craftingSkill = Skills.SkillType.Crafting;
        if (craftingStation.m_craftItemEffects == null)
        {
            craftingStation.m_craftItemEffects = new EffectList();
        }

        if (craftingStation.m_craftItemDoneEffects == null)
        {
            craftingStation.m_craftItemDoneEffects = new EffectList();
        }

        if (craftingStation.m_repairItemDoneEffects == null)
        {
            craftingStation.m_repairItemDoneEffects = new EffectList();
        }

        EnsureCraftingStationRuntimeRegistration(craftingStation);
        return craftingStation;
    }

    private static void RemoveManagedCraftingStationIfPresent(GameObject gameObject)
    {
        CraftingStation? craftingStation = gameObject
            .GetComponent<DataForgePieceComponentOwnership>()
            ?.CraftingStation;
        if (craftingStation == null)
        {
            return;
        }

        ForgetManagedCraftingStation(gameObject, craftingStation);
        CraftingStationTopologyChanged = true;
        StationExtensionTopologyChanged = true;
        craftingStation.CancelInvoke();
        craftingStation.m_attachedExtensions?.Clear();
        CraftingStation.m_allStations.Remove(craftingStation);
        CraftingStation.Instances.Remove(craftingStation);
        try
        {
            UnityEngine.Object.DestroyImmediate(craftingStation);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not immediately remove managed CraftingStation from '{GetPrefabName(gameObject)}': {ex.Message}");
            UnityEngine.Object.Destroy(craftingStation);
        }
    }

    private static void EnsureCraftingStationRuntimeRegistration(CraftingStation craftingStation)
    {
        if (craftingStation == null)
        {
            return;
        }

        bool isManaged = IsManagedCraftingStation(craftingStation.gameObject, craftingStation);
        if (isManaged)
        {
            if (craftingStation.m_roofCheckPoint == null)
            {
                craftingStation.m_roofCheckPoint = craftingStation.transform;
            }

            if (craftingStation.m_connectionPoint == null)
            {
                craftingStation.m_connectionPoint = craftingStation.transform;
            }
        }

        craftingStation.m_updateExtensionTimer = CraftingStation.m_updateExtensionInterval;
        if (!craftingStation.gameObject.scene.IsValid())
        {
            return;
        }

        craftingStation.m_nview = craftingStation.GetComponent<ZNetView>();
        if ((craftingStation.m_nview == null || craftingStation.m_nview.GetZDO() != null) &&
            !CraftingStation.m_allStations.Contains(craftingStation))
        {
            CraftingStation.m_allStations.Add(craftingStation);
        }

        if (!CraftingStation.Instances.Contains(craftingStation))
        {
            CraftingStation.Instances.Add(craftingStation);
        }

        craftingStation.CancelInvoke("CheckFire");
        if (craftingStation.m_craftRequireFire)
        {
            craftingStation.InvokeRepeating("CheckFire", 1f, 1f);
        }
    }

    private static void InvalidateCraftingStationExtensionCaches()
    {
        foreach (CraftingStation station in Resources.FindObjectsOfTypeAll<CraftingStation>())
        {
            if (station == null)
            {
                continue;
            }

            station.m_updateExtensionTimer = CraftingStation.m_updateExtensionInterval;
            station.m_attachedExtensions?.Clear();
        }
    }

    private static string GetStationExtensionStation(string definition)
    {
        string[] parts = SplitTuple(definition);
        return parts.Length > 0 ? parts[0] : "";
    }

    private static void ApplyCraftingStationComponentDefinition(CraftingStation craftingStation, CraftingStationComponentDefinition definition)
    {
        Copy(definition.Name, value => craftingStation.m_name = value);
        Copy(definition.DiscoveryRange, value => craftingStation.m_discoverRange = Math.Max(0f, value));
        if (!string.IsNullOrWhiteSpace(definition.BuildRange))
        {
            string[] parts = SplitTuple(definition.BuildRange);
            CopyFloatPart(parts, 0, value => craftingStation.m_rangeBuild = Math.Max(0f, value));
            CopyFloatPart(parts, 1, value => craftingStation.m_extraRangePerLevel = Math.Max(0f, value));
        }

        Copy(definition.CraftRequiresRoof, value => craftingStation.m_craftRequireRoof = value);
        Copy(definition.CraftRequiresFire, value => craftingStation.m_craftRequireFire = value);
        Copy(definition.ShowBasicRecipes, value => craftingStation.m_showBasicRecipies = value);
        Copy(definition.UseDistance, value => craftingStation.m_useDistance = Math.Max(0f, value));
        Copy(definition.UseAnimation, value => craftingStation.m_useAnimation = Math.Max(0, value));
        if (!string.IsNullOrWhiteSpace(definition.CraftingSkill))
        {
            if (Enum.TryParse(definition.CraftingSkill, ignoreCase: true, out Skills.SkillType skillType))
            {
                craftingStation.m_craftingSkill = skillType;
            }
            else
            {
                DataForgeLogContext.Warning($"{GetPrefabName(craftingStation.gameObject)} has unknown craftingStation craftingSkill '{definition.CraftingSkill}'.");
            }
        }

        craftingStation.m_updateExtensionTimer = CraftingStation.m_updateExtensionInterval;
    }

    private static void CopyBoolPart(string[] parts, int index, Action<bool> assign)
    {
        if (index < parts.Length && parts[index].Length > 0 && bool.TryParse(parts[index], out bool parsed))
        {
            assign(parsed);
        }
    }

    private static void CopyIntPart(string[] parts, int index, Action<int> assign)
    {
        if (TryGetIntPart(parts, index, out int parsed))
        {
            assign(parsed);
        }
    }

    private static bool TryGetIntPart(string[] parts, int index, out int parsed)
    {
        parsed = 0;
        return index < parts.Length && parts[index].Length > 0 &&
               int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static void CopyFloatPart(string[] parts, int index, Action<float> assign)
    {
        if (index < parts.Length && parts[index].Length > 0 &&
            float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            assign(parsed);
        }
    }

    private static int GetIntPart(string[] parts, int index, int defaultValue)
    {
        return index < parts.Length &&
               int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : defaultValue;
    }

    private static float GetFloatPart(string[] parts, int index, float defaultValue)
    {
        return index < parts.Length &&
               float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : defaultValue;
    }

    private static bool GetBoolPart(string[] parts, int index, bool defaultValue)
    {
        return index < parts.Length && bool.TryParse(parts[index], out bool parsed)
            ? parsed
            : defaultValue;
    }

    private static ItemDrop? ResolveItemDrop(string prefabName)
    {
        if (ObjectDB.instance != null)
        {
            GameObject itemPrefab = ObjectDB.instance.GetItemPrefab(prefabName);
            if (itemPrefab != null && itemPrefab.TryGetComponent(out ItemDrop itemDrop))
            {
                return itemDrop;
            }
        }

        if (ZNetScene.instance != null)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab != null && prefab.TryGetComponent(out ItemDrop itemDrop))
            {
                return itemDrop;
            }
        }

        return null;
    }

    private static CraftingStation? ResolveCraftingStation(string stationName)
    {
        if (ZNetScene.instance != null)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(stationName);
            if (prefab != null && prefab.TryGetComponent(out CraftingStation station))
            {
                return station;
            }
        }

        foreach ((_, Piece piece) in GetPrefabPieces())
        {
            CraftingStation station = piece.GetComponent<CraftingStation>();
            if (station == null)
            {
                continue;
            }

            if (piece.name.Equals(stationName, StringComparison.OrdinalIgnoreCase) ||
                piece.gameObject.name.Equals(stationName, StringComparison.OrdinalIgnoreCase) ||
                station.name.Equals(stationName, StringComparison.OrdinalIgnoreCase) ||
                station.m_name.Equals(stationName, StringComparison.OrdinalIgnoreCase))
            {
                return station;
            }
        }

        return null;
    }

    private static IEnumerable<PieceTable> GetAllPieceTables(bool includeIgnored = false)
    {
        HashSet<PieceTable> seen = new();

        foreach ((string itemName, PieceTable pieceTable) in GetBuildPieceOwnerTables())
        {
            if (pieceTable != null &&
                (includeIgnored || !IsIgnoredPieceTableName(itemName)) &&
                (includeIgnored || !IsIgnoredPieceTableName(pieceTable.name)) &&
                seen.Add(pieceTable))
            {
                yield return pieceTable;
            }
        }

        foreach (PieceTable pieceTable in Resources.FindObjectsOfTypeAll<PieceTable>())
        {
            if (pieceTable != null &&
                (includeIgnored || !IsIgnoredPieceTableName(pieceTable.name)) &&
                seen.Add(pieceTable))
            {
                yield return pieceTable;
            }
        }
    }

    private static IEnumerable<PieceTable> GetPieceTablesContaining(GameObject piecePrefab, bool includeIgnored = false)
    {
        foreach (PieceTable pieceTable in GetAllPieceTables(includeIgnored))
        {
            if (pieceTable.m_pieces != null && pieceTable.m_pieces.Contains(piecePrefab))
            {
                yield return pieceTable;
            }
        }
    }

    private static void EnsurePieceTableCategory(PieceTable pieceTable, Piece.PieceCategory category)
    {
        bool alreadyPresent = pieceTable.m_categories?.Contains(category) == true;
        PieceTableCategoryGuard.EnsureCategory(pieceTable, category);
        if (alreadyPresent || pieceTable.m_categories?.Contains(category) != true)
        {
            return;
        }

        if (!InsertedPieceTableCategories.TryGetValue(pieceTable, out HashSet<Piece.PieceCategory>? categories))
        {
            categories = new HashSet<Piece.PieceCategory>();
            InsertedPieceTableCategories[pieceTable] = categories;
        }

        categories.Add(category);
    }

    private static void PruneUnusedInsertedPieceTableCategories()
    {
        foreach (KeyValuePair<PieceTable, HashSet<Piece.PieceCategory>> pair in InsertedPieceTableCategories.ToArray())
        {
            foreach (Piece.PieceCategory category in pair.Value.ToArray())
            {
                if (PieceTableCategoryGuard.RemoveOwnedCategoryIfUnused(pair.Key, category))
                {
                    pair.Value.Remove(category);
                }
            }

            if (pair.Value.Count == 0)
            {
                InsertedPieceTableCategories.Remove(pair.Key);
            }
        }
    }

    private static void CapturePieceTableOrderBaselinesIfNeeded(IEnumerable<PieceTable> pieceTables)
    {
        if (!PieceTablesReady)
        {
            return;
        }

        foreach (PieceTable pieceTable in pieceTables)
        {
            CapturePieceTableOrderBaseline(pieceTable);
        }
    }

    private static void CapturePieceTableOrderBaseline(PieceTable pieceTable)
    {
        if (!pieceTable || pieceTable.m_pieces == null || PieceTableOrderBaselines.ContainsKey(pieceTable))
        {
            return;
        }

        PieceTableCategoryGuard.Normalize(pieceTable);
        PieceTableOrderBaselines[pieceTable] = pieceTable.m_pieces
            .Where(piece => piece != null)
            .ToList();
    }

    private static void RestorePieceCategoryMoveBaselines()
    {
        foreach (KeyValuePair<GameObject, Piece.PieceCategory> baseline in PieceCategoryMoveBaselines.ToArray())
        {
            Piece? piece = baseline.Key ? baseline.Key.GetComponent<Piece>() : null;
            if (piece != null)
            {
                piece.m_category = baseline.Value;
            }
        }

        PieceCategoryMoveBaselines.Clear();
    }

    private static List<ResolvedPieceCategoryMove> ApplyPieceTableStructure(
        IReadOnlyDictionary<string, PieceTableAssignment> pieceTableAssignments,
        IReadOnlyDictionary<string, int> sortOrders,
        IReadOnlyCollection<string> removedPieces,
        IReadOnlyList<PieceCategoryMoveRule> pieceCategoryMoves)
    {
        if (!PieceTablesReady)
        {
            return new List<ResolvedPieceCategoryMove>();
        }

        List<PendingPieceCategoryMove> pendingCategoryMoves = ResolvePieceCategoryMoveTables(pieceCategoryMoves);
        bool shouldTouchPieceTables =
            pieceTableAssignments.Count > 0 ||
            removedPieces.Count > 0 ||
            sortOrders.Count > 0 ||
            pieceCategoryMoves.Count > 0 ||
            PieceTableMembershipWasApplied ||
            PieceTableSortWasApplied;
        if (!shouldTouchPieceTables)
        {
            return new List<ResolvedPieceCategoryMove>();
        }

        HashSet<PieceTable> affectedTables = GetAffectedPieceTables(
            pieceTableAssignments,
            sortOrders,
            removedPieces,
            pendingCategoryMoves,
            includePreviouslyTouchedTables: PieceTableMembershipWasApplied || PieceTableSortWasApplied);
        if (affectedTables.Count == 0)
        {
            return new List<ResolvedPieceCategoryMove>();
        }

        CapturePieceTableOrderBaselinesIfNeeded(affectedTables);
        RestorePieceTableMemberships(affectedTables);

        List<ResolvedPieceCategoryMove> appliedCategoryMoves =
            ApplyPieceCategoryMoves(pendingCategoryMoves, affectedTables, pieceTableAssignments);

        if (pieceTableAssignments.Count > 0)
        {
            ApplyPieceTableAssignments(pieceTableAssignments, affectedTables);
        }

        if (removedPieces.Count > 0)
        {
            ApplyPieceTableRemovals(removedPieces, affectedTables);
        }

        if (sortOrders.Count == 0)
        {
            RefreshLocalBuildPieces();
            return appliedCategoryMoves;
        }

        foreach (PieceTable pieceTable in affectedTables)
        {
            ApplyPieceTableSortOrder(pieceTable, sortOrders);
        }

        RefreshLocalBuildPieces();
        return appliedCategoryMoves;
    }

    private static HashSet<PieceTable> GetAffectedPieceTables(
        IReadOnlyDictionary<string, PieceTableAssignment> pieceTableAssignments,
        IReadOnlyDictionary<string, int> sortOrders,
        IReadOnlyCollection<string> removedPieces,
        IReadOnlyCollection<PendingPieceCategoryMove> pieceCategoryMoves,
        bool includePreviouslyTouchedTables)
    {
        HashSet<PieceTable> affectedTables = new(ReferenceComparer<PieceTable>.Instance);
        if (includePreviouslyTouchedTables)
        {
            foreach (PieceTable pieceTable in PieceTableOrderBaselines.Keys)
            {
                AddPieceTableIfValid(affectedTables, pieceTable);
            }
        }

        foreach (KeyValuePair<string, PieceTableAssignment> assignment in pieceTableAssignments)
        {
            GameObject? prefab = ResolvePiecePrefab(assignment.Key);
            if (prefab != null)
            {
                foreach (PieceTable pieceTable in GetPieceTablesContaining(prefab))
                {
                    AddPieceTableIfValid(affectedTables, pieceTable);
                }
            }

            PieceTable? target = ResolvePieceTable(assignment.Value.PieceTable);
            AddPieceTableIfValid(affectedTables, target);
        }

        foreach (PendingPieceCategoryMove move in pieceCategoryMoves)
        {
            AddPieceTableIfValid(affectedTables, move.Source);
            AddPieceTableIfValid(affectedTables, move.Target);
        }

        foreach (string pieceName in removedPieces.Concat(sortOrders.Keys))
        {
            GameObject? prefab = ResolvePiecePrefab(pieceName);
            if (prefab == null)
            {
                continue;
            }

            foreach (PieceTable pieceTable in GetPieceTablesContaining(prefab))
            {
                AddPieceTableIfValid(affectedTables, pieceTable);
            }
        }

        return affectedTables;
    }

    private static List<PendingPieceCategoryMove> ResolvePieceCategoryMoveTables(
        IReadOnlyList<PieceCategoryMoveRule> rules)
    {
        List<PendingPieceCategoryMove> resolved = new();
        Dictionary<PieceTable, HashSet<string>> claimedSourceCategories =
            new(ReferenceComparer<PieceTable>.Instance);
        foreach (PieceCategoryMoveRule rule in rules)
        {
            PieceTable? target = ResolvePieceTable(rule.TargetPieceTable);
            if (target == null || IsIgnoredPieceTableName(rule.TargetPieceTable))
            {
                ReportPieceCategoryConfigurationIssue(
                    $"move-target:{rule.TargetPieceTable}:{rule.Category}:{rule.SourcePieceTable}",
                    $"{rule.LogContext}: Cannot move category '{rule.Category}': target piece table '{rule.TargetPieceTable}' was not found or is not supported.");
                continue;
            }

            PieceTable? source = ResolvePieceTable(rule.SourcePieceTable);
            if (source == null || IsIgnoredPieceTableName(rule.SourcePieceTable))
            {
                ReportPieceCategoryConfigurationIssue(
                    $"move-source:{rule.TargetPieceTable}:{rule.Category}:{rule.SourcePieceTable}",
                    $"{rule.LogContext}: Cannot move category '{rule.Category}': source piece table '{rule.SourcePieceTable}' was not found or is not supported.");
                continue;
            }

            if (ReferenceEquals(source, target))
            {
                ReportPieceCategoryConfigurationIssue(
                    $"move-same-table:{rule.TargetPieceTable}:{rule.Category}:{rule.SourcePieceTable}",
                    $"{rule.LogContext}: Category '{rule.Category}' already uses piece table '{rule.TargetPieceTable}'; its move source was ignored.");
                continue;
            }

            if (!claimedSourceCategories.TryGetValue(source, out HashSet<string>? claimedCategories))
            {
                claimedCategories = new HashSet<string>(StringComparer.Ordinal);
                claimedSourceCategories[source] = claimedCategories;
            }

            if (!claimedCategories.Add(rule.Category))
            {
                ReportPieceCategoryConfigurationIssue(
                    $"move-duplicate-source:{RuntimeHelpers.GetHashCode(source)}:{rule.Category}",
                    $"{rule.LogContext}: Cannot move source category '{GetFriendlyPieceTableName(source)}.{rule.Category}' to more than one target; the later move was ignored.");
                continue;
            }

            resolved.Add(new PendingPieceCategoryMove(target, source, rule.Category, rule.LogContext));
        }

        return resolved;
    }

    private static List<ResolvedPieceCategoryMove> ApplyPieceCategoryMoves(
        IReadOnlyList<PendingPieceCategoryMove> moves,
        IReadOnlyCollection<PieceTable> affectedTables,
        IReadOnlyDictionary<string, PieceTableAssignment> explicitAssignments)
    {
        List<ResolvedPieceCategoryMove> applied = new();
        foreach (PendingPieceCategoryMove move in moves)
        {
            if (!TryResolvePieceTableCategory(move.Source, move.CategoryName, out Piece.PieceCategory sourceCategory))
            {
                ReportPieceCategoryConfigurationIssue(
                    $"move-category:{GetFriendlyPieceTableName(move.Target)}:{move.CategoryName}:{GetFriendlyPieceTableName(move.Source)}",
                    $"{move.LogContext}: Cannot move category '{move.CategoryName}': source piece table '{GetFriendlyPieceTableName(move.Source)}' has no exact matching category.");
                continue;
            }

            Piece.PieceCategory targetCategory =
                TryResolvePieceTableCategory(move.Target, move.CategoryName, out Piece.PieceCategory existingTargetCategory)
                    ? existingTargetCategory
                    : sourceCategory;
            List<GameObject> matchingPieces = (move.Source.m_pieces ?? new List<GameObject>())
                .Where(piecePrefab =>
                {
                    Piece? piece = piecePrefab ? piecePrefab.GetComponent<Piece>() : null;
                    return piece != null && piece.m_category == sourceCategory;
                })
                .ToList();
            if (matchingPieces.Count == 0)
            {
                ReportPieceCategoryConfigurationIssue(
                    $"move-empty:{GetFriendlyPieceTableName(move.Target)}:{move.CategoryName}:{GetFriendlyPieceTableName(move.Source)}",
                    $"{move.LogContext}: Found no pieces in category '{move.CategoryName}' on source piece table '{GetFriendlyPieceTableName(move.Source)}'.");
                continue;
            }

            foreach (GameObject piecePrefab in matchingPieces)
            {
                if (!piecePrefab)
                {
                    continue;
                }

                if (explicitAssignments.ContainsKey(GetPrefabName(piecePrefab)))
                {
                    continue;
                }

                Piece? piece = piecePrefab ? piecePrefab.GetComponent<Piece>() : null;
                if (piece != null && piece.m_category != targetCategory)
                {
                    if (!PieceCategoryMoveBaselines.ContainsKey(piecePrefab!))
                    {
                        PieceCategoryMoveBaselines[piecePrefab!] = piece.m_category;
                    }

                    piece.m_category = targetCategory;
                }

                MovePiecePrefabToTable(piecePrefab!, move.Target, affectedTables);
            }

            applied.Add(new ResolvedPieceCategoryMove(move.Source, sourceCategory));
        }

        return applied;
    }

    private static void AddPieceTableIfValid(HashSet<PieceTable> pieceTables, PieceTable? pieceTable)
    {
        if (pieceTable != null && !IsIgnoredPieceTableName(pieceTable.name))
        {
            pieceTables.Add(pieceTable);
        }
    }

    private static void ApplyPieceTableRemovals(IReadOnlyCollection<string> removedPieces, IReadOnlyCollection<PieceTable> affectedTables)
    {
        foreach (string pieceName in removedPieces)
        {
            foreach (PieceTable pieceTable in affectedTables)
            {
                if (!pieceTable || pieceTable.m_pieces == null)
                {
                    continue;
                }

                RemovePieceByPrefabName(pieceTable, pieceName);
                PieceTableCategoryGuard.Normalize(pieceTable);
            }
        }
    }

    private static void RestorePieceTableMemberships(IReadOnlyCollection<PieceTable> affectedTables)
    {
        HashSet<GameObject> capturedPieces = GetCapturedBaselinePieces(affectedTables);
        foreach (PieceTable pieceTable in affectedTables)
        {
            if (!pieceTable || pieceTable.m_pieces == null)
            {
                continue;
            }

            if (!PieceTableOrderBaselines.TryGetValue(pieceTable, out List<GameObject> baseline))
            {
                CapturePieceTableOrderBaseline(pieceTable);
                PieceTableOrderBaselines.TryGetValue(pieceTable, out baseline);
            }

            if (baseline == null)
            {
                PieceTableCategoryGuard.Normalize(pieceTable);
                continue;
            }

            List<GameObject> restored = new();
            HashSet<GameObject> seen = new(ReferenceComparer<GameObject>.Instance);
            foreach (GameObject piece in baseline)
            {
                AddPieceIfValid(restored, seen, piece);
            }

            foreach (GameObject piece in pieceTable.m_pieces)
            {
                if (piece == null || capturedPieces.Contains(piece))
                {
                    continue;
                }

                AddPieceIfValid(restored, seen, piece);
            }

            pieceTable.m_pieces = restored;
            PieceTableCategoryGuard.Normalize(pieceTable);
        }
    }

    private static void ApplyPieceTableAssignments(
        IReadOnlyDictionary<string, PieceTableAssignment> assignments,
        IReadOnlyCollection<PieceTable> affectedTables)
    {
        foreach (KeyValuePair<string, PieceTableAssignment> assignment in assignments.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            using (DataForgeLogContext.Push(assignment.Value.LogContext))
            {
                GameObject? prefab = ResolvePiecePrefab(assignment.Key);
                if (prefab == null)
                {
                    DataForgeLogContext.Warning($"Could not move piece '{assignment.Key}': piece prefab was not found.");
                    continue;
                }

                PieceTable? target = ResolvePieceTable(assignment.Value.PieceTable);
                if (target == null)
                {
                    DataForgeLogContext.Warning($"Could not move piece '{assignment.Key}': pieceTable '{assignment.Value.PieceTable}' was not found.");
                    continue;
                }

                MovePiecePrefabToTable(prefab, target, affectedTables);
            }
        }
    }

    private static void MovePiecePrefabToTable(GameObject prefab, PieceTable target, IReadOnlyCollection<PieceTable> affectedTables)
    {
        string prefabName = GetPrefabName(prefab);
        foreach (PieceTable pieceTable in affectedTables)
        {
            if (!pieceTable || pieceTable.m_pieces == null || ReferenceEquals(pieceTable, target))
            {
                continue;
            }

            RemovePieceByPrefabName(pieceTable, prefabName);
            PieceTableCategoryGuard.Normalize(pieceTable);
        }

        target.m_pieces ??= new List<GameObject>();
        if (!ContainsPieceByPrefabName(target, prefabName))
        {
            target.m_pieces.Add(prefab);
        }

        Piece piece = prefab.GetComponent<Piece>();
        if (piece != null)
        {
            EnsurePieceTableCategory(target, piece.m_category);
        }

        PieceTableCategoryGuard.Normalize(target);
    }

    private static void RemovePieceByPrefabName(PieceTable pieceTable, string prefabName)
    {
        if (pieceTable.m_pieces == null)
        {
            return;
        }

        for (int index = pieceTable.m_pieces.Count - 1; index >= 0; index--)
        {
            GameObject piece = pieceTable.m_pieces[index];
            if (piece == null || GetPrefabName(piece).Equals(prefabName, StringComparison.OrdinalIgnoreCase))
            {
                pieceTable.m_pieces.RemoveAt(index);
            }
        }
    }

    private static bool ContainsPieceByPrefabName(PieceTable pieceTable, string prefabName)
    {
        return pieceTable.m_pieces != null &&
               pieceTable.m_pieces.Any(piece => piece != null && GetPrefabName(piece).Equals(prefabName, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<GameObject> GetCapturedBaselinePieces(IEnumerable<PieceTable> affectedTables)
    {
        HashSet<GameObject> captured = new(ReferenceComparer<GameObject>.Instance);
        foreach (PieceTable pieceTable in affectedTables)
        {
            if (!PieceTableOrderBaselines.TryGetValue(pieceTable, out List<GameObject> pieces))
            {
                continue;
            }

            foreach (GameObject piece in pieces)
            {
                if (piece != null)
                {
                    captured.Add(piece);
                }
            }
        }

        return captured;
    }

    private static void AddPieceIfValid(List<GameObject> pieces, HashSet<GameObject> seen, GameObject? piece)
    {
        if (piece != null && seen.Add(piece))
        {
            pieces.Add(piece);
        }
    }

    private static void ApplyPieceTableSortOrder(PieceTable pieceTable, IReadOnlyDictionary<string, int> sortOrders)
    {
        if (!pieceTable || pieceTable.m_pieces == null)
        {
            return;
        }

        List<PieceOrderItem> orderedPieces = GetPieceTableBaselineOrderedPieces(pieceTable)
            .Select((piece, index) => PieceOrderItem.From(piece, index, sortOrders))
            .Where(item => item.Prefab != null)
            .ToList();
        if (orderedPieces.Count == 0)
        {
            return;
        }

        Dictionary<Piece.PieceCategory, Queue<PieceOrderItem>> sortedByCategory = orderedPieces
            .GroupBy(item => item.Category)
            .ToDictionary(
                group => group.Key,
                group => new Queue<PieceOrderItem>(group
                    .OrderBy(item => item.SortOrder)
                    .ThenBy(item => item.OriginalIndex)));

        List<GameObject> sortedPieces = new(orderedPieces.Count);
        foreach (PieceOrderItem item in orderedPieces)
        {
            sortedPieces.Add(sortedByCategory[item.Category].Dequeue().Prefab);
        }

        pieceTable.m_pieces = sortedPieces;
        PieceTableCategoryGuard.Normalize(pieceTable);
    }

    private static List<GameObject> GetPieceTableBaselineOrderedPieces(PieceTable pieceTable)
    {
        CapturePieceTableOrderBaseline(pieceTable);
        if (pieceTable.m_pieces == null)
        {
            return new List<GameObject>();
        }

        List<GameObject> currentPieces = pieceTable.m_pieces
            .Where(piece => piece != null)
            .ToList();
        if (!PieceTableOrderBaselines.TryGetValue(pieceTable, out List<GameObject> baseline))
        {
            return currentPieces;
        }

        Dictionary<GameObject, int> baselineIndex = new(ReferenceComparer<GameObject>.Instance);
        for (int index = 0; index < baseline.Count; index++)
        {
            GameObject piece = baseline[index];
            if (piece != null && !baselineIndex.ContainsKey(piece))
            {
                baselineIndex[piece] = index;
            }
        }

        return currentPieces
            .Select((piece, index) => new
            {
                Piece = piece,
                CurrentIndex = index,
                HasBaselineIndex = baselineIndex.TryGetValue(piece, out int originalIndex),
                BaselineIndex = originalIndex
            })
            .OrderBy(item => item.HasBaselineIndex ? 0 : 1)
            .ThenBy(item => item.HasBaselineIndex ? item.BaselineIndex : item.CurrentIndex)
            .Select(item => item.Piece)
            .ToList();
    }

    private static string? FormatReferenceCategory(string prefabName, string? fallback)
    {
        GameObject? prefab = ResolvePiecePrefab(prefabName);
        Piece? piece = prefab != null ? prefab.GetComponent<Piece>() : null;
        if (piece != null)
        {
            if (IsOwnerManagedHomesteadCategory(piece.m_category))
            {
                return null;
            }

            return NullIfIgnoredCategory(FormatPieceCategory(piece));
        }

        string fallbackValue = fallback?.Trim() ?? "";
        if (fallbackValue.Length > 0 &&
            TryResolvePieceCategory(fallbackValue, out Piece.PieceCategory category))
        {
            if (IsOwnerManagedHomesteadCategory(category))
            {
                return null;
            }

            return NullIfIgnoredCategory(FormatPieceCategory(category, null, fallbackValue));
        }

        return NullIfIgnoredCategory(fallback);
    }

    private static string? NullIfIgnoredCategory(string? categoryName)
    {
        return string.Equals(categoryName?.Trim(), nameof(Piece.PieceCategory.Misc), StringComparison.OrdinalIgnoreCase) ||
               IsIgnoredCategoryName(categoryName) ||
               IsOwnerManagedHomesteadCategoryName(categoryName)
            ? null
            : categoryName;
    }

    private static bool IsOwnerManagedHomesteadCategoryName(string? categoryName)
    {
        return IsHomesteadLoaded() &&
               string.Equals(categoryName?.Trim(), HomesteadCategoryName, StringComparison.Ordinal);
    }

    private static bool IsOwnerManagedHomesteadCategory(Piece.PieceCategory category)
    {
        return OwnerManagedPieceCategories.Contains(category);
    }

    private static bool IsHomesteadLoaded()
    {
        return BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(HomesteadPluginGuid);
    }

    private static bool IsIgnoredCategoryName(string? categoryName)
    {
        string trimmed = categoryName?.Trim() ?? "";
        return trimmed.Length > 0 && IgnoredCategoryNames.Contains(trimmed.TrimStart('$'));
    }

    private static bool IsIgnoredPieceTableName(string? pieceTableName)
    {
        string trimmed = pieceTableName?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return false;
        }

        return IgnoredPieceTableNames.Contains(trimmed) ||
               IgnoredPieceTableNames.Contains(NormalizePieceTableIdentifier(trimmed));
    }

    private static string FormatPieceCategory(Piece piece)
    {
        return FormatPieceCategory(piece.m_category, piece.gameObject, piece.m_category.ToString());
    }

    private static string FormatPieceCategory(Piece.PieceCategory category, GameObject? piecePrefab, string fallback)
    {
        if (!int.TryParse(category.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return category.ToString();
        }

        if (KnownPieceCategoryNames.TryGetValue(category, out string displayName) &&
            !string.IsNullOrWhiteSpace(displayName) &&
            !IsNumericCategoryName(displayName))
        {
            return displayName;
        }

        if (PieceTableCategoryGuard.TryGetCustomCategoryName(category, out displayName))
        {
            return displayName;
        }

        if (TryGetCategoryDisplayName(category, piecePrefab, out displayName) &&
            !IsNumericCategoryName(displayName))
        {
            return displayName;
        }

        return fallback;
    }

    private static bool TryResolvePieceCategory(string categoryName, out Piece.PieceCategory category)
    {
        if (Enum.TryParse(categoryName, ignoreCase: false, out category))
        {
            return true;
        }

        string normalized = categoryName.Trim();
        if (KnownPieceCategoryValues.TryGetValue(normalized, out category) ||
            PieceTableCategoryGuard.TryResolveKnownCategory(normalized, out category))
        {
            return true;
        }

        if (PieceTableCategoryGuard.TryResolveCustomCategory(normalized, out category))
        {
            return true;
        }

        foreach (PieceTable pieceTable in GetAllPieceTables())
        {
            List<Piece.PieceCategory>? categories = pieceTable ? pieceTable.m_categories : null;
            List<string>? labels = pieceTable ? pieceTable.m_categoryLabels : null;
            if (categories == null || labels == null)
            {
                continue;
            }

            int count = Math.Min(categories.Count, labels.Count);
            for (int index = 0; index < count; index++)
            {
                if (!CategoryLabelMatches(normalized, labels[index]))
                {
                    continue;
                }

                category = categories[index];
                return true;
            }
        }

        return false;
    }

    private static bool TryResolvePieceTableCategory(
        PieceTable pieceTable,
        string categoryName,
        out Piece.PieceCategory category)
    {
        string exactName = categoryName.Trim();
        HashSet<Piece.PieceCategory> checkedCategories = new();
        List<Piece.PieceCategory>? categories = pieceTable ? pieceTable.m_categories : null;
        List<string>? labels = pieceTable ? pieceTable.m_categoryLabels : null;
        if (categories != null)
        {
            for (int index = 0; index < categories.Count; index++)
            {
                Piece.PieceCategory candidate = categories[index];
                checkedCategories.Add(candidate);
                string rawLabel = labels != null && index < labels.Count ? labels[index] ?? "" : "";
                if (GetPieceCategoryReferenceName(candidate, rawLabel).Equals(exactName, StringComparison.Ordinal))
                {
                    category = candidate;
                    return true;
                }
            }
        }

        if (pieceTable && pieceTable.m_pieces != null)
        {
            foreach (GameObject piecePrefab in pieceTable.m_pieces)
            {
                Piece? piece = piecePrefab ? piecePrefab.GetComponent<Piece>() : null;
                if (piece == null || !checkedCategories.Add(piece.m_category))
                {
                    continue;
                }

                if (GetPieceCategoryReferenceName(piece.m_category, "").Equals(exactName, StringComparison.Ordinal))
                {
                    category = piece.m_category;
                    return true;
                }
            }
        }

        category = Piece.PieceCategory.Misc;
        return false;
    }

    private static void RefreshPieceCategoryRegistry()
    {
        KnownPieceCategoryNames.Clear();
        KnownPieceCategoryValues.Clear();
        KnownPieceCategoryNamePriorities.Clear();
        KnownPieceCategoryValuePriorities.Clear();
        KnownPieceCategoryNameSources.Clear();
        KnownPieceCategoryValueSources.Clear();

        for (int value = 0; value < (int)Piece.PieceCategory.Max; value++)
        {
            Piece.PieceCategory category = (Piece.PieceCategory)value;
            RegisterKnownPieceCategory(category.ToString(), category, 1000, "Valheim");
        }

        RegisterJotunnPieceCategories();
        RegisterEmbeddedPieceManagerCategories();
        RegisterPieceTableCategoryLabels();
        RefreshOwnerManagedPieceCategories();
        PieceTableCategoryGuard.ReplaceKnownCategories(
            KnownPieceCategoryValues,
            KnownPieceCategoryNames,
            OwnerManagedPieceCategories);
    }

    private static void RefreshOwnerManagedPieceCategories()
    {
        OwnerManagedPieceCategories.Clear();
        if (IsHomesteadLoaded() &&
            TryResolvePieceCategory(HomesteadCategoryName, out Piece.PieceCategory homesteadCategory))
        {
            OwnerManagedPieceCategories.Add(homesteadCategory);
        }
    }

    private static void ResetPieceCategoryRegistry()
    {
        KnownPieceCategoryNames.Clear();
        KnownPieceCategoryValues.Clear();
        KnownPieceCategoryNamePriorities.Clear();
        KnownPieceCategoryValuePriorities.Clear();
        KnownPieceCategoryNameSources.Clear();
        KnownPieceCategoryValueSources.Clear();
        OwnerManagedPieceCategories.Clear();
        ReportedPieceCategoryConflicts.Clear();
        ReportedPieceCategoryConfigurationIssues.Clear();
    }

    private static void RegisterJotunnPieceCategories()
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .OrderBy(static assembly => assembly.FullName, StringComparer.Ordinal))
        {
            Type? type = assembly.GetType("Jotunn.Managers.PieceManager", throwOnError: false);
            if (type == null)
            {
                continue;
            }

            try
            {
                object? instance = type
                    .GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    ?.GetValue(null);
                string source = assembly.GetName().Name ?? "Jotunn";
                RegisterPieceManagerCategoryDictionary(type, instance, "PieceCategories", 800, source);
                RegisterPieceManagerCategoryDictionary(type, instance, "OtherPieceCategories", 700, source);
            }
            catch (Exception ex)
            {
                DataForgePlugin.Log.LogDebug($"Could not inspect Jotunn piece categories: {ex.Message}");
            }
        }
    }

    private static void RegisterEmbeddedPieceManagerCategories()
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .OrderBy(static assembly => assembly.FullName, StringComparer.Ordinal))
        {
            Type? type = assembly.GetType("PieceManager.PiecePrefabManager", throwOnError: false);
            if (type == null)
            {
                continue;
            }

            try
            {
                string source = assembly.GetName().Name ?? type.FullName ?? "PieceManager";
                RegisterPieceManagerCategoryDictionary(type, null, "PieceCategories", 800, source);
                RegisterPieceManagerCategoryDictionary(type, null, "OtherPieceCategories", 700, source);
            }
            catch (Exception ex)
            {
                DataForgePlugin.Log.LogDebug($"Could not inspect piece categories from '{assembly.GetName().Name}': {ex.Message}");
            }
        }
    }

    private static void RegisterPieceManagerCategoryDictionary(
        Type type,
        object? instance,
        string fieldName,
        int priority,
        string source)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance;
        System.Reflection.FieldInfo? field = type.GetField(fieldName, flags);
        if (field == null || (!field.IsStatic && instance == null))
        {
            return;
        }

        object? dictionaryObject = field.GetValue(field.IsStatic ? null : instance);
        if (dictionaryObject is not System.Collections.IDictionary dictionary)
        {
            return;
        }

        List<(string Name, Piece.PieceCategory Category)> categories = new();
        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string name && entry.Value is Piece.PieceCategory category)
            {
                categories.Add((name, category));
            }
        }

        foreach ((string name, Piece.PieceCategory category) in categories.OrderBy(static pair => pair.Name, StringComparer.Ordinal))
        {
            RegisterKnownPieceCategory(name, category, priority, source);
        }
    }

    private static void RegisterPieceTableCategoryLabels()
    {
        foreach (PieceTable pieceTable in GetAllPieceTables().OrderBy(static table => table.name, StringComparer.OrdinalIgnoreCase))
        {
            List<Piece.PieceCategory>? categories = pieceTable.m_categories;
            List<string>? labels = pieceTable.m_categoryLabels;
            if (categories == null || labels == null)
            {
                continue;
            }

            int count = Math.Min(categories.Count, labels.Count);
            for (int index = 0; index < count; index++)
            {
                if (TryFormatCategoryLabel(labels[index], out string displayName))
                {
                    RegisterKnownPieceCategory(displayName, categories[index], 200, pieceTable.name);
                }
            }
        }
    }

    private static void RegisterKnownPieceCategory(
        string? name,
        Piece.PieceCategory category,
        int priority,
        string source)
    {
        string normalized = name?.Trim() ?? "";
        if (normalized.Length == 0 ||
            IsNumericCategoryName(normalized) ||
            category is Piece.PieceCategory.Max or Piece.PieceCategory.All ||
            (int)category < 0)
        {
            return;
        }

        if (KnownPieceCategoryValues.TryGetValue(normalized, out Piece.PieceCategory existingCategory) &&
            existingCategory != category)
        {
            int existingPriority = KnownPieceCategoryValuePriorities[normalized];
            string existingSource = KnownPieceCategoryValueSources[normalized];
            bool replace = priority > existingPriority ||
                           (priority == existingPriority && (int)category < (int)existingCategory);
            ReportPieceCategoryConflict(normalized, existingCategory, existingSource, category, source, replace ? category : existingCategory);
            if (!replace)
            {
                return;
            }

            if (KnownPieceCategoryNames.TryGetValue(existingCategory, out string staleName) &&
                staleName.Equals(normalized, StringComparison.Ordinal))
            {
                KnownPieceCategoryNames.Remove(existingCategory);
                KnownPieceCategoryNamePriorities.Remove(existingCategory);
                KnownPieceCategoryNameSources.Remove(existingCategory);
            }
        }

        KnownPieceCategoryValues[normalized] = category;
        KnownPieceCategoryValuePriorities[normalized] = priority;
        KnownPieceCategoryValueSources[normalized] = source;

        if (KnownPieceCategoryNames.TryGetValue(category, out string existingName))
        {
            int existingPriority = KnownPieceCategoryNamePriorities[category];
            string existingSource = KnownPieceCategoryNameSources[category];
            if (priority >= 800 &&
                existingPriority >= 800 &&
                !normalized.Equals(existingName, StringComparison.Ordinal))
            {
                ReportPieceCategoryIdConflict(category, existingName, existingSource, normalized, source);
            }

            bool replace = priority > existingPriority ||
                           (priority == existingPriority &&
                            string.Compare(normalized, existingName, StringComparison.Ordinal) < 0);
            if (!replace)
            {
                return;
            }
        }

        KnownPieceCategoryNames[category] = normalized;
        KnownPieceCategoryNamePriorities[category] = priority;
        KnownPieceCategoryNameSources[category] = source;
    }

    private static void ReportPieceCategoryConflict(
        string name,
        Piece.PieceCategory first,
        string firstSource,
        Piece.PieceCategory second,
        string secondSource,
        Piece.PieceCategory selected)
    {
        int low = Math.Min((int)first, (int)second);
        int high = Math.Max((int)first, (int)second);
        string key = $"{name}|{low}|{high}";
        if (!ReportedPieceCategoryConflicts.Add(key))
        {
            return;
        }

        DataForgePlugin.Log.LogWarning(
            $"Piece category '{name}' was registered as both {(int)first} by '{firstSource}' and {(int)second} by '{secondSource}'; using {(int)selected}.");
    }

    private static void ReportPieceCategoryIdConflict(
        Piece.PieceCategory category,
        string firstName,
        string firstSource,
        string secondName,
        string secondSource)
    {
        string lowName;
        string highName;
        if (string.Compare(firstName, secondName, StringComparison.Ordinal) <= 0)
        {
            lowName = firstName;
            highName = secondName;
        }
        else
        {
            lowName = secondName;
            highName = firstName;
        }

        string key = $"id:{(int)category}|{lowName}|{highName}";
        if (!ReportedPieceCategoryConflicts.Add(key))
        {
            return;
        }

        DataForgePlugin.Log.LogWarning(
            $"Piece category id {(int)category} is shared by '{firstName}' from '{firstSource}' and '{secondName}' from '{secondSource}'. " +
            "DataForge will keep the deterministic registry name, but those external categories cannot be separated without remapping their owner mod's pieces.");
    }

    private static bool IsNumericCategoryName(string? categoryName)
    {
        return int.TryParse(categoryName?.Trim() ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static bool TryGetCategoryDisplayName(Piece.PieceCategory category, GameObject? piecePrefab, out string displayName)
    {
        if (piecePrefab != null)
        {
            foreach (PieceTable pieceTable in GetPieceTablesContaining(piecePrefab))
            {
                if (TryGetCategoryDisplayNameFromTable(pieceTable, category, out displayName))
                {
                    return true;
                }
            }
        }

        foreach (PieceTable pieceTable in GetAllPieceTables())
        {
            if (TryGetCategoryDisplayNameFromTable(pieceTable, category, out displayName))
            {
                return true;
            }
        }

        displayName = "";
        return false;
    }

    private static bool TryGetCategoryDisplayNameFromTable(PieceTable pieceTable, Piece.PieceCategory category, out string displayName)
    {
        displayName = "";
        List<Piece.PieceCategory>? categories = pieceTable ? pieceTable.m_categories : null;
        List<string>? labels = pieceTable ? pieceTable.m_categoryLabels : null;
        if (categories == null || labels == null)
        {
            return false;
        }

        int count = Math.Min(categories.Count, labels.Count);
        for (int index = 0; index < count; index++)
        {
            if (categories[index] != category ||
                !TryFormatCategoryLabel(labels[index], out displayName))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool CategoryLabelMatches(string input, string? label)
    {
        string trimmedLabel = label?.Trim() ?? "";
        if (trimmedLabel.Length == 0)
        {
            return false;
        }

        if (input.Equals(trimmedLabel, StringComparison.Ordinal) ||
            input.Equals(trimmedLabel.TrimStart('$'), StringComparison.Ordinal))
        {
            return true;
        }

        return TryFormatCategoryLabel(trimmedLabel, out string displayName) &&
               input.Equals(displayName, StringComparison.Ordinal);
    }

    private static bool TryFormatCategoryLabel(string? label, out string displayName)
    {
        displayName = "";
        string trimmed = label?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (!trimmed.StartsWith("$", StringComparison.Ordinal))
        {
            displayName = trimmed;
            return true;
        }

        string localized = Localization.instance != null ? Localization.instance.Localize(trimmed).Trim() : "";
        if (localized.Length > 0 &&
            !localized.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            displayName = localized;
            return true;
        }

        return TryFormatKnownCategoryToken(trimmed, out displayName);
    }

    private static bool TryFormatKnownCategoryToken(string token, out string displayName)
    {
        displayName = "";
        string normalized = token.TrimStart('$');
        string[] prefixes = { "piecemanager_cat_", "jotunn_cat_" };
        foreach (string prefix in prefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string suffix = normalized.Substring(prefix.Length);
            displayName = ToDisplayName(suffix);
            return displayName.Length > 0;
        }

        return false;
    }

    private static string ToDisplayName(string value)
    {
        string[] parts = value
            .Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();
        return string.Join(" ", parts.Select(Capitalize));
    }

    private static string Capitalize(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return value.Length == 1
            ? value.ToUpperInvariant()
            : char.ToUpperInvariant(value[0]) + value.Substring(1);
    }

    private static GameObject? ResolvePiecePrefab(string prefabName)
    {
        string normalizedName = NormalizePrefabName(prefabName);
        if (ZNetScene.instance != null)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(normalizedName);
            if (prefab != null && IsManagedPiece(prefab))
            {
                return prefab;
            }
        }

        foreach ((string candidateName, Piece piece) in GetPrefabPieces())
        {
            if (candidateName.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                return piece.gameObject;
            }
        }

        return null;
    }

    private static string? GetFullScaffoldPieceTableName(string prefabName, PieceTableMembershipSnapshot pieceTableMembership)
    {
        return pieceTableMembership
            .GetTableNames(prefabName)
            .Where(name => !IsIgnoredPieceTableName(name))
            .FirstOrDefault();
    }

    private static bool ShouldGeneratePieceEntry(string prefabName, PieceTableMembershipSnapshot pieceTableMembership)
    {
        return !pieceTableMembership.IsOnlyInIgnoredTables(prefabName);
    }

    private static PieceTableMembershipSnapshot BuildPieceTableMembershipSnapshot()
    {
        Dictionary<string, List<string>> namesByPiece = new(StringComparer.OrdinalIgnoreCase);
        foreach (PieceTable pieceTable in GetAllPieceTables(includeIgnored: true))
        {
            if (!pieceTable || pieceTable.m_pieces == null)
            {
                continue;
            }

            string tableName = GetFriendlyPieceTableName(pieceTable);
            if (string.IsNullOrWhiteSpace(tableName))
            {
                continue;
            }

            foreach (GameObject piece in pieceTable.m_pieces)
            {
                if (piece == null)
                {
                    continue;
                }

                string prefabName = GetPrefabName(piece);
                if (!namesByPiece.TryGetValue(prefabName, out List<string> tableNames))
                {
                    tableNames = new List<string>();
                    namesByPiece[prefabName] = tableNames;
                }

                if (!tableNames.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                {
                    tableNames.Add(tableName);
                }
            }
        }

        return new PieceTableMembershipSnapshot(namesByPiece);
    }

    private static string GetFriendlyPieceTableName(PieceTable pieceTable)
    {
        string? ownerItemName = GetPieceTableOwnerItemName(pieceTable);
        if (!string.IsNullOrWhiteSpace(ownerItemName))
        {
            return ownerItemName!;
        }

        string tableName = NormalizePrefabName(pieceTable.name);
        foreach (KeyValuePair<string, string> alias in PieceTableAliases)
        {
            if (alias.Value.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                return alias.Key;
            }
        }

        foreach ((string name, PieceTable table) in GetNamedPieceTables())
        {
            if (ReferenceEquals(pieceTable, table) && !string.IsNullOrWhiteSpace(name))
            {
                return NormalizePieceTableAlias(name);
            }
        }

        return NormalizePieceTableAlias(tableName);
    }

    private static string? GetPieceTableOwnerItemName(PieceTable pieceTable)
    {
        foreach ((string itemName, PieceTable buildPieces) in GetBuildPieceOwnerTables())
        {
            if (ReferenceEquals(buildPieces, pieceTable) && itemName.Length > 0)
            {
                return itemName;
            }
        }

        return null;
    }

    private static string NormalizePieceTableAlias(string pieceTableName)
    {
        string normalizedName = NormalizePrefabName(pieceTableName);
        foreach (KeyValuePair<string, string> alias in PieceTableAliases)
        {
            if (alias.Key.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                alias.Value.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                return alias.Key;
            }
        }

        return normalizedName;
    }

    private static PieceTable? ResolvePieceTable(string pieceTableName)
    {
        if (string.IsNullOrWhiteSpace(pieceTableName))
        {
            return null;
        }

        if (IsIgnoredPieceTableName(pieceTableName))
        {
            return null;
        }

        string normalizedName = NormalizePieceTableIdentifier(pieceTableName);
        foreach ((string name, PieceTable table) in GetNamedPieceTables())
        {
            if (name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(pieceTableName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        return null;
    }

    private static string NormalizePieceTableIdentifier(string pieceTableName)
    {
        string trimmed = NormalizePrefabName(pieceTableName);
        return PieceTableAliases.TryGetValue(trimmed, out string internalName)
            ? internalName
            : trimmed;
    }

    private static IEnumerable<(string Name, PieceTable Table)> GetNamedPieceTables()
    {
        HashSet<string> seenAliases = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string itemName, PieceTable pieceTable) in GetBuildPieceOwnerTables())
        {
            foreach ((string name, PieceTable table) in GetPieceTableNamesForItem(itemName, pieceTable))
            {
                string key = $"{RuntimeHelpers.GetHashCode(table)}:{name}";
                if (seenAliases.Add(key))
                {
                    yield return (name, table);
                }
            }
        }

        foreach (PieceTable pieceTable in Resources.FindObjectsOfTypeAll<PieceTable>())
        {
            if (pieceTable == null)
            {
                continue;
            }

            string name = NormalizePrefabName(pieceTable.name);
            if (name.Length == 0)
            {
                continue;
            }

            string key = $"{RuntimeHelpers.GetHashCode(pieceTable)}:{name}";
            if (seenAliases.Add(key))
            {
                yield return (name, pieceTable);
            }
        }
    }

    private static IEnumerable<(string Name, PieceTable Table)> GetBuildPieceOwnerTables()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject itemPrefab in GetKnownItemPrefabs())
        {
            PieceTable? pieceTable = itemPrefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
            string itemName = NormalizePrefabName(itemPrefab.name);
            if (pieceTable == null || itemName.Length == 0)
            {
                continue;
            }

            string key = $"{RuntimeHelpers.GetHashCode(pieceTable)}:{itemName}";
            if (seen.Add(key))
            {
                yield return (itemName, pieceTable);
            }
        }

        if (ItemDrop.s_instances == null)
        {
            yield break;
        }

        foreach (ItemDrop itemDrop in ItemDrop.s_instances)
        {
            if (itemDrop == null)
            {
                continue;
            }

            PieceTable? pieceTable = itemDrop.m_itemData?.m_shared?.m_buildPieces;
            if (pieceTable == null)
            {
                continue;
            }

            string itemName = itemDrop.m_itemData?.m_dropPrefab != null
                ? NormalizePrefabName(itemDrop.m_itemData.m_dropPrefab.name)
                : GetPrefabName(itemDrop.gameObject);
            if (itemName.Length == 0)
            {
                continue;
            }

            string key = $"{RuntimeHelpers.GetHashCode(pieceTable)}:{itemName}";
            if (seen.Add(key))
            {
                yield return (itemName, pieceTable);
            }
        }
    }

    private static IEnumerable<GameObject> GetKnownItemPrefabs()
    {
        HashSet<GameObject> seen = new(ReferenceComparer<GameObject>.Instance);
        if (ObjectDB.instance != null)
        {
            foreach (GameObject itemPrefab in ObjectDB.instance.m_items)
            {
                if (itemPrefab != null && itemPrefab.GetComponent<ItemDrop>() != null && seen.Add(itemPrefab))
                {
                    yield return itemPrefab;
                }
            }
        }

        if (ZNetScene.instance == null)
        {
            yield break;
        }

        foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
        {
            if (prefab != null && prefab.GetComponent<ItemDrop>() != null && seen.Add(prefab))
            {
                yield return prefab;
            }
        }
    }

    private static IEnumerable<(string Name, PieceTable Table)> GetPieceTableNamesForItem(string itemName, PieceTable pieceTable)
    {
        if (itemName.Length > 0)
        {
            yield return (itemName, pieceTable);
        }

        string tableName = NormalizePrefabName(pieceTable.name);
        if (tableName.Length > 0)
        {
            yield return (tableName, pieceTable);
        }

        foreach (KeyValuePair<string, string> alias in PieceTableAliases)
        {
            if (alias.Key.Equals(itemName, StringComparison.OrdinalIgnoreCase) ||
                alias.Value.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                yield return (alias.Key, pieceTable);
                yield return (alias.Value, pieceTable);
            }
        }
    }

    private static void RefreshLocalBuildPieces()
    {
        if (Player.m_localPlayer == null || Player.m_localPlayer.m_buildPieces == null)
        {
            return;
        }

        PieceTableCategoryGuard.Normalize(Player.m_localPlayer.m_buildPieces);
        Player.m_localPlayer.UpdateAvailablePiecesList();
    }

    internal static void ReconcileHammerCategoriesAfterHudUpdate()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown ||
            !DataForgeWorldLifecycle.IsGameStarted ||
            !GameDataReady ||
            !ObjectDbReady ||
            !PieceTablesReady ||
            ZNetScene.instance == null ||
            ObjectDB.instance == null)
        {
            return;
        }

        Player? player = Player.m_localPlayer;
        PieceTable? hammerPieceTable = GetHammerPieceTable();
        if (player == null ||
            !hammerPieceTable ||
            !ReferenceEquals(player.m_buildPieces, hammerPieceTable))
        {
            HammerCategoryReconciliationFailures = 0;
            return;
        }

        if (ExpectedHammerCategories.Count == 0 &&
            !PieceTableCategoryGuard.HasConfiguredOrder(hammerPieceTable))
        {
            return;
        }

        if (HammerCategoryReconciliationFailures >= 2)
        {
            return;
        }

        try
        {
            if (HammerAvailabilityRefreshAttemptsRemaining == 0 &&
                !HammerCategoryIdentityResolutionPending &&
                !HasConfiguredHammerCategoryDrift(hammerPieceTable) &&
                !PieceTableCategoryGuard.NeedsNormalization(hammerPieceTable))
            {
                HammerCategoryReconciliationFailures = 0;
                return;
            }

            ReconcileConfiguredHammerCategories(hammerPieceTable, player);
            HammerCategoryReconciliationFailures = 0;
        }
        catch (Exception ex)
        {
            HammerCategoryReconciliationFailures++;
            DataForgePlugin.Log.LogWarning($"Failed to reconcile configured Hammer categories: {ex}");
        }
    }

    private static void ReconcileConfiguredHammerCategories(PieceTable hammerPieceTable, Player player)
    {
        bool categoryChanged = ResolveCanonicalHammerCategoryClaims(hammerPieceTable);
        HammerCategoryIdentityResolutionPending = false;
        List<Piece>? discardedPieces = null;
        foreach (KeyValuePair<Piece, HammerCategoryClaim> expected in ExpectedHammerCategories)
        {
            Piece? piece = expected.Key;
            if (!piece)
            {
                (discardedPieces ??= new List<Piece>()).Add(expected.Key);
                continue;
            }

            Piece.PieceCategory targetCategory = expected.Value.TargetCategory;
            bool pieceCategoryChanged = piece.m_category != targetCategory;
            bool categoryMissing = hammerPieceTable.m_categories?.Contains(targetCategory) != true;
            if (!pieceCategoryChanged && !categoryMissing)
            {
                continue;
            }

            if (hammerPieceTable.m_pieces?.Contains(piece.gameObject) != true)
            {
                continue;
            }

            try
            {
                if (pieceCategoryChanged)
                {
                    ApplyCategory(piece, targetCategory);
                }
                else
                {
                    PieceTableCategoryGuard.EnsureCategory(hammerPieceTable, targetCategory);
                }

                if (categoryMissing &&
                    hammerPieceTable.m_categories?.Contains(targetCategory) != true)
                {
                    string prefabName = GetPrefabName(piece.gameObject);
                    ReportPieceCategoryConfigurationIssue(
                        $"hammer-final-category:{prefabName}",
                        $"Could not restore the configured Hammer category for piece '{prefabName}'.");
                    (discardedPieces ??= new List<Piece>()).Add(expected.Key);
                    continue;
                }

                categoryChanged = true;
            }
            catch (Exception ex)
            {
                string prefabName = GetPrefabName(piece.gameObject);
                ReportPieceCategoryConfigurationIssue(
                    $"hammer-final-piece:{prefabName}",
                    $"Could not reconcile Hammer category for piece '{prefabName}': {ex.Message}");
                if (piece.m_category != targetCategory ||
                    hammerPieceTable.m_categories?.Contains(targetCategory) != true)
                {
                    (discardedPieces ??= new List<Piece>()).Add(expected.Key);
                }
            }
        }

        if (discardedPieces != null)
        {
            foreach (Piece piece in discardedPieces)
            {
                ExpectedHammerCategories.Remove(piece);
            }
        }

        if (categoryChanged)
        {
            PruneUnusedInsertedPieceTableCategories();
        }

        PieceTableCategoryGuard.Normalize(hammerPieceTable);
        if (categoryChanged)
        {
            HammerAvailabilityRefreshAttemptsRemaining = HammerAvailabilityRefreshAttemptLimit;
        }

        if (HammerAvailabilityRefreshAttemptsRemaining > 0)
        {
            try
            {
                bool allPiecesUnlocked = player.m_noPlacementCost ||
                                         (ZoneSystem.instance != null &&
                                          ZoneSystem.instance.GetGlobalKey(GlobalKeys.AllPiecesUnlocked));
                hammerPieceTable.UpdateAvailable(player.m_knownRecipes, player, false, allPiecesUnlocked);
                HammerAvailabilityRefreshAttemptsRemaining = 0;
            }
            catch (Exception ex)
            {
                HammerAvailabilityRefreshAttemptsRemaining--;
                ReportPieceCategoryConfigurationIssue(
                    "hammer-final-availability",
                    $"Could not refresh Hammer availability after category reconciliation: {ex.Message}");
            }
        }
    }

    private static bool ResolveCanonicalHammerCategoryClaims(PieceTable hammerPieceTable)
    {
        if (ExpectedHammerCategories.Count == 0)
        {
            return false;
        }

        HashSet<Piece> managedPieces = new(
            ExpectedHammerCategories.Keys,
            ReferenceComparer<Piece>.Instance);
        HashSet<Piece.PieceCategory> categoriesUsedByUnmanagedPieces = new();
        if (hammerPieceTable.m_pieces != null)
        {
            foreach (GameObject piecePrefab in hammerPieceTable.m_pieces)
            {
                Piece? piece = piecePrefab ? piecePrefab.GetComponent<Piece>() : null;
                if (piece != null && !managedPieces.Contains(piece))
                {
                    categoriesUsedByUnmanagedPieces.Add(piece.m_category);
                }
            }
        }

        HashSet<Piece.PieceCategory> categories = new();
        if (hammerPieceTable.m_categories != null)
        {
            foreach (Piece.PieceCategory category in hammerPieceTable.m_categories)
            {
                if (IsHammerCategoryCoalescingCandidate(category))
                {
                    categories.Add(category);
                }
            }
        }

        foreach (HammerCategoryClaim claim in ExpectedHammerCategories.Values)
        {
            if (IsHammerCategoryCoalescingCandidate(claim.TargetCategory))
            {
                categories.Add(claim.TargetCategory);
            }
        }

        bool changed = false;
        foreach (IGrouping<string, HammerCategoryClaim> group in ExpectedHammerCategories.Values
                     .GroupBy(
                         claim => GetHammerCategoryDisplayName(
                             hammerPieceTable,
                             claim.TargetCategory,
                             claim.ConfiguredName),
                         StringComparer.Ordinal))
        {
            if (group.Key.Length == 0)
            {
                continue;
            }

            List<Piece.PieceCategory> matches = categories
                .Where(category => group.Key.Equals(
                    GetHammerCategoryDisplayName(hammerPieceTable, category, ""),
                    StringComparison.Ordinal))
                .OrderBy(static category => (int)category)
                .ToList();
            if (matches.Count < 2)
            {
                continue;
            }

            List<Piece.PieceCategory> ownerCandidates = matches
                .Where(categoriesUsedByUnmanagedPieces.Contains)
                .ToList();
            if (ownerCandidates.Count == 0)
            {
                continue;
            }

            if (ownerCandidates.Count > 1)
            {
                ReportPieceCategoryConfigurationIssue(
                    $"hammer-display-category-ambiguous:{group.Key}",
                    $"Hammer has multiple non-DataForge category ids with the displayed name '{group.Key}'; " +
                    "configured pieces were not merged automatically.");
                continue;
            }

            Piece.PieceCategory canonicalCategory = ownerCandidates[0];
            foreach (HammerCategoryClaim claim in group)
            {
                if (claim.TargetCategory == canonicalCategory)
                {
                    continue;
                }

                claim.TargetCategory = canonicalCategory;
                changed = true;
            }
        }

        return changed;
    }

    private static bool IsHammerCategoryCoalescingCandidate(Piece.PieceCategory category)
    {
        return category is not Piece.PieceCategory.Max and not Piece.PieceCategory.All &&
               (int)category >= 0 &&
               !IsOwnerManagedHomesteadCategory(category);
    }

    private static string GetHammerCategoryDisplayName(
        PieceTable hammerPieceTable,
        Piece.PieceCategory category,
        string fallback)
    {
        if (TryGetCategoryDisplayNameFromTable(hammerPieceTable, category, out string displayName))
        {
            return displayName.Trim();
        }

        if (TryFormatCategoryLabel(fallback, out displayName))
        {
            return displayName.Trim();
        }

        return "";
    }

    private static bool HasConfiguredHammerCategoryDrift(PieceTable hammerPieceTable)
    {
        foreach (KeyValuePair<Piece, HammerCategoryClaim> expected in ExpectedHammerCategories)
        {
            Piece? piece = expected.Key;
            if (!piece)
            {
                return true;
            }

            Piece.PieceCategory targetCategory = expected.Value.TargetCategory;
            bool pieceCategoryChanged = piece.m_category != targetCategory;
            bool categoryMissing = hammerPieceTable.m_categories?.Contains(targetCategory) != true;
            if ((pieceCategoryChanged || categoryMissing) &&
                hammerPieceTable.m_pieces?.Contains(piece.gameObject) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static void CaptureExpectedHammerCategories(
        IEnumerable<PieceEntry> entries,
        bool pieceOverridesEnabled,
        ISet<string> removedPieces)
    {
        Dictionary<string, string> claims = new(StringComparer.OrdinalIgnoreCase);
        if (pieceOverridesEnabled)
        {
            foreach (PieceEntry entry in entries)
            {
                string prefabName = entry.Piece?.Trim() ?? "";
                string categoryName = entry.Category?.Trim() ?? "";
                if (!entry.Override ||
                    entry.Remove ||
                    prefabName.Length == 0 ||
                    categoryName.Length == 0 ||
                    IsIgnoredCategoryName(categoryName) ||
                    IsOwnerManagedHomesteadCategoryName(categoryName) ||
                    removedPieces.Contains(prefabName))
                {
                    continue;
                }

                claims[prefabName] = categoryName;
            }
        }

        HammerCategoryReconciliationFailures = 0;
        HammerAvailabilityRefreshAttemptsRemaining = 0;
        PieceTable? hammerPieceTable = GetHammerPieceTable();
        if (!hammerPieceTable)
        {
            ExpectedHammerCategories.Clear();
            HammerCategoryIdentityResolutionPending = false;
            return;
        }

        Dictionary<Piece, HammerCategoryClaim> expected = new(ReferenceComparer<Piece>.Instance);
        foreach (KeyValuePair<string, string> claim in claims)
        {
            if (TryResolvePieceCategory(claim.Value, out Piece.PieceCategory category) &&
                Baselines.TryGetValue(claim.Key, out PieceBaseline? baseline) &&
                baseline.Piece &&
                hammerPieceTable.m_pieces?.Contains(baseline.Piece.gameObject) == true &&
                !IsOwnerManagedHomesteadCategory(category))
            {
                expected[baseline.Piece] = new HammerCategoryClaim(claim.Value, category);
            }
        }

        ExpectedHammerCategories.Clear();
        foreach (KeyValuePair<Piece, HammerCategoryClaim> pair in expected)
        {
            ExpectedHammerCategories[pair.Key] = pair.Value;
        }

        HammerCategoryIdentityResolutionPending = ExpectedHammerCategories.Count > 0;
    }

    internal static void MarkHammerCategoryIdentityResolutionPending(PieceTable? pieceTable)
    {
        if (ExpectedHammerCategories.Count == 0 ||
            !pieceTable ||
            !ReferenceEquals(pieceTable, GetHammerPieceTable()))
        {
            return;
        }

        HammerCategoryIdentityResolutionPending = true;
    }

    private static PieceTable? GetHammerPieceTable()
    {
        GameObject? hammer = ObjectDB.instance?.GetItemPrefab(HammerPrefabName);
        return hammer ? hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
    }

    private static void ResetHammerCategoryReconciliation()
    {
        ExpectedHammerCategories.Clear();
        HammerCategoryReconciliationFailures = 0;
        HammerAvailabilityRefreshAttemptsRemaining = 0;
        HammerCategoryIdentityResolutionPending = false;
    }

    private static void ReapplyRecipesIfCraftingStationTopologyChanged()
    {
        if (!CraftingStationTopologyChanged)
        {
            return;
        }

        CraftingStationTopologyChanged = false;
        try
        {
            RecipeOverrideManager.ApplyCurrentConfiguration();
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning(
                $"Could not reapply recipes after a piece crafting-station topology change: {ex.Message}");
        }
    }

    private static void ApplyHealth(WearNTear wearNTear, float maxHealth, bool adjustHealthZdo)
    {
        float previousMax = wearNTear.m_health;
        wearNTear.m_health = maxHealth;

        if (!adjustHealthZdo || previousMax <= 0f)
        {
            return;
        }

        ZNetView zNetView = wearNTear.GetComponent<ZNetView>();
        if (zNetView == null || zNetView.GetZDO() == null || !zNetView.IsOwner())
        {
            return;
        }

        ZDO zdo = zNetView.GetZDO();
        float currentHealth = zdo.GetFloat(ZDOVars.s_health, previousMax);
        float ratio = Mathf.Clamp01(currentHealth / previousMax);
        zdo.Set(ZDOVars.s_health, Mathf.Clamp(maxHealth * ratio, 0f, maxHealth));
    }

    private static void WritePieceCategoryReferenceArtifact()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles ||
            DataForgeWorldLifecycle.IsShuttingDown ||
            !CanBuildPieceCategoryReferenceArtifact())
        {
            return;
        }

        _ = WritePieceCategoryReferenceArtifactCore();
    }

    internal static bool TryRegeneratePieceCategoryReferenceFile(
        out string path,
        out bool changed,
        out string error)
    {
        path = Path.Combine(ConfigDirectory, PieceCategoryReferenceFileName);
        changed = false;
        if (!GeneratedArtifactWriter.CanWriteGeneratedArtifact(
                CanBuildPieceCategoryReferenceArtifact(),
                "Piece category game data is not ready yet.",
                out error))
        {
            return false;
        }

        try
        {
            changed = WritePieceCategoryReferenceArtifactCore();
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not regenerate the piece category reference file: {ex.Message}";
            return false;
        }
    }

    private static bool CanBuildPieceCategoryReferenceArtifact()
    {
        return PieceTablesReady &&
               ZNetScene.instance != null &&
               ObjectDB.instance != null;
    }

    private static bool WritePieceCategoryReferenceArtifactCore()
    {
        Dictionary<string, List<string>> tables = new(StringComparer.OrdinalIgnoreCase);
        foreach (PieceTable pieceTable in GetAllPieceTables()
                     .OrderBy(GetFriendlyPieceTableName, PieceTableNameComparer.Instance))
        {
            if (!pieceTable || pieceTable.m_categories == null)
            {
                continue;
            }

            string pieceTableName = GetFriendlyPieceTableName(pieceTable);
            if (pieceTableName.Length == 0 || tables.ContainsKey(pieceTableName))
            {
                continue;
            }

            PieceTableCategoryGuard.Normalize(pieceTable);
            Dictionary<Piece.PieceCategory, string> labelsByCategory = new();
            int pairedCount = Math.Min(
                pieceTable.m_categories.Count,
                pieceTable.m_categoryLabels?.Count ?? 0);
            for (int index = 0; index < pairedCount; index++)
            {
                Piece.PieceCategory category = pieceTable.m_categories[index];
                if (!labelsByCategory.ContainsKey(category))
                {
                    labelsByCategory[category] = pieceTable.m_categoryLabels![index]?.Trim() ?? "";
                }
            }

            HashSet<Piece.PieceCategory> usedCategories = new();
            if (pieceTable.m_pieces != null)
            {
                foreach (GameObject piecePrefab in pieceTable.m_pieces)
                {
                    Piece? piece = piecePrefab ? piecePrefab.GetComponent<Piece>() : null;
                    Piece.PieceCategory category = piece != null ? piece.m_category : Piece.PieceCategory.Max;
                    if (category is not Piece.PieceCategory.Max and not Piece.PieceCategory.All &&
                        (int)category >= 0)
                    {
                        usedCategories.Add(category);
                    }
                }
            }

            List<Piece.PieceCategory> categoryOrder = pieceTable.m_categories
                .Where(usedCategories.Contains)
                .Distinct()
                .ToList();
            categoryOrder.AddRange(usedCategories
                .Where(category => !categoryOrder.Contains(category))
                .OrderBy(static category => (int)category));

            List<string> categories = new();
            foreach (Piece.PieceCategory category in categoryOrder)
            {
                if (IsOwnerManagedHomesteadCategory(category))
                {
                    continue;
                }

                string rawLabel = labelsByCategory.TryGetValue(category, out string label)
                    ? label
                    : PieceTableCategoryGuard.GetLabel(category);
                string categoryName = GetPieceCategoryReferenceName(category, rawLabel);
                if (categoryName.Length == 0)
                {
                    continue;
                }

                categories.Add(ShouldWritePieceCategoryLabel(categoryName, rawLabel)
                    ? $"{categoryName}, {rawLabel}"
                    : categoryName);
            }

            if (categories.Count > 0)
            {
                tables[pieceTableName] = categories;
            }
        }

        string header = string.Join(Environment.NewLine, new[]
        {
            "# Generated by DataForge: detected piece category order and labels.",
            "# Do not edit this generated file directly. Copy the sections you want to control into pieceCategory.yml.",
            "# Category names are exact and case-sensitive. An optional second value is the displayed label or a $localization token.",
            "# In pieceCategory.yml, '- Furniture: GB_Parchment_Tool' moves that entire source category into the destination section.",
            "# Add a label before the colon to move and translate together: '- Furniture, $hud_furniture: GB_Parchment_Tool'.",
            "# Exact destination category names merge, and individual pieces.yml pieceTable assignments have final priority.",
            "# A plain category entry and one or more mappings may share the same destination category name.",
            "# A source tool/category pair can move to only one destination tool.",
            "# After moving all categories out of a source tool, use 'GB_Parchment_Tool: []' for its empty section.",
            "# Categories omitted from pieceCategory.yml remain after configured categories in their existing relative order.",
            "# Listing a category does not create or preserve an empty build tab.",
            "# When Homestead is installed, its owner-managed category is omitted and always remains last."
        }) + Environment.NewLine;
        return GeneratedArtifactWriter.WriteTextIfChanged(
            Path.Combine(ConfigDirectory, PieceCategoryReferenceFileName),
            header + SparseSerializer.Serialize(tables));
    }

    private static string GetPieceCategoryReferenceName(Piece.PieceCategory category, string rawLabel)
    {
        string categoryName = FormatPieceCategory(category, null, category.ToString()).Trim();
        if (!IsNumericCategoryName(categoryName))
        {
            return categoryName;
        }

        if (TryFormatCategoryLabel(rawLabel, out string displayName) && !IsNumericCategoryName(displayName))
        {
            return displayName;
        }

        string rawName = rawLabel.Trim().TrimStart('$');
        return !IsNumericCategoryName(rawName) ? rawName : categoryName;
    }

    private static bool ShouldWritePieceCategoryLabel(string categoryName, string rawLabel)
    {
        if (rawLabel.Length == 0 || IsNumericCategoryName(rawLabel))
        {
            return false;
        }

        return !CategoryLabelMatches(categoryName, rawLabel);
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
                PieceTableMembershipSnapshot pieceTableMembership = BuildPieceTableMembershipSnapshot();
                HashSet<string> currentPieces = GetCurrentPrefabPieceNameSet();
                var fullEntries = Baselines
                    .Where(pair => currentPieces.Contains(pair.Key) &&
                                   ShouldGeneratePieceEntry(pair.Key, pieceTableMembership))
                    .Select(pair => new
                    {
                        Entry = PieceEntry.FromDefinition(pair.Key, pair.Value.Definition, GetFullScaffoldPieceTableName(pair.Key, pieceTableMembership)),
                        OwnerKey = pair.Key,
                        SortKey = DataForgeResourceMap.BuildSortKey(
                            GetPieceGroupSortRank(pair.Value.Definition),
                            DataForgeResourceMap.GetResourceTierSortValue(
                                pair.Value.Definition.Piece?.Resources?.SelectMany(resource => resource.Keys) ?? Array.Empty<string>()),
                            pair.Key)
                    })
                    .OrderBy(pair => pair.SortKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return GeneratedArtifactWriter.GeneratedHeader(DomainName, $"{DomainName}.yml", "full scaffold") +
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
            $"{DomainName}.yml",
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
        if (Baselines.Count == 0)
        {
            return null;
        }

        return BuildReferenceArtifactContent();
    }

    private static string BuildReferenceArtifactContent()
    {
        PieceTableMembershipSnapshot pieceTableMembership = BuildPieceTableMembershipSnapshot();
        HashSet<string> currentPieces = GetCurrentPrefabPieceNameSet();
        var referenceEntries = Baselines
            .Where(pair => currentPieces.Contains(pair.Key) &&
                           ShouldGeneratePieceEntry(pair.Key, pieceTableMembership))
            .Select(pair => new
            {
                Entry = PieceReferenceEntry.From(pair.Key, pair.Value.Definition),
                SortKey = DataForgeResourceMap.BuildSortKey(
                    GetPieceGroupSortRank(pair.Value.Definition),
                    DataForgeResourceMap.GetResourceTierSortValue(
                        pair.Value.Definition.Piece?.Resources?.SelectMany(resource => resource.Keys) ?? Array.Empty<string>()),
                    pair.Key)
            })
            .ToList();

        return DataForgeReferenceSections.SerializeReferenceSections(
            referenceEntries,
            entry => entry.SortKey,
            entry => DataForgeOwnerResolver.GetPrefabOwnerName(entry.Entry.Piece),
            entry => entry.Entry,
            SparseSerializer);
    }

    private static bool CanBuildGeneratedArtifacts()
    {
        return ObjectDbReady &&
               PieceTablesReady &&
               ZNetScene.instance != null &&
               ObjectDB.instance != null;
    }

    private static int GetPieceGroupSortRank(PieceDefinition definition)
    {
        if (definition.CraftingStation != null)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(definition.StationExtension))
        {
            return 1;
        }

        if (definition.Smelter != null ||
            definition.CookingStation != null ||
            definition.Fermenter != null ||
            !string.IsNullOrWhiteSpace(definition.Beehive) ||
            !string.IsNullOrWhiteSpace(definition.SapCollector))
        {
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(definition.Container))
        {
            return 3;
        }

        if (HasComfort(definition.Piece?.Comfort))
        {
            return 4;
        }

        if (definition.Piece?.Health != null ||
            definition.Piece?.Resources != null)
        {
            return 5;
        }

        return 6;
    }

    private static bool HasComfort(string? comfort)
    {
        string[] parts = SplitTuple(comfort);
        return parts.Length > 0 &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) &&
               amount > 0;
    }

    private static string GetPrefabName(GameObject gameObject)
    {
        return NormalizePrefabName(gameObject.name);
    }

    private static string NormalizePrefabName(string prefabName)
    {
        return prefabName.Replace("(Clone)", "").Trim();
    }

    internal sealed class PieceEntry
    {
        internal string LogContext { get; private set; } = "";
        public string Piece { get; set; } = "";
        public bool Override { get; set; } = true;
        public bool Remove { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? PieceTable { get; set; }
        public string? Category { get; set; }
        public int? SortOrder { get; set; }
        public string? NeedStation { get; set; }
        public bool? CanBeRemoved { get; set; }
        public float? Health { get; set; }
        public string? Comfort { get; set; }
        public List<PieceResourceDefinition>? Resources { get; set; }
        public string? SapCollector { get; set; }
        public string? Beehive { get; set; }
        public FermenterDefinition? Fermenter { get; set; }
        public CookingStationDefinition? CookingStation { get; set; }
        public SmelterDefinition? Smelter { get; set; }
        public string? Container { get; set; }
        public string? StationExtension { get; set; }
        public CraftingStationComponentDefinition? CraftingStation { get; set; }
        public PieceVisualDefinition? Visual { get; set; }

        internal void SetLogContext(string value)
        {
            LogContext = value;
        }

        internal bool HasDefinition =>
            Remove ||
            PieceTable != null ||
            SortOrder != null ||
            HasRuntimeDefinition;

        internal bool HasRuntimeDefinition =>
            HasBaseDefinition ||
            SapCollector != null ||
            Beehive != null ||
            Fermenter != null ||
            CookingStation != null ||
            Smelter != null ||
            Container != null ||
            StationExtension != null ||
            CraftingStation != null ||
            Visual != null;

        private bool HasBaseDefinition =>
            Name != null ||
            Description != null ||
            Category != null ||
            NeedStation != null ||
            CanBeRemoved != null ||
            Health != null ||
            Comfort != null ||
            Resources != null;

        internal PieceComponentDefinition? ToPieceComponentDefinition()
        {
            if (!HasBaseDefinition)
            {
                return null;
            }

            return new PieceComponentDefinition
            {
                Name = Name,
                Description = Description,
                Category = Category,
                NeedStation = NeedStation,
                CanBeRemoved = CanBeRemoved,
                Health = Health,
                Comfort = Comfort,
                Resources = Resources
            };
        }

        internal static PieceEntry FromDefinition(string prefab, PieceDefinition definition, string? pieceTable = null)
        {
            PieceComponentDefinition? piece = definition.Piece;
            return new PieceEntry
            {
                Piece = prefab,
                Override = true,
                Remove = false,
                Name = piece?.Name,
                Description = piece?.Description,
                PieceTable = pieceTable,
                Category = FormatReferenceCategory(prefab, piece?.Category),
                SortOrder = DefaultPieceSortOrder,
                NeedStation = piece?.NeedStation,
                CanBeRemoved = piece?.CanBeRemoved,
                Health = piece?.Health,
                Comfort = piece?.Comfort,
                Resources = piece?.Resources,
                SapCollector = definition.SapCollector,
                Beehive = definition.Beehive,
                Fermenter = definition.Fermenter,
                CookingStation = definition.CookingStation,
                Smelter = definition.Smelter,
                Container = definition.Container,
                StationExtension = definition.StationExtension,
                CraftingStation = definition.CraftingStation,
                Visual = definition.Visual
            };
        }
    }

    internal sealed class PieceReferenceEntry
    {
        public string Piece { get; set; } = "";
        public bool? CanBeRemoved { get; set; }
        public float? Health { get; set; }
        public string? Comfort { get; set; }
        public List<PieceResourceDefinition>? Resources { get; set; }
        public string? SapCollector { get; set; }
        public string? Beehive { get; set; }
        public FermenterDefinition? Fermenter { get; set; }
        public CookingStationDefinition? CookingStation { get; set; }
        public SmelterDefinition? Smelter { get; set; }
        public string? Container { get; set; }
        public string? StationExtension { get; set; }
        public CraftingStationReferenceDefinition? CraftingStation { get; set; }
        public PieceVisualDefinition? Visual { get; set; }

        internal static PieceReferenceEntry From(string prefab, PieceDefinition definition)
        {
            PieceReferenceEntry entry = new()
            {
                Piece = prefab,
                CanBeRemoved = definition.Piece?.CanBeRemoved,
                Health = definition.Piece?.Health,
                Comfort = FormatReferenceComfort(definition.Piece?.Comfort),
                Resources = PieceResourceDefinition.ToReference(definition.Piece?.Resources),
                SapCollector = definition.SapCollector,
                Beehive = definition.Beehive,
                Fermenter = FermenterDefinition.ToReference(definition.Fermenter),
                CookingStation = definition.CookingStation != null ? ReferenceValue.ClonePruned(definition.CookingStation) : null,
                Smelter = definition.Smelter != null ? ReferenceValue.ClonePruned(definition.Smelter) : null,
                Container = definition.Container,
                StationExtension = definition.StationExtension,
                CraftingStation = definition.CraftingStation != null
                    ? ReferenceValue.ClonePruned(new CraftingStationReferenceDefinition
                    {
                        DiscoveryRange = definition.CraftingStation.DiscoveryRange,
                        BuildRange = definition.CraftingStation.BuildRange,
                        CraftRequiresRoof = definition.CraftingStation.CraftRequiresRoof,
                        CraftRequiresFire = definition.CraftingStation.CraftRequiresFire
                    })
                    : null,
                Visual = ToReferenceVisual(definition.Visual)
            };
            return ReferenceValue.ClonePruned(entry) ?? new PieceReferenceEntry { Piece = prefab };
        }

        private static PieceVisualDefinition? ToReferenceVisual(PieceVisualDefinition? visual)
        {
            if (visual == null)
            {
                return null;
            }

            PieceVisualDefinition reference = new()
            {
                Scale = visual.Scale.HasValue && Math.Abs(visual.Scale.Value - 1f) <= 0.0001f
                    ? null
                    : visual.Scale,
                Material = visual.Material,
                Icon = visual.Icon,
                IconRotation = visual.IconRotation
            };
            return ReferenceValue.ClonePruned(reference);
        }

        private static string? FormatReferenceComfort(string? value)
        {
            string[] parts = SplitTuple(value);
            if (parts.Length != 2 ||
                !parts[1].Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            return parts[0];
        }
    }

    internal sealed class PieceDefinition
    {
        public PieceComponentDefinition? Piece { get; set; }
        public string? SapCollector { get; set; }
        public string? Beehive { get; set; }
        public FermenterDefinition? Fermenter { get; set; }
        public CookingStationDefinition? CookingStation { get; set; }
        public SmelterDefinition? Smelter { get; set; }
        public string? Container { get; set; }
        public string? StationExtension { get; set; }
        public CraftingStationComponentDefinition? CraftingStation { get; set; }
        public PieceVisualDefinition? Visual { get; set; }

        internal static PieceDefinition From(PieceEntry entry)
        {
            return new PieceDefinition
            {
                Piece = entry.ToPieceComponentDefinition(),
                SapCollector = entry.SapCollector,
                Beehive = entry.Beehive,
                Fermenter = entry.Fermenter,
                CookingStation = entry.CookingStation,
                Smelter = entry.Smelter,
                Container = entry.Container,
                StationExtension = entry.StationExtension,
                CraftingStation = entry.CraftingStation,
                Visual = entry.Visual
            };
        }

        internal static PieceDefinition From(Piece piece)
        {
            return new PieceDefinition
            {
                Piece = PieceComponentDefinition.From(piece),
                SapCollector = FormatSapCollector(piece.GetComponent<SapCollector>()),
                Beehive = FormatBeehive(piece.GetComponent<Beehive>()),
                Fermenter = FermenterDefinition.From(piece.GetComponent<Fermenter>()),
                CookingStation = CookingStationDefinition.From(piece.GetComponent<CookingStation>()),
                Smelter = SmelterDefinition.From(piece.GetComponent<Smelter>()),
                Container = FormatContainer(piece.GetComponent<Container>()),
                StationExtension = FormatStationExtension(piece.GetComponents<StationExtension>()),
                CraftingStation = CraftingStationComponentDefinition.From(piece.GetComponent<CraftingStation>()),
                Visual = PieceVisualDefinition.From(piece)
            };
        }
    }

    internal sealed class PieceComponentDefinition
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? NeedStation { get; set; }
        public bool? CanBeRemoved { get; set; }
        public float? Health { get; set; }
        public string? Comfort { get; set; }
        public List<PieceResourceDefinition>? Resources { get; set; }
        [YamlIgnore]
        public string? BaselineDoorName { get; set; }
        [YamlIgnore]
        public Piece.PieceCategory? BaselineCategory { get; set; }

        internal static PieceComponentDefinition From(Piece piece)
        {
            WearNTear wearNTear = piece.GetComponent<WearNTear>();
            Door? door = piece.GetComponent<Door>();
            return new PieceComponentDefinition
            {
                Name = piece.m_name,
                Description = piece.m_description,
                Category = FormatPieceCategory(piece),
                BaselineCategory = piece.m_category,
                NeedStation = FormatCraftingStation(piece.m_craftingStation),
                CanBeRemoved = piece.m_canBeRemoved,
                Health = wearNTear != null ? wearNTear.m_health : null,
                Comfort = FormatTuple(piece.m_comfort, piece.m_comfortGroup),
                Resources = PieceResourceDefinition.From(piece.m_resources),
                BaselineDoorName = door != null ? door.m_name : null
            };
        }
    }

    internal sealed class PieceResourceDefinition : Dictionary<string, string>
    {
        internal static List<PieceResourceDefinition> From(Piece.Requirement[] requirements)
        {
            List<PieceResourceDefinition> resources = new();
            if (requirements == null)
            {
                return resources;
            }

            foreach (Piece.Requirement requirement in requirements)
            {
                if (requirement?.m_resItem == null)
                {
                    continue;
                }

                PieceResourceDefinition resource = new();
                resource[GetPrefabName(requirement.m_resItem.gameObject)] = string.Join(", ", new[]
                {
                    Math.Max(0, requirement.m_amount).ToString(CultureInfo.InvariantCulture),
                    requirement.m_recover.ToString().ToLowerInvariant()
                });
                resources.Add(resource);
            }

            return resources;
        }

        internal static List<PieceResourceDefinition>? ToReference(List<PieceResourceDefinition>? resources)
        {
            if (resources == null)
            {
                return null;
            }

            List<PieceResourceDefinition> referenceResources = new();
            foreach (PieceResourceDefinition resource in resources)
            {
                foreach (KeyValuePair<string, string> pair in resource)
                {
                    string[] parts = SplitTuple(pair.Value);
                    int amount = Math.Max(0, GetIntPart(parts, 0, 1));
                    bool recover = GetBoolPart(parts, 1, true);

                    PieceResourceDefinition referenceResource = new();
                    referenceResource[pair.Key] = recover
                        ? amount.ToString(CultureInfo.InvariantCulture)
                        : FormatTuple(amount, false);
                    referenceResources.Add(referenceResource);
                }
            }

            return referenceResources.Count > 0 ? referenceResources : null;
        }
    }

    private sealed class PieceOrderItem
    {
        public GameObject Prefab { get; private set; } = null!;
        public Piece.PieceCategory Category { get; private set; }
        public int SortOrder { get; private set; }
        public int OriginalIndex { get; private set; }

        internal static PieceOrderItem From(GameObject piece, int originalIndex, IReadOnlyDictionary<string, int> sortOrders)
        {
            Piece component = piece.GetComponent<Piece>();
            string prefabName = GetPrefabName(piece);
            return new PieceOrderItem
            {
                Prefab = piece,
                Category = component != null ? component.m_category : Piece.PieceCategory.Misc,
                SortOrder = sortOrders.TryGetValue(prefabName, out int sortOrder)
                    ? sortOrder
                    : DefaultPieceSortOrder,
                OriginalIndex = originalIndex
            };
        }
    }

    private sealed class PieceBaseline
    {
        internal PieceBaseline(Piece piece, PieceDefinition definition)
        {
            Piece = piece;
            Definition = definition;
        }

        internal Piece Piece { get; }
        internal PieceDefinition Definition { get; }
    }

    private sealed class PieceTableAssignment
    {
        internal PieceTableAssignment(string pieceTable, string logContext)
        {
            PieceTable = pieceTable;
            LogContext = logContext;
        }

        internal string PieceTable { get; }
        internal string LogContext { get; }
    }

    private sealed class PieceTableMembershipSnapshot
    {
        private readonly Dictionary<string, List<string>> TableNamesByPiece;

        internal PieceTableMembershipSnapshot(Dictionary<string, List<string>> tableNamesByPiece)
        {
            TableNamesByPiece = tableNamesByPiece;
        }

        internal List<string> GetTableNames(string prefabName)
        {
            return TableNamesByPiece.TryGetValue(NormalizePrefabName(prefabName), out List<string> tableNames)
                ? tableNames
                : new List<string>();
        }

        internal bool IsOnlyInIgnoredTables(string prefabName)
        {
            List<string> tableNames = GetTableNames(prefabName);
            return tableNames.Count > 0 && tableNames.All(IsIgnoredPieceTableName);
        }
    }

    private sealed class PieceCategoryConfiguration
    {
        internal Dictionary<string, List<PieceCategoryOrderEntry>> Tables { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, string> TableContexts { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PieceCategoryOrderEntry
    {
        internal PieceCategoryOrderEntry(
            string category,
            string? label,
            string? sourcePieceTable = null,
            string logContext = PieceCategoryFileName)
        {
            Category = category;
            Label = label;
            SourcePieceTable = sourcePieceTable;
            LogContext = logContext;
        }

        internal string Category { get; }
        internal string? Label { get; }
        internal string? SourcePieceTable { get; }
        internal string LogContext { get; }

        internal string ToSerializedValue()
        {
            return Label == null ? Category : $"{Category}, {Label}";
        }
    }

    private sealed class PieceCategoryMoveRule
    {
        internal PieceCategoryMoveRule(
            string targetPieceTable,
            string sourcePieceTable,
            string category,
            string logContext)
        {
            TargetPieceTable = targetPieceTable;
            SourcePieceTable = sourcePieceTable;
            Category = category;
            LogContext = logContext;
        }

        internal string TargetPieceTable { get; }
        internal string SourcePieceTable { get; }
        internal string Category { get; }
        internal string LogContext { get; }
    }

    private sealed class PendingPieceCategoryMove
    {
        internal PendingPieceCategoryMove(
            PieceTable target,
            PieceTable source,
            string categoryName,
            string logContext)
        {
            Target = target;
            Source = source;
            CategoryName = categoryName;
            LogContext = logContext;
        }

        internal PieceTable Target { get; }
        internal PieceTable Source { get; }
        internal string CategoryName { get; }
        internal string LogContext { get; }
    }

    private sealed class ResolvedPieceCategoryMove
    {
        internal ResolvedPieceCategoryMove(PieceTable source, Piece.PieceCategory sourceCategory)
        {
            Source = source;
            SourceCategory = sourceCategory;
        }

        internal PieceTable Source { get; }
        internal Piece.PieceCategory SourceCategory { get; }
    }

    private sealed class HammerCategoryClaim
    {
        internal HammerCategoryClaim(string configuredName, Piece.PieceCategory targetCategory)
        {
            ConfiguredName = configuredName;
            TargetCategory = targetCategory;
        }

        internal string ConfiguredName { get; }
        internal Piece.PieceCategory TargetCategory { get; set; }
    }

    private sealed class PieceTableNameComparer : IComparer<string>
    {
        internal static readonly PieceTableNameComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            bool leftIsHammer = string.Equals(left, "Hammer", StringComparison.OrdinalIgnoreCase);
            bool rightIsHammer = string.Equals(right, "Hammer", StringComparison.OrdinalIgnoreCase);
            if (leftIsHammer != rightIsHammer)
            {
                return leftIsHammer ? -1 : 1;
            }

            int comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
        }
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T? x, T? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(T obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    private static string? FormatSapCollector(SapCollector? sapCollector)
    {
        return sapCollector == null
            ? null
            : FormatTuple(GetItemName(sapCollector.m_spawnItem), sapCollector.m_secPerUnit, sapCollector.m_maxLevel);
    }

    private static string? FormatBeehive(Beehive? beehive)
    {
        return beehive == null
            ? null
            : FormatTuple(beehive.m_secPerUnit, beehive.m_maxHoney);
    }

    internal sealed class FermenterDefinition
    {
        public float? Duration { get; set; }
        public bool? RequiresRoof { get; set; }
        public bool? RequiresCover { get; set; }
        public List<FermenterConversionDefinition>? Conversions { get; set; }

        internal static FermenterDefinition? From(Fermenter? fermenter)
        {
            return fermenter == null
                ? null
                : new FermenterDefinition
                {
                    Duration = fermenter.m_fermentationDuration,
                    RequiresRoof = true,
                    RequiresCover = true,
                    Conversions = FermenterConversionDefinition.From(fermenter.m_conversion)
                };
        }

        internal static FermenterDefinition? ToReference(FermenterDefinition? definition)
        {
            return definition == null
                ? null
                : ReferenceValue.ClonePruned(new FermenterDefinition
                {
                    Duration = definition.Duration,
                    Conversions = definition.Conversions
                });
        }
    }

    internal sealed class FermenterConversionDefinition : Dictionary<string, string>
    {
        internal static List<FermenterConversionDefinition> From(List<Fermenter.ItemConversion> conversions)
        {
            List<FermenterConversionDefinition> definitions = new();
            if (conversions == null)
            {
                return definitions;
            }

            foreach (Fermenter.ItemConversion conversion in conversions)
            {
                if (conversion?.m_from == null || conversion.m_to == null)
                {
                    continue;
                }

                FermenterConversionDefinition definition = new();
                definition[GetItemName(conversion.m_from)] = FormatTuple(GetItemName(conversion.m_to), conversion.m_producedItems);
                definitions.Add(definition);
            }

            return definitions;
        }
    }

    internal sealed class CookingStationDefinition
    {
        public string? Fuel { get; set; }
        public List<CookingStationConversionDefinition>? Conversions { get; set; }

        internal static CookingStationDefinition? From(CookingStation? cookingStation)
        {
            return cookingStation == null
                ? null
                : new CookingStationDefinition
                {
                    Fuel = FormatTuple(GetItemName(cookingStation.m_fuelItem), cookingStation.m_requireFire, cookingStation.m_maxFuel, cookingStation.m_secPerFuel),
                    Conversions = CookingStationConversionDefinition.From(cookingStation.m_conversion)
                };
        }
    }

    internal sealed class CookingStationConversionDefinition : Dictionary<string, string>
    {
        internal static List<CookingStationConversionDefinition> From(List<CookingStation.ItemConversion> conversions)
        {
            List<CookingStationConversionDefinition> definitions = new();
            if (conversions == null)
            {
                return definitions;
            }

            foreach (CookingStation.ItemConversion conversion in conversions)
            {
                if (conversion?.m_from == null || conversion.m_to == null)
                {
                    continue;
                }

                CookingStationConversionDefinition definition = new();
                definition[GetItemName(conversion.m_from)] = FormatTuple(GetItemName(conversion.m_to), conversion.m_cookTime);
                definitions.Add(definition);
            }

            return definitions;
        }
    }

    internal sealed class SmelterDefinition
    {
        public string? Input { get; set; }
        public string? Output { get; set; }
        public bool? RequiresRoof { get; set; }
        public List<SmelterConversionDefinition>? Conversions { get; set; }

        internal static SmelterDefinition? From(Smelter? smelter)
        {
            return smelter == null
                ? null
                : new SmelterDefinition
                {
                    Input = FormatTuple(GetItemName(smelter.m_fuelItem), smelter.m_maxFuel, smelter.m_maxOre),
                    Output = FormatTuple(smelter.m_fuelPerProduct, smelter.m_secPerProduct),
                    RequiresRoof = smelter.m_requiresRoof,
                    Conversions = SmelterConversionDefinition.From(smelter.m_conversion)
                };
        }
    }

    internal sealed class SmelterConversionDefinition : Dictionary<string, string>
    {
        internal static List<SmelterConversionDefinition> From(List<Smelter.ItemConversion> conversions)
        {
            List<SmelterConversionDefinition> definitions = new();
            if (conversions == null)
            {
                return definitions;
            }

            foreach (Smelter.ItemConversion conversion in conversions)
            {
                if (conversion?.m_from == null || conversion.m_to == null)
                {
                    continue;
                }

                SmelterConversionDefinition definition = new();
                definition[GetItemName(conversion.m_from)] = GetItemName(conversion.m_to);
                definitions.Add(definition);
            }

            return definitions;
        }
    }

    private static string? FormatContainer(Container? container)
    {
        return container == null
            ? null
            : FormatTuple(container.m_width, container.m_height);
    }

    private static float? FormatUniformScale(Vector3 scale)
    {
        return Math.Abs(scale.x - scale.y) <= 0.0001f && Math.Abs(scale.x - scale.z) <= 0.0001f
            ? scale.x
            : null;
    }

    private static string? FormatStationExtension(StationExtension[] extensions)
    {
        if (extensions == null || extensions.Length == 0)
        {
            return null;
        }

        StationExtension extension = extensions.FirstOrDefault(extension => extension != null);
        return extension == null
            ? null
            : FormatTuple(
                extension.m_craftingStation != null ? GetPrefabName(extension.m_craftingStation.gameObject) : "None",
                extension.m_maxStationDistance);
    }

    internal sealed class PieceVisualDefinition
    {
        public float? Scale { get; set; }
        public string? Material { get; set; }
        public string? Icon { get; set; }
        public string? IconRotation { get; set; }

        internal static PieceVisualDefinition? From(Piece piece)
        {
            return new PieceVisualDefinition
            {
                Scale = FormatUniformScale(piece.transform.localScale)
            };
        }
    }

    internal sealed class CraftingStationReferenceDefinition
    {
        public float? DiscoveryRange { get; set; }
        public string? BuildRange { get; set; }
        public bool? CraftRequiresRoof { get; set; }
        public bool? CraftRequiresFire { get; set; }
    }

    internal sealed class CraftingStationComponentDefinition
    {
        public string? Name { get; set; }
        public float? DiscoveryRange { get; set; }
        public string? BuildRange { get; set; }
        public bool? CraftRequiresRoof { get; set; }
        public bool? CraftRequiresFire { get; set; }
        public bool? ShowBasicRecipes { get; set; }
        public float? UseDistance { get; set; }
        public int? UseAnimation { get; set; }
        public string? CraftingSkill { get; set; }

        internal static CraftingStationComponentDefinition? From(CraftingStation? craftingStation)
        {
            return craftingStation == null
                ? null
                : new CraftingStationComponentDefinition
                {
                    Name = craftingStation.m_name,
                    DiscoveryRange = craftingStation.m_discoverRange,
                    BuildRange = FormatTuple(craftingStation.m_rangeBuild, craftingStation.m_extraRangePerLevel),
                    CraftRequiresRoof = craftingStation.m_craftRequireRoof,
                    CraftRequiresFire = craftingStation.m_craftRequireFire,
                    ShowBasicRecipes = craftingStation.m_showBasicRecipies,
                    UseDistance = craftingStation.m_useDistance,
                    UseAnimation = craftingStation.m_useAnimation,
                    CraftingSkill = craftingStation.m_craftingSkill.ToString()
                };
        }
    }

    private sealed class StationExtensionSnapshot
    {
        private CraftingStation? CraftingStation { get; set; }
        private float MaxStationDistance { get; set; }
        private bool Stack { get; set; }
        private GameObject? ConnectionPrefab { get; set; }
        private Vector3 ConnectionOffset { get; set; }
        private bool ContinuousConnection { get; set; }
        private Piece? Piece { get; set; }
        internal bool WasManaged { get; private set; }

        internal static StationExtensionSnapshot From(StationExtension extension, bool wasManaged)
        {
            return new StationExtensionSnapshot
            {
                CraftingStation = extension.m_craftingStation,
                MaxStationDistance = extension.m_maxStationDistance,
                Stack = extension.m_stack,
                ConnectionPrefab = extension.m_connectionPrefab,
                ConnectionOffset = extension.m_connectionOffset,
                ContinuousConnection = extension.m_continousConnection,
                Piece = extension.m_piece,
                WasManaged = wasManaged
            };
        }

        internal void Apply(StationExtension extension)
        {
            extension.m_craftingStation = CraftingStation;
            extension.m_maxStationDistance = MaxStationDistance;
            extension.m_stack = Stack;
            extension.m_connectionPrefab = ConnectionPrefab;
            extension.m_connectionOffset = ConnectionOffset;
            extension.m_continousConnection = ContinuousConnection;
            extension.m_piece = Piece != null ? Piece : extension.GetComponent<Piece>();
        }
    }

    private static string FormatCraftingStation(CraftingStation? craftingStation)
    {
        return craftingStation != null ? GetPrefabName(craftingStation.gameObject) : "None";
    }

    private static string GetItemName(ItemDrop? itemDrop)
    {
        return itemDrop != null ? GetPrefabName(itemDrop.gameObject) : "None";
    }

    private static string FormatTuple(params object[] values)
    {
        return string.Join(", ", values.Select(FormatTupleValue));
    }

    private static string FormatTupleValue(object value)
    {
        return value switch
        {
            float floatValue => floatValue.ToString("0.###", CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString("0.###", CultureInfo.InvariantCulture),
            int intValue => intValue.ToString(CultureInfo.InvariantCulture),
            bool boolValue => boolValue.ToString().ToLowerInvariant(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
    }

}

internal sealed class DataForgePieceComponentOwnership : MonoBehaviour
{
    [SerializeField]
    private StationExtension? _stationExtension;

    [SerializeField]
    private CraftingStation? _craftingStation;

    [SerializeField]
    private string _visualPrefabKey = "";

    [SerializeField]
    private bool _hasIconOwnership;

    [SerializeField]
    private Sprite? _originalPieceIcon;

    [SerializeField]
    private Sprite? _appliedPieceIcon;

    [SerializeField]
    private CraftingStation? _iconCraftingStation;

    [SerializeField]
    private Sprite? _originalCraftingStationIcon;

    [SerializeField]
    private List<RendererMaterialOwnership> _materialOwnership = new();

    internal StationExtension? StationExtension
    {
        get => _stationExtension;
        set => _stationExtension = value;
    }

    internal CraftingStation? CraftingStation
    {
        get => _craftingStation;
        set => _craftingStation = value;
    }

    internal string VisualPrefabKey => _visualPrefabKey;
    internal bool HasIconOwnership => _hasIconOwnership;
    internal bool HasMaterialOwnership => _materialOwnership.Count > 0;
    internal bool HasVisualOwnership => HasIconOwnership || HasMaterialOwnership;

    internal void TrackIconOwnership(
        string prefabKey,
        Piece piece,
        CraftingStation? craftingStation,
        Sprite appliedIcon)
    {
        PrepareVisualOwnership(prefabKey);
        if (!_hasIconOwnership)
        {
            _hasIconOwnership = true;
            _originalPieceIcon = piece.m_icon;
            _iconCraftingStation = craftingStation;
            _originalCraftingStationIcon = craftingStation != null ? craftingStation.m_icon : null;
        }
        else if (_iconCraftingStation == null && craftingStation != null)
        {
            _iconCraftingStation = craftingStation;
            _originalCraftingStationIcon =
                ReferenceEquals(craftingStation.m_icon, _appliedPieceIcon) &&
                ReferenceEquals(craftingStation, _craftingStation)
                    ? _originalPieceIcon
                    : craftingStation.m_icon;
        }

        _appliedPieceIcon = appliedIcon;
    }

    internal void RestoreIconOwnership(
        Piece? piece,
        CraftingStation? craftingStation,
        bool craftingStationIsManaged)
    {
        if (!_hasIconOwnership)
        {
            return;
        }

        if (piece != null && ReferenceEquals(piece.m_icon, _appliedPieceIcon))
        {
            piece.m_icon = _originalPieceIcon;
        }

        if (craftingStation != null &&
            ReferenceEquals(craftingStation.m_icon, _appliedPieceIcon))
        {
            if (_iconCraftingStation != null &&
                ReferenceEquals(craftingStation, _iconCraftingStation))
            {
                craftingStation.m_icon = _originalCraftingStationIcon;
            }
            else if (craftingStationIsManaged)
            {
                craftingStation.m_icon = _originalPieceIcon;
            }
        }

        ClearIconOwnership();
    }

    internal void ClearIconOwnership()
    {
        _hasIconOwnership = false;
        _originalPieceIcon = null;
        _appliedPieceIcon = null;
        _iconCraftingStation = null;
        _originalCraftingStationIcon = null;
        ClearVisualPrefabKeyIfUnused();
    }

    internal void TrackMaterialOwnership(
        string prefabKey,
        Renderer renderer,
        Material[] originalMaterials,
        Material appliedMaterial)
    {
        PrepareVisualOwnership(prefabKey);
        RendererMaterialOwnership? ownership = _materialOwnership
            .FirstOrDefault(entry => ReferenceEquals(entry.Renderer, renderer));
        if (ownership == null)
        {
            ownership = new RendererMaterialOwnership(renderer, originalMaterials);
            _materialOwnership.Add(ownership);
        }

        ownership.AppliedMaterial = appliedMaterial;
    }

    internal void RestoreMaterialOwnership()
    {
        foreach (RendererMaterialOwnership ownership in _materialOwnership)
        {
            ownership.RestoreIfOwned();
        }

        ClearMaterialOwnership();
    }

    internal void ClearMaterialOwnership()
    {
        _materialOwnership.Clear();
        ClearVisualPrefabKeyIfUnused();
    }

    private void PrepareVisualOwnership(string prefabKey)
    {
        if (!HasVisualOwnership)
        {
            _visualPrefabKey = prefabKey;
            return;
        }

        if (string.Equals(_visualPrefabKey, prefabKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ClearIconOwnership();
        ClearMaterialOwnership();
        _visualPrefabKey = prefabKey;
    }

    private void ClearVisualPrefabKeyIfUnused()
    {
        if (!HasVisualOwnership)
        {
            _visualPrefabKey = "";
        }
    }

    [Serializable]
    private sealed class RendererMaterialOwnership
    {
        [SerializeField]
        private Renderer? _renderer;

        [SerializeField]
        private Material[] _originalMaterials = Array.Empty<Material>();

        [SerializeField]
        private Material? _appliedMaterial;

        internal RendererMaterialOwnership(Renderer renderer, Material[] originalMaterials)
        {
            _renderer = renderer;
            _originalMaterials = originalMaterials.ToArray();
        }

        internal Renderer? Renderer => _renderer;

        internal Material? AppliedMaterial
        {
            set => _appliedMaterial = value;
        }

        internal void RestoreIfOwned()
        {
            if (_renderer == null)
            {
                return;
            }

            Material[] currentMaterials = _renderer.sharedMaterials;
            bool changed = false;
            int slotCount = Math.Min(currentMaterials.Length, _originalMaterials.Length);
            for (int index = 0; index < slotCount; index++)
            {
                if (_originalMaterials[index] != null &&
                    ReferenceEquals(currentMaterials[index], _appliedMaterial))
                {
                    currentMaterials[index] = _originalMaterials[index];
                    changed = true;
                }
            }

            if (changed)
            {
                _renderer.sharedMaterials = currentMaterials;
            }
        }

    }
}
