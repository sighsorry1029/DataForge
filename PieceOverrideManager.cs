using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using HarmonyLib;
using ServerSync;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static DataForge.DataForgeValue;

namespace DataForge;

internal static class PieceOverrideManager
{
    private const string DomainName = "pieces";
    private const string ReferenceFileName = "pieces.reference.yml";
    private const string FullScaffoldFileName = "pieces.full.yml";
    private const string SyncedPayloadKey = "pieces";
    private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;
    private const int DefaultPieceSortOrder = 100;
    private const string ReferenceStateKey = "pieces";
    private const string ReferenceLogicVersion = "2026-06-26-piece-visual-scale-v1";
    private static readonly HashSet<string> IgnoredCategoryNames = new(StringComparer.OrdinalIgnoreCase)
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
    private static readonly Dictionary<string, PieceDefinition> Baselines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<PieceTable, List<GameObject>> PieceTableOrderBaselines = new(ReferenceComparer<PieceTable>.Instance);
    private static readonly HashSet<int> ManagedStationExtensionInstanceIds = new();
    private static readonly HashSet<int> ManagedCraftingStationInstanceIds = new();
    private static readonly Dictionary<int, List<StationExtensionSnapshot>> StationExtensionRemovalSnapshots = new();
    private static readonly Dictionary<int, List<RendererMaterialSnapshot>> PieceMaterialSnapshots = new();
    private static readonly HashSet<string> RuntimeAppliedPieceKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PieceTableAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hammer"] = "_HammerPieceTable",
        ["Hoe"] = "_HoePieceTable",
        ["Cultivator"] = "_CultivatorPieceTable",
        ["ServingTray"] = "_FeasterPieceTable"
    };
    private static Dictionary<Piece.PieceCategory, string>? JotunnPieceCategoryNames;
    private static Dictionary<string, Piece.PieceCategory>? JotunnPieceCategoryValues;
    private static bool JotunnPieceCategoryMapLoaded;
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
    private static Dictionary<string, string> ActiveEntrySignaturesByPiece = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string>? PendingChangedPieceKeys;
    private static bool HasPendingScopedApply;
    private static bool ForceNextFullApply = true;
    private static CustomSyncedValue<string>? SyncedPayload;
    private static string? LastAppliedSyncedPayload;
    private static FileSystemWatcher? Watcher;
    private static DataForgeFileWatcher.DebouncedAction? ReloadDebouncer;
    private static bool GameDataReady;
    private static bool ObjectDbReady;
    private static bool PieceTablesReady;
    private static bool RuntimeStateWasApplied;
    private static bool PieceTableSortWasApplied;
    private static bool PieceTableMembershipWasApplied;
    private static bool CraftingStationTopologyChanged;

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
        List<PieceEntry> entries = LoadEntriesFromDisk();
        lock (StateLock)
        {
            SetActiveEntries(entries);
        }

        PublishPayload(SerializeEntries(entries));
        ApplyCurrentConfiguration();
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

        bool hasActiveRuntimeDefinitions = HasActiveRuntimeDefinitions();
        Dictionary<string, int> activeSortOrders = GetActiveSortOrders();
        Dictionary<string, PieceTableAssignment> activePieceTableAssignments = GetActivePieceTableAssignments();
        HashSet<string> activeRemovedPieces = GetActiveRemovedPieces();
        HashSet<string>? changedPieceKeys;
        lock (StateLock)
        {
            changedPieceKeys = ConsumePendingChangedPieceKeys();
        }

        if (changedPieceKeys is { Count: 0 })
        {
            return;
        }

        bool hasActiveSortOrders = activeSortOrders.Count > 0;
        bool hasActivePieceTableAssignments = activePieceTableAssignments.Count > 0;
        bool hasActiveRemovedPieces = activeRemovedPieces.Count > 0;
        if (!hasActiveRuntimeDefinitions &&
            !hasActiveSortOrders &&
            !hasActivePieceTableAssignments &&
            !hasActiveRemovedPieces &&
            !RuntimeStateWasApplied &&
            !PieceTableSortWasApplied &&
            !PieceTableMembershipWasApplied)
        {
            return;
        }

        bool shouldTouchRuntime = hasActiveRuntimeDefinitions || RuntimeStateWasApplied;
        HashSet<string>? runtimePieceKeys = shouldTouchRuntime
            ? GetRuntimeApplyKeys(changedPieceKeys, hasActiveRuntimeDefinitions)
            : null;
        if (shouldTouchRuntime)
        {
            CaptureBaselinesForPiecesIfNeeded(runtimePieceKeys);
            ApplyToPrefabDefinitions(runtimePieceKeys);
        }

        ApplyPieceTableStructure(activePieceTableAssignments, activeSortOrders, activeRemovedPieces);

        if (shouldTouchRuntime)
        {
            ApplyToLoadedInstances(runtimePieceKeys);
            InvalidateCraftingStationExtensionCaches();
            RefreshLocalBuildPieces();
        }

        ReapplyRecipesIfCraftingStationTopologyChanged();

        RuntimeStateWasApplied = hasActiveRuntimeDefinitions;
        RuntimeAppliedPieceKeys.Clear();
        if (hasActiveRuntimeDefinitions)
        {
            foreach (string key in GetRuntimeApplyKeys(null, hasActiveRuntimeDefinitions))
            {
                RuntimeAppliedPieceKeys.Add(key);
            }
        }
        PieceTableSortWasApplied = hasActiveSortOrders;
        PieceTableMembershipWasApplied = hasActivePieceTableAssignments || hasActiveRemovedPieces;
        VneiRefreshManager.RequestRefresh(DomainName);
    }

    private static bool ShouldSkipRemoteClientBaselineWork()
    {
        if (!DataForgePlugin.IsRemoteServerClient ||
            RuntimeStateWasApplied ||
            PieceTableSortWasApplied ||
            PieceTableMembershipWasApplied)
        {
            return false;
        }

        lock (StateLock)
        {
            return ActiveEntries.Count == 0;
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

        WriteGeneratedArtifacts();
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

    internal static void OnWorldShutdown()
    {
        GameDataReady = false;
        ObjectDbReady = false;
        PieceTablesReady = false;
        RuntimeStateWasApplied = false;
        RuntimeAppliedPieceKeys.Clear();
        ManagedCraftingStationInstanceIds.Clear();
        ManagedStationExtensionInstanceIds.Clear();
        StationExtensionRemovalSnapshots.Clear();
        PieceMaterialSnapshots.Clear();
        CraftingStationTopologyChanged = false;
        PieceTableSortWasApplied = false;
        PieceTableMembershipWasApplied = false;
    }

    internal static void OnPieceTablesReady()
    {
        if (ZNetScene.instance == null)
        {
            return;
        }

        GameDataReady = true;
        PieceTablesReady = true;
        if (ShouldSkipRemoteClientBaselineWork())
        {
            return;
        }

        WriteGeneratedArtifacts();
        ApplyCurrentConfiguration();
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
            DataForgePlugin.Log.LogDebug("Reloading piece YAML files...");
            ReloadFromDiskAndSync();
            DataForgePlugin.Log.LogInfo("Piece YAML reload complete.");
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Error reloading piece YAML files: {ex}");
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
        List<PieceEntry> entries = DeserializeEntries(payload, "synced piece payload");
        lock (StateLock)
        {
            SetActiveEntries(entries);
        }

        ApplyCurrentConfiguration();
    }

    private static void SetActiveEntries(List<PieceEntry> entries)
    {
        Dictionary<string, string> signatures = BuildEntrySignaturesByPiece(entries);
        if (!ForceNextFullApply)
        {
            PendingChangedPieceKeys = GetChangedKeys(ActiveEntrySignaturesByPiece, signatures);
            HasPendingScopedApply = true;
        }

        ActiveEntries = entries;
        ActiveRuntimeEntriesByPiece = BuildActiveRuntimeEntriesByPiece(entries);
        ActiveEntrySignaturesByPiece = signatures;
    }

    private static HashSet<string>? ConsumePendingChangedPieceKeys()
    {
        if (ForceNextFullApply)
        {
            ForceNextFullApply = false;
            PendingChangedPieceKeys = null;
            HasPendingScopedApply = false;
            return null;
        }

        if (!HasPendingScopedApply)
        {
            return null;
        }

        HashSet<string> changedKeys = PendingChangedPieceKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PendingChangedPieceKeys = null;
        HasPendingScopedApply = false;
        return changedKeys;
    }

    private static Dictionary<string, string> BuildEntrySignaturesByPiece(List<PieceEntry> entries)
    {
        Dictionary<string, List<PieceEntry>> entriesByPiece = new(StringComparer.OrdinalIgnoreCase);
        foreach (PieceEntry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Piece))
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

        Dictionary<string, string> signatures = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<PieceEntry>> pair in entriesByPiece)
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

    private static List<PieceEntry> LoadEntriesFromDisk()
    {
        return DataForgeOverrideFiles.LoadEntries(GetOverrideFiles(), DeserializeEntries);
    }

    private static List<PieceEntry> DeserializeEntries(string yaml, string source)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new List<PieceEntry>();
        }

        try
        {
            List<PieceEntry>? entries = Deserializer.Deserialize<List<PieceEntry>>(yaml);
            return NormalizeEntries(entries, source);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Failed to parse {source}: {ex.Message}");
            return new List<PieceEntry>();
        }
    }

    private static List<PieceEntry> NormalizeEntries(List<PieceEntry>? entries, string source)
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
            string sourceContext = DataForgeLogContext.FormatSource(source, entryIndex);
            if (string.IsNullOrWhiteSpace(entry.Piece))
            {
                DataForgeLogContext.Warning($"{sourceContext}: Skipping piece entry without piece.");
                continue;
            }

            entry.Piece = NormalizePrefabName(entry.Piece);
            entry.SetLogContext($"{sourceContext} piece={entry.Piece}");
            if (IsIgnoredCategoryName(entry.Category))
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

        return fileName.StartsWith(DomainName, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureConfigDirectoryAndDefaultOverride()
    {
        DataForgeOverrideFiles.EnsureDefaultOverride(ConfigDirectory, $"{DomainName}.yml", GetOverrideFiles, DefaultOverrideTemplate);
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
            "#   category: Building                  # Piece.PieceCategory enum; Feasts, Food, and Meads are ignored by DataForge.",
            "#   sortOrder: 100                      # lower appears earlier in the same build tab; omitted keeps original order.",
            "#   needStation: None                   # station prefab/name needed to build; None clears the station requirement.",
            "#   canBeRemoved: true                  # false prevents removing the placed piece with a hammer.",
            "#   health: 100                         # max structural health when the prefab is damageable.",
            "#   comfort: 0, None                    # comfort amount, comfort group: None, Fire, Bed, Banner, Chair, Table, Carpet.",
            "#   visual:",
            "#     scale: 1                          # uniform prefab scale for newly placed pieces; larger values also affect collider/support behavior.",
            "#     material: wood                    # material name from z_materials.reference.txt; replaces piece renderer material slots.",
            "#   resources:",
            "#   - Wood: 2                           # item: amount; add ', false' to disable build resource recovery.",
            "#   sapCollector: Sap, 60, 10           # produced item, seconds per unit, max stored units.",
            "#   beehive: 1200, 4                    # seconds per produced honey, max stored honey.",
            "#   fermenter:",
            "#     duration: 2400                    # fermentation duration in seconds.",
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
            if (normalizedName.Length == 0 || !seen.Add(normalizedName) || Baselines.ContainsKey(normalizedName))
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
        if (string.IsNullOrWhiteSpace(prefabName) || Baselines.ContainsKey(prefabName))
        {
            return false;
        }

        Baselines[prefabName] = PieceDefinition.From(piece);
        return true;
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

    private static void ApplyToPrefabDefinitions(HashSet<string>? pieceKeys = null)
    {
        IEnumerable<(string PrefabName, Piece Piece)> pieces = pieceKeys == null
            ? GetPrefabPieces()
            : GetPrefabPieces(pieceKeys);

        foreach ((string prefabName, Piece piece) in pieces)
        {
            ApplyConfiguredState(piece.gameObject, prefabName, adjustHealthZdo: false, applyVisualScale: true);
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

    private static void ApplyToLoadedInstances(HashSet<string>? pieceKeys = null)
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

            ApplyConfiguredState(piece.gameObject, prefabName, adjustHealthZdo: true, applyVisualScale: false);
        }
    }

    private static bool IsManagedPiece(GameObject gameObject, Piece? piece = null)
    {
        return gameObject != null &&
               (piece ?? gameObject.GetComponent<Piece>()) != null &&
               gameObject.GetComponent<WearNTear>() != null;
    }

    private static void ApplyConfiguredState(GameObject gameObject, string prefabName, bool adjustHealthZdo, bool applyVisualScale)
    {
        RestorePieceVisualMaterials(gameObject);
        bool finalHasStationExtension = false;
        bool finalHasCraftingStation = false;
        if (Baselines.TryGetValue(prefabName, out PieceDefinition? baseline))
        {
            finalHasStationExtension = baseline.StationExtension != null;
            finalHasCraftingStation = baseline.CraftingStation != null;
            ApplyDefinition(gameObject, baseline, adjustHealthZdo, applyVisualScale);
        }

        if (!DataForgePlugin.PieceOverridesEnabled)
        {
            RemoveManagedComponentsIfAbsent(gameObject, finalHasStationExtension, finalHasCraftingStation);
            return;
        }

        List<PieceEntry>? entries;
        lock (StateLock)
        {
            ActiveRuntimeEntriesByPiece.TryGetValue(prefabName, out entries);
        }

        if (entries == null)
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
                ApplyDefinition(gameObject, definition, adjustHealthZdo, applyVisualScale);
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

    private static bool HasActiveRuntimeDefinitions()
    {
        if (!DataForgePlugin.PieceOverridesEnabled)
        {
            return false;
        }

        lock (StateLock)
        {
            return ActiveRuntimeEntriesByPiece.Count > 0;
        }
    }

    private static HashSet<string> GetRuntimeApplyKeys(HashSet<string>? changedPieceKeys, bool hasActiveRuntimeDefinitions)
    {
        if (changedPieceKeys != null)
        {
            return new HashSet<string>(changedPieceKeys, StringComparer.OrdinalIgnoreCase);
        }

        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        if (hasActiveRuntimeDefinitions)
        {
            lock (StateLock)
            {
                foreach (string key in ActiveRuntimeEntriesByPiece.Keys)
                {
                    keys.Add(key);
                }
            }
        }

        if (RuntimeStateWasApplied)
        {
            foreach (string key in RuntimeAppliedPieceKeys)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static Dictionary<string, int> GetActiveSortOrders()
    {
        Dictionary<string, int> sortOrders = new(StringComparer.OrdinalIgnoreCase);
        if (!DataForgePlugin.PieceOverridesEnabled)
        {
            return sortOrders;
        }

        lock (StateLock)
        {
            foreach (PieceEntry entry in ActiveEntries)
            {
                if (entry.Override && !entry.Remove && entry.SortOrder.HasValue)
                {
                    sortOrders[entry.Piece] = entry.SortOrder.Value;
                }
            }
        }

        return sortOrders;
    }

    private static Dictionary<string, PieceTableAssignment> GetActivePieceTableAssignments()
    {
        Dictionary<string, PieceTableAssignment> assignments = new(StringComparer.OrdinalIgnoreCase);
        if (!DataForgePlugin.PieceOverridesEnabled)
        {
            return assignments;
        }

        lock (StateLock)
        {
            foreach (PieceEntry entry in ActiveEntries)
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
        }

        return assignments;
    }

    private static HashSet<string> GetActiveRemovedPieces()
    {
        HashSet<string> removedPieces = new(StringComparer.OrdinalIgnoreCase);
        if (!DataForgePlugin.PieceOverridesEnabled)
        {
            return removedPieces;
        }

        lock (StateLock)
        {
            foreach (PieceEntry entry in ActiveEntries)
            {
                if (entry.Override && entry.Remove && !string.IsNullOrWhiteSpace(entry.Piece))
                {
                    removedPieces.Add(entry.Piece);
                }
            }
        }

        return removedPieces;
    }

    private static void ApplyDefinition(GameObject gameObject, PieceDefinition definition, bool adjustHealthZdo, bool applyVisualScale)
    {
        Piece piece = gameObject.GetComponent<Piece>();
        if (piece != null && definition.Piece != null)
        {
            ApplyPieceDefinition(piece, definition.Piece, adjustHealthZdo);
        }

        ApplySupportedComponentDefinitions(gameObject, definition, applyVisualScale);
    }

    private static void ApplyPieceDefinition(Piece piece, PieceComponentDefinition definition, bool adjustHealthZdo)
    {
        Copy(definition.Name, value =>
        {
            piece.m_name = value;
            Door door = piece.GetComponent<Door>();
            if (door != null)
            {
                door.m_name = value;
            }
        });
        Copy(definition.Description, value => piece.m_description = value);
        ApplyCategory(piece, definition.Category);
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

        if (!TryResolvePieceCategory(trimmedCategoryName, out Piece.PieceCategory category))
        {
            DataForgeLogContext.Warning($"{GetPrefabName(piece.gameObject)} has unknown piece category '{trimmedCategoryName}'.");
            return;
        }

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
                    continue;
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

    private static void ApplySupportedComponentDefinitions(GameObject gameObject, PieceDefinition definition, bool applyVisualScale)
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

        if (definition.Visual != null)
        {
            ApplyPieceVisualDefinition(gameObject, definition.Visual, applyVisualScale);
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
        if (definition.Conversions != null)
        {
            fermenter.m_conversion = BuildFermenterConversions(fermenter, definition.Conversions);
        }
    }

    private static List<Fermenter.ItemConversion> BuildFermenterConversions(Fermenter fermenter, List<FermenterConversionDefinition> definitions)
    {
        List<Fermenter.ItemConversion> conversions = new();
        string prefabName = GetPrefabName(fermenter.gameObject);
        foreach (FermenterConversionDefinition definition in definitions)
        {
            foreach (KeyValuePair<string, string> pair in definition)
            {
                string[] parts = SplitTuple(pair.Value);
                if (parts.Length == 0 || parts[0].Length == 0)
                {
                    DataForgeLogContext.Warning($"{prefabName} has fermenter conversion '{pair.Key}' without output item.");
                    continue;
                }

                ItemDrop? from = ResolveItemDrop(pair.Key);
                ItemDrop? to = ResolveItemDrop(parts[0]);
                if (from == null || to == null)
                {
                    DataForgeLogContext.Warning($"{prefabName} has unknown fermenter conversion '{pair.Key}: {pair.Value}'.");
                    continue;
                }

                conversions.Add(new Fermenter.ItemConversion
                {
                    m_from = from,
                    m_to = to,
                    m_producedItems = Math.Max(1, GetIntPart(parts, 1, 4))
                });
            }
        }

        return conversions;
    }

    private static void ApplyCookingStationDefinition(CookingStation cookingStation, CookingStationDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.Fuel))
        {
            string[] parts = SplitTuple(definition.Fuel);
            if (parts.Length > 0 && parts[0].Length > 0)
            {
                if (IsNone(parts[0]))
                {
                    cookingStation.m_fuelItem = null;
                    cookingStation.m_useFuel = false;
                }
                else
                {
                    ItemDrop? fuelItem = ResolveItemDrop(parts[0]);
                    if (fuelItem == null)
                    {
                        DataForgeLogContext.Warning($"{GetPrefabName(cookingStation.gameObject)} has unknown cookingStation fuel item '{parts[0]}'.");
                    }
                    else
                    {
                        cookingStation.m_fuelItem = fuelItem;
                        cookingStation.m_useFuel = true;
                    }
                }
            }

            CopyBoolPart(parts, 1, parsed => cookingStation.m_requireFire = parsed);
            CopyIntPart(parts, 2, parsed => cookingStation.m_maxFuel = Math.Max(0, parsed));
            CopyIntPart(parts, 3, parsed => cookingStation.m_secPerFuel = Math.Max(0, parsed));
        }

        if (definition.Conversions != null)
        {
            cookingStation.m_conversion = BuildCookingStationConversions(cookingStation, definition.Conversions);
        }
    }

    private static List<CookingStation.ItemConversion> BuildCookingStationConversions(CookingStation cookingStation, List<CookingStationConversionDefinition> definitions)
    {
        List<CookingStation.ItemConversion> conversions = new();
        string prefabName = GetPrefabName(cookingStation.gameObject);
        foreach (CookingStationConversionDefinition definition in definitions)
        {
            foreach (KeyValuePair<string, string> pair in definition)
            {
                string[] parts = SplitTuple(pair.Value);
                if (parts.Length == 0 || parts[0].Length == 0)
                {
                    DataForgeLogContext.Warning($"{prefabName} has cookingStation conversion '{pair.Key}' without output item.");
                    continue;
                }

                ItemDrop? from = ResolveItemDrop(pair.Key);
                ItemDrop? to = ResolveItemDrop(parts[0]);
                if (from == null || to == null)
                {
                    DataForgeLogContext.Warning($"{prefabName} has unknown cookingStation conversion '{pair.Key}: {pair.Value}'.");
                    continue;
                }

                conversions.Add(new CookingStation.ItemConversion
                {
                    m_from = from,
                    m_to = to,
                    m_cookTime = Math.Max(0f, GetFloatPart(parts, 1, 10f))
                });
            }
        }

        return conversions;
    }

    private static void ApplySmelterDefinition(Smelter smelter, SmelterDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.Input))
        {
            string[] parts = SplitTuple(definition.Input);
            if (parts.Length > 0 && parts[0].Length > 0)
            {
                string fuelItem = parts[0];
                if (IsNone(fuelItem))
                {
                    smelter.m_fuelItem = null;
                }
                else
                {
                    ItemDrop? item = ResolveItemDrop(fuelItem);
                    if (item == null)
                    {
                        DataForgeLogContext.Warning($"{GetPrefabName(smelter.gameObject)} has unknown smelter fuel item '{fuelItem}'.");
                    }
                    else
                    {
                        smelter.m_fuelItem = item;
                    }
                }
            }

            CopyIntPart(parts, 1, parsed => smelter.m_maxFuel = Math.Max(0, parsed));
            CopyIntPart(parts, 2, parsed => smelter.m_maxOre = Math.Max(0, parsed));
        }

        if (!string.IsNullOrWhiteSpace(definition.Output))
        {
            string[] parts = SplitTuple(definition.Output);
            CopyIntPart(parts, 0, parsed => smelter.m_fuelPerProduct = Math.Max(0, parsed));
            CopyFloatPart(parts, 1, parsed => smelter.m_secPerProduct = Math.Max(0f, parsed));
        }

        Copy(definition.RequiresRoof, value => smelter.m_requiresRoof = value);
        if (definition.Conversions != null)
        {
            smelter.m_conversion = BuildSmelterConversions(smelter, definition.Conversions);
        }
    }

    private static List<Smelter.ItemConversion> BuildSmelterConversions(Smelter smelter, List<SmelterConversionDefinition> definitions)
    {
        List<Smelter.ItemConversion> conversions = new();
        string prefabName = GetPrefabName(smelter.gameObject);
        foreach (SmelterConversionDefinition definition in definitions)
        {
            foreach (KeyValuePair<string, string> pair in definition)
            {
                string output = pair.Value.Trim();
                if (output.Length == 0)
                {
                    DataForgeLogContext.Warning($"{prefabName} has smelter conversion '{pair.Key}' without output item.");
                    continue;
                }

                ItemDrop? from = ResolveItemDrop(pair.Key);
                ItemDrop? to = ResolveItemDrop(output);
                if (from == null || to == null)
                {
                    DataForgeLogContext.Warning($"{prefabName} has unknown smelter conversion '{pair.Key}: {pair.Value}'.");
                    continue;
                }

                conversions.Add(new Smelter.ItemConversion
                {
                    m_from = from,
                    m_to = to
                });
            }
        }

        return conversions;
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

    private static void ApplyPieceVisualDefinition(GameObject gameObject, PieceVisualDefinition definition, bool applyVisualScale)
    {
        if (applyVisualScale)
        {
            ApplyVisualScale(gameObject, definition.Scale);
        }
        if (string.IsNullOrWhiteSpace(definition.Material))
        {
            return;
        }

        string prefabName = GetPrefabName(gameObject);
        string materialName = (definition.Material ?? "").Trim();
        Material? material = ItemVisualOverrides.ResolveMaterial(materialName);
        if (material == null)
        {
            DataForgeLogContext.Warning($"{prefabName} has unknown visual material '{materialName}'. Check z_materials.reference.txt.");
            return;
        }

        List<Renderer> renderers = GetPieceVisualRenderers(gameObject);
        if (renderers.Count == 0)
        {
            DataForgeLogContext.Warning($"{prefabName} has no piece renderers for visual material override.");
            return;
        }

        StorePieceMaterialSnapshots(gameObject, renderers);
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

            renderer.sharedMaterials = updatedMaterials;
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

    private static void StorePieceMaterialSnapshots(GameObject gameObject, List<Renderer> renderers)
    {
        int key = gameObject.GetInstanceID();
        if (PieceMaterialSnapshots.ContainsKey(key))
        {
            return;
        }

        PieceMaterialSnapshots[key] = renderers
            .Select(renderer => new RendererMaterialSnapshot(renderer, renderer.sharedMaterials.ToArray()))
            .ToList();
    }

    private static void RestorePieceVisualMaterials(GameObject gameObject)
    {
        int key = gameObject.GetInstanceID();
        if (!PieceMaterialSnapshots.TryGetValue(key, out List<RendererMaterialSnapshot> snapshots))
        {
            return;
        }

        PieceMaterialSnapshots.Remove(key);
        foreach (RendererMaterialSnapshot snapshot in snapshots)
        {
            if (snapshot.Renderer != null)
            {
                snapshot.Renderer.sharedMaterials = snapshot.Materials.ToArray();
            }
        }
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
            ManagedStationExtensionInstanceIds.Add(gameObject.GetInstanceID());
            extension.m_piece = gameObject.GetComponent<Piece>();
            extension.m_continousConnection = false;
            extension.m_stack = false;
        }

        ApplyStationExtensionDefinition(extension, definition);
    }

    private static void ApplyStationExtensionDefinition(StationExtension extension, string definition)
    {
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
    }

    private static void RemoveStationExtensions(GameObject gameObject)
    {
        StationExtension[] extensions = gameObject.GetComponents<StationExtension>();
        if (extensions.Length == 0)
        {
            RemoveManagedStationExtensionIfPresent(gameObject);
            return;
        }

        int key = gameObject.GetInstanceID();
        if (!StationExtensionRemovalSnapshots.ContainsKey(key))
        {
            StationExtensionRemovalSnapshots[key] = extensions
                .Where(extension => extension != null)
                .Select(StationExtensionSnapshot.From)
                .ToList();
        }

        foreach (StationExtension extension in extensions)
        {
            RemoveStationExtensionComponent(extension);
        }

        ManagedStationExtensionInstanceIds.Remove(key);
    }

    private static void RestoreRemovedStationExtensions(GameObject gameObject)
    {
        int key = gameObject.GetInstanceID();
        if (!StationExtensionRemovalSnapshots.TryGetValue(key, out List<StationExtensionSnapshot> snapshots))
        {
            return;
        }

        StationExtensionRemovalSnapshots.Remove(key);
        foreach (StationExtensionSnapshot snapshot in snapshots)
        {
            if (!CanAddStationExtension(gameObject, GetPrefabName(gameObject)))
            {
                continue;
            }

            StationExtension extension = gameObject.AddComponent<StationExtension>();
            snapshot.Apply(extension);
            EnableStationExtension(extension);
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

        StationExtension.m_allExtensions.Remove(extension);
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

    private static void RemoveManagedStationExtensionIfPresent(GameObject gameObject)
    {
        if (!ManagedStationExtensionInstanceIds.Remove(gameObject.GetInstanceID()))
        {
            return;
        }

        StationExtension extension = gameObject.GetComponent<StationExtension>();
        if (extension == null)
        {
            return;
        }

        StationExtension.m_allExtensions.Remove(extension);
        try
        {
            UnityEngine.Object.DestroyImmediate(extension);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not immediately remove managed StationExtension from '{GetPrefabName(gameObject)}': {ex.Message}");
            UnityEngine.Object.Destroy(extension);
        }
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
        ManagedCraftingStationInstanceIds.Add(gameObject.GetInstanceID());
        CraftingStationTopologyChanged = true;

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
        int key = gameObject.GetInstanceID();
        if (!ManagedCraftingStationInstanceIds.Remove(key))
        {
            return;
        }

        CraftingStation craftingStation = gameObject.GetComponent<CraftingStation>();
        if (craftingStation == null)
        {
            CraftingStationTopologyChanged = true;
            return;
        }

        CraftingStationTopologyChanged = true;
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

        bool isManaged = ManagedCraftingStationInstanceIds.Contains(craftingStation.gameObject.GetInstanceID());
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
        PieceTableCategoryGuard.EnsureCategory(pieceTable, category);
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

    private static void ApplyPieceTableStructure(
        IReadOnlyDictionary<string, PieceTableAssignment> pieceTableAssignments,
        IReadOnlyDictionary<string, int> sortOrders,
        IReadOnlyCollection<string> removedPieces)
    {
        if (!PieceTablesReady)
        {
            return;
        }

        bool shouldTouchPieceTables =
            pieceTableAssignments.Count > 0 ||
            removedPieces.Count > 0 ||
            sortOrders.Count > 0 ||
            PieceTableMembershipWasApplied ||
            PieceTableSortWasApplied;
        if (!shouldTouchPieceTables)
        {
            return;
        }

        HashSet<PieceTable> affectedTables = GetAffectedPieceTables(
            pieceTableAssignments,
            sortOrders,
            removedPieces,
            includePreviouslyTouchedTables: PieceTableMembershipWasApplied || PieceTableSortWasApplied);
        if (affectedTables.Count == 0)
        {
            return;
        }

        CapturePieceTableOrderBaselinesIfNeeded(affectedTables);
        RestorePieceTableMemberships(affectedTables);

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
            return;
        }

        foreach (PieceTable pieceTable in affectedTables)
        {
            ApplyPieceTableSortOrder(pieceTable, sortOrders);
        }

        RefreshLocalBuildPieces();
    }

    private static HashSet<PieceTable> GetAffectedPieceTables(
        IReadOnlyDictionary<string, PieceTableAssignment> pieceTableAssignments,
        IReadOnlyDictionary<string, int> sortOrders,
        IReadOnlyCollection<string> removedPieces,
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
            return NullIfIgnoredCategory(FormatPieceCategory(piece));
        }

        string fallbackValue = fallback?.Trim() ?? "";
        if (fallbackValue.Length > 0 &&
            TryResolvePieceCategory(fallbackValue, out Piece.PieceCategory category))
        {
            return NullIfIgnoredCategory(FormatPieceCategory(category, null, fallbackValue));
        }

        return NullIfIgnoredCategory(fallback);
    }

    private static string? NullIfIgnoredCategory(string? categoryName)
    {
        return IsIgnoredCategoryName(categoryName) ? null : categoryName;
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

        if (TryGetCategoryDisplayName(category, piecePrefab, out string displayName) &&
            !IsNumericCategoryName(displayName))
        {
            return displayName;
        }

        if (TryGetJotunnPieceCategoryDisplayName(category, out displayName))
        {
            return displayName;
        }

        return fallback;
    }

    private static bool TryResolvePieceCategory(string categoryName, out Piece.PieceCategory category)
    {
        if (Enum.TryParse(categoryName, ignoreCase: true, out category))
        {
            return true;
        }

        if (TryResolveJotunnPieceCategory(categoryName, out category))
        {
            return true;
        }

        string normalized = categoryName.Trim();
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

    private static bool TryGetJotunnPieceCategoryDisplayName(Piece.PieceCategory category, out string displayName)
    {
        EnsureJotunnPieceCategoryMap();
        if (JotunnPieceCategoryNames != null &&
            JotunnPieceCategoryNames.TryGetValue(category, out string name) &&
            !string.IsNullOrWhiteSpace(name) &&
            !IsNumericCategoryName(name))
        {
            displayName = name;
            return true;
        }

        displayName = "";
        return false;
    }

    private static bool TryResolveJotunnPieceCategory(string categoryName, out Piece.PieceCategory category)
    {
        EnsureJotunnPieceCategoryMap();
        string normalized = categoryName.Trim();
        if (JotunnPieceCategoryValues != null &&
            JotunnPieceCategoryValues.TryGetValue(normalized, out category))
        {
            return true;
        }

        category = Piece.PieceCategory.Misc;
        return false;
    }

    private static void EnsureJotunnPieceCategoryMap()
    {
        if (JotunnPieceCategoryMapLoaded)
        {
            return;
        }

        JotunnPieceCategoryMapLoaded = true;
        JotunnPieceCategoryNames = new Dictionary<Piece.PieceCategory, string>();
        JotunnPieceCategoryValues = new Dictionary<string, Piece.PieceCategory>(StringComparer.OrdinalIgnoreCase);

        Type? pieceManagerType = FindLoadedType("Jotunn.Managers.PieceManager");
        object? pieceManager = pieceManagerType
            ?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetValue(null);
        object? categoryMap = pieceManagerType
            ?.GetMethod("GetPieceCategoriesMap", Type.EmptyTypes)
            ?.Invoke(pieceManager, null);
        if (categoryMap is not System.Collections.IEnumerable entries)
        {
            return;
        }

        foreach (object entry in entries)
        {
            Type entryType = entry.GetType();
            object? key = entryType.GetProperty("Key")?.GetValue(entry);
            object? value = entryType.GetProperty("Value")?.GetValue(entry);
            if (key is not Piece.PieceCategory category ||
                value is not string name ||
                string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            JotunnPieceCategoryNames[category] = name;
            JotunnPieceCategoryValues[name] = category;
        }
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
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

        if (input.Equals(trimmedLabel, StringComparison.OrdinalIgnoreCase) ||
            input.Equals(trimmedLabel.TrimStart('$'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryFormatCategoryLabel(trimmedLabel, out string displayName) &&
               input.Equals(displayName, StringComparison.OrdinalIgnoreCase);
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

    private static string? GetReferencePieceTableName(string prefabName)
    {
        List<string> tableNames = BuildPieceTableMembershipSnapshot().GetTableNames(prefabName);
        return tableNames.FirstOrDefault(name =>
            !IsDefaultReferencePieceTableName(name) &&
            !IsIgnoredPieceTableName(name));
    }

    private static string? GetFullScaffoldPieceTableName(string prefabName)
    {
        return GetFullScaffoldPieceTableName(prefabName, BuildPieceTableMembershipSnapshot());
    }

    private static bool ShouldGeneratePieceEntry(string prefabName)
    {
        return ShouldGeneratePieceEntry(prefabName, BuildPieceTableMembershipSnapshot());
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

    private static bool IsOnlyInIgnoredPieceTables(string prefabName)
    {
        return BuildPieceTableMembershipSnapshot().IsOnlyInIgnoredTables(prefabName);
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

    private static bool IsDefaultReferencePieceTableName(string pieceTableName)
    {
        return NormalizePieceTableIdentifier(pieceTableName).Equals("_HammerPieceTable", StringComparison.OrdinalIgnoreCase);
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

    private static void ReapplyRecipesIfCraftingStationTopologyChanged()
    {
        if (!CraftingStationTopologyChanged)
        {
            return;
        }

        CraftingStationTopologyChanged = false;
        RecipeOverrideManager.ApplyCurrentConfiguration();
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
                var fullEntries = Baselines
                    .Where(pair => ShouldGeneratePieceEntry(pair.Key, pieceTableMembership))
                    .Select(pair => new
                    {
                        Entry = PieceEntry.FromDefinition(pair.Key, pair.Value, GetFullScaffoldPieceTableName(pair.Key, pieceTableMembership)),
                        OwnerKey = pair.Key,
                        SortKey = DataForgeResourceMap.BuildSortKey(
                            GetPieceGroupSortRank(pair.Value),
                            DataForgeResourceMap.GetResourceTierSortValue(
                                pair.Value.Piece?.Resources?.SelectMany(resource => resource.Keys) ?? Array.Empty<string>()),
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
            $"{DomainName}.yml",
            BuildReferenceArtifactContent);
        if (wrote || File.Exists(referencePath))
        {
            RecordReferenceUpdateState(referencePath, sourceSignature);
        }
    }

    private static string BuildReferenceArtifactContent()
    {
        PieceTableMembershipSnapshot pieceTableMembership = BuildPieceTableMembershipSnapshot();
        var referenceEntries = Baselines
            .Where(pair => ShouldGeneratePieceEntry(pair.Key, pieceTableMembership))
            .Select(pair => new
            {
                Entry = PieceReferenceEntry.From(pair.Key, pair.Value),
                SortKey = DataForgeResourceMap.BuildSortKey(
                    GetPieceGroupSortRank(pair.Value),
                    DataForgeResourceMap.GetResourceTierSortValue(
                        pair.Value.Piece?.Resources?.SelectMany(resource => resource.Keys) ?? Array.Empty<string>()),
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
               ZNetScene.instance != null &&
               ObjectDB.instance != null;
    }

    private static string ComputeReferenceSourceSignature()
    {
        StringBuilder builder = new();
        builder.AppendLine(ReferenceLogicVersion);
        builder.AppendLine(BuildFileStamp(Path.Combine(ConfigDirectory, "z_resourcemap.txt")));
        foreach ((string prefabName, _) in GetPrefabPieces()
                     .OrderBy(pair => pair.PrefabName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(prefabName);
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
                Fermenter = definition.Fermenter != null ? ReferenceValue.ClonePruned(definition.Fermenter) : null,
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
                Visual = definition.Visual != null ? ReferenceValue.ClonePruned(definition.Visual) : null
            };
            return ReferenceValue.ClonePruned(entry) ?? new PieceReferenceEntry { Piece = prefab };
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

        internal static PieceComponentDefinition From(Piece piece)
        {
            WearNTear wearNTear = piece.GetComponent<WearNTear>();
            return new PieceComponentDefinition
            {
                Name = piece.m_name,
                Description = piece.m_description,
                Category = FormatPieceCategory(piece),
                NeedStation = FormatCraftingStation(piece.m_craftingStation),
                CanBeRemoved = piece.m_canBeRemoved,
                Health = wearNTear != null ? wearNTear.m_health : null,
                Comfort = FormatTuple(piece.m_comfort, piece.m_comfortGroup),
                Resources = PieceResourceDefinition.From(piece.m_resources)
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
        public List<FermenterConversionDefinition>? Conversions { get; set; }

        internal static FermenterDefinition? From(Fermenter? fermenter)
        {
            return fermenter == null
                ? null
                : new FermenterDefinition
                {
                    Duration = fermenter.m_fermentationDuration,
                    Conversions = FermenterConversionDefinition.From(fermenter.m_conversion)
                };
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

    private static float FormatUniformScale(Vector3 scale)
    {
        return Math.Abs(scale.x - scale.y) <= 0.0001f && Math.Abs(scale.x - scale.z) <= 0.0001f
            ? scale.x
            : Math.Max(scale.x, Math.Max(scale.y, scale.z));
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

        internal static StationExtensionSnapshot From(StationExtension extension)
        {
            return new StationExtensionSnapshot
            {
                CraftingStation = extension.m_craftingStation,
                MaxStationDistance = extension.m_maxStationDistance,
                Stack = extension.m_stack,
                ConnectionPrefab = extension.m_connectionPrefab,
                ConnectionOffset = extension.m_connectionOffset,
                ContinuousConnection = extension.m_continousConnection,
                Piece = extension.m_piece
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

    private sealed class RendererMaterialSnapshot
    {
        internal RendererMaterialSnapshot(Renderer renderer, Material[] materials)
        {
            Renderer = renderer;
            Materials = materials;
        }

        internal Renderer Renderer { get; }
        internal Material[] Materials { get; }
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
