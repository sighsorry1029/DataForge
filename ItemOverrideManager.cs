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
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static DataForge.DataForgeValue;

namespace DataForge;

internal static class ItemOverrideManager
{
    private const string DomainName = "items";
    private const string OverrideFileName = "items.yml";
    private const string ReferenceFileName = "items.reference.yml";
    private const string FullScaffoldFileName = "items.full.yml";
    private const string SyncedPayloadKey = "items";
    private const string CloneRootName = "DataForge_ItemClones";
    private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;
    private const string ReferenceStateKey = "items";
    private const string ReferenceLogicVersion = "2026-06-24-item-reference-state-v2";

    private static readonly object StateLock = new();
    private static readonly Dictionary<string, ItemDefinition> Baselines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly MethodInfo? MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? SetupEquipmentMethod =
        AccessTools.Method(typeof(Humanoid), "SetupEquipment");
    private static GameObject? CloneRoot;
    private static readonly HashSet<string> ReferenceVisiblePrefabs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CreatedClones = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, GameObject> CreatedClonePrefabs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RuntimeAppliedItemKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LiveSafeAppliedItemKeys = new(StringComparer.OrdinalIgnoreCase);
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

    private static List<ItemEntry> ActiveEntries = new();
    private static Dictionary<string, float> ActiveAmountMultipliers = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> ActiveEntrySignaturesByItem = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string>? PendingChangedItemKeys;
    private static bool HasPendingScopedApply;
    private static bool ForceNextFullApply = true;
    private static CustomSyncedValue<string>? SyncedPayload;
    private static string? LastAppliedSyncedPayload;
    private static FileSystemWatcher? Watcher;
    private static DataForgeFileWatcher.DebouncedAction? ReloadDebouncer;
    private static bool ObjectDbReady;
    private static bool ZNetSceneReady;
    private static bool RuntimeStateWasApplied;
    private static bool GlobalMultiplierStateWasApplied;
    private static bool LiveSafeStateWasApplied;

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
        Watcher = DataForgeFileWatcher.Create(ConfigDirectory, "*.*", includeSubdirectories: true, ReadYamlValues);
    }

    internal static void ReloadFromDiskAndSync()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles)
        {
            ApplySyncedPayload(SyncedPayload?.Value ?? "");
            return;
        }

        EnsureConfigDirectoryAndDefaultOverride();
        List<ItemEntry> entries = LoadEntriesFromDisk();
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
        if (!ObjectDbReady ||
            !DataForgeWorldLifecycle.IsGameStarted ||
            ObjectDB.instance == null)
        {
            return;
        }

        List<ItemEntry> entries;
        HashSet<string>? changedItemKeys;
        lock (StateLock)
        {
            entries = ActiveEntries.ToList();
            changedItemKeys = ConsumePendingChangedItemKeys();
        }

        if (changedItemKeys is { Count: 0 })
        {
            return;
        }

        CleanupCreatedPrefabs(entries, destroy: true);
        EnsureClonePrefabs(entries, warnIfMissingSource: true);
        Dictionary<string, List<ItemEntry>> entriesByItem = BuildEnabledEntriesByItem(entries);
        bool shouldApplyAllItems = HasGlobalItemMultiplierOverrides() || GlobalMultiplierStateWasApplied;
        HashSet<string>? applyItemKeys = shouldApplyAllItems
            ? null
            : GetRuntimeApplyKeys(entriesByItem, changedItemKeys);
        if (shouldApplyAllItems)
        {
            CaptureAllBaselinesIfNeeded();
        }
        else
        {
            CaptureBaselinesForItemsIfNeeded(applyItemKeys);
        }

        ApplyToItemPrefabs(entriesByItem, applyItemKeys);
        RepairDropPrefabs(applyItemKeys);
        bool shouldRefreshExistingItems = ShouldRefreshExistingItems(entriesByItem, shouldApplyAllItems);
        if (shouldRefreshExistingItems)
        {
            ApplyLiveSafeToExistingItems(entriesByItem, applyItemKeys);
        }

        ObjectDB.instance.UpdateRegisters();
        UpdateRuntimeAppliedItemState(entriesByItem, HasGlobalItemMultiplierOverrides());
        UpdateLiveSafeItemState(entriesByItem, shouldRefreshExistingItems);
        VneiRefreshManager.RequestRefresh(DomainName);
    }

    internal static void ApplyCurrentConfiguration()
    {
        if (!ObjectDbReady ||
            !ZNetSceneReady ||
            !DataForgeWorldLifecycle.IsGameStarted ||
            ObjectDB.instance == null ||
            ZNetScene.instance == null)
        {
            return;
        }

        if (ShouldSkipRemoteClientBaselineWork())
        {
            return;
        }

        List<ItemEntry> entries;
        HashSet<string>? changedItemKeys;
        lock (StateLock)
        {
            entries = ActiveEntries.ToList();
            changedItemKeys = ConsumePendingChangedItemKeys();
        }

        if (changedItemKeys is { Count: 0 })
        {
            return;
        }

        CleanupCreatedPrefabs(entries, destroy: true);
        EnsureClonePrefabs(entries, warnIfMissingSource: ZNetSceneReady);
        Dictionary<string, List<ItemEntry>> entriesByItem = BuildEnabledEntriesByItem(entries);
        bool shouldApplyAllItems = HasGlobalItemMultiplierOverrides() || GlobalMultiplierStateWasApplied;
        HashSet<string>? applyItemKeys = shouldApplyAllItems
            ? null
            : GetRuntimeApplyKeys(entriesByItem, changedItemKeys);
        if (shouldApplyAllItems)
        {
            CaptureAllBaselinesIfNeeded();
        }
        else
        {
            CaptureBaselinesForItemsIfNeeded(applyItemKeys);
        }

        ApplyToItemPrefabs(entriesByItem, applyItemKeys);
        RepairDropPrefabs(applyItemKeys);
        bool shouldRefreshExistingItems = ShouldRefreshExistingItems(entriesByItem, shouldApplyAllItems);
        if (shouldRefreshExistingItems)
        {
            ApplyLiveSafeToExistingItems(entriesByItem, applyItemKeys);
        }

        ObjectDB.instance.UpdateRegisters();
        UpdateRuntimeAppliedItemState(entriesByItem, HasGlobalItemMultiplierOverrides());
        UpdateLiveSafeItemState(entriesByItem, shouldRefreshExistingItems);
        VneiRefreshManager.RequestRefresh(DomainName);
    }

    private static bool ShouldSkipRemoteClientBaselineWork()
    {
        if (!DataForgePlugin.IsRemoteServerClient ||
            DataForgePlugin.StackableStackMultiplier != 1 ||
            Math.Abs(DataForgePlugin.ItemWeightMultiplier - 1f) > 0.0001f)
        {
            return false;
        }

        lock (StateLock)
        {
            return ActiveEntries.Count == 0 && CreatedClones.Count == 0 && CreatedClonePrefabs.Count == 0;
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
            DataForgePlugin.Log.LogDebug("Reloading item YAML files...");
            ReloadFromDiskAndSync();
            DataForgePlugin.Log.LogInfo("Item YAML reload complete.");
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Error reloading item YAML files: {ex}");
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

        if (ItemVisualOverrides.IsIconFile(e.FullPath))
        {
            return true;
        }

        return e is RenamedEventArgs renamed &&
               (IsOverrideFile(renamed.OldFullPath) || ItemVisualOverrides.IsIconFile(renamed.OldFullPath));
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
        List<ItemEntry> entries = DeserializeEntries(payload, "synced item payload");
        lock (StateLock)
        {
            SetActiveEntries(entries);
        }

        ApplyCurrentConfiguration();
    }

    internal static void CleanupCreatedClonesForWorldTransition()
    {
        CleanupCreatedPrefabs(new List<ItemEntry>(), destroy: true);
        CreatedClones.Clear();
        CreatedClonePrefabs.Clear();
    }

    internal static void OnWorldShutdown()
    {
        ObjectDbReady = false;
        ZNetSceneReady = false;
        RuntimeStateWasApplied = false;
        GlobalMultiplierStateWasApplied = false;
        LiveSafeStateWasApplied = false;
        RuntimeAppliedItemKeys.Clear();
        LiveSafeAppliedItemKeys.Clear();
        CleanupCreatedClonesForWorldTransition();
    }

    private static void PublishPayload(string payload)
    {
        DataForgeSync.PublishPayload(SyncedPayload, DomainName, payload);
    }

    private static List<ItemEntry> LoadEntriesFromDisk()
    {
        return DataForgeOverrideFiles.LoadEntries(GetOverrideFiles(), DeserializeEntries);
    }

    private static List<ItemEntry> DeserializeEntries(string yaml, string source)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new List<ItemEntry>();
        }

        try
        {
            List<ItemEntry>? entries = Deserializer.Deserialize<List<ItemEntry>>(yaml);
            return NormalizeEntries(entries, source);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Failed to parse {source}: {ex.Message}");
            return new List<ItemEntry>();
        }
    }

    private static List<ItemEntry> NormalizeEntries(List<ItemEntry>? entries, string source)
    {
        List<ItemEntry> normalized = new();
        if (entries == null)
        {
            return normalized;
        }

        int entryIndex = 0;
        foreach (ItemEntry entry in entries)
        {
            entryIndex++;
            string sourceContext = DataForgeLogContext.FormatSource(source, entryIndex);
            if (string.IsNullOrWhiteSpace(entry.Item))
            {
                DataForgeLogContext.Warning($"{sourceContext}: Skipping item entry without item.");
                continue;
            }

            string[] itemParts = SplitTuple(entry.Item);
            if (itemParts.Length == 0 || string.IsNullOrWhiteSpace(itemParts[0]))
            {
                DataForgeLogContext.Warning($"{sourceContext}: Skipping item entry without item.");
                continue;
            }

            entry.Item = NormalizePrefabName(itemParts[0]);
            if (itemParts.Length > 1)
            {
                if (float.TryParse(itemParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float amountMultiplier))
                {
                    entry.AmountMultiplier = Math.Max(0f, amountMultiplier);
                }
                else
                {
                    DataForgeLogContext.Warning($"{sourceContext}: Ignoring invalid item amount multiplier '{itemParts[1]}' for '{entry.Item}'.");
                }
            }

            if (itemParts.Length > 2)
            {
                DataForgeLogContext.Warning($"{sourceContext}: Ignoring extra item tuple values after '{entry.Item}, {itemParts[1]}'.");
            }

            string amountContext = entry.AmountMultiplier.HasValue
                ? $" amountMultiplier={FormatFloat(entry.AmountMultiplier)}"
                : "";
            entry.SetLogContext($"{sourceContext} item={entry.Item}{amountContext}");
            string? cloneFrom = entry.CloneFrom;
            if (cloneFrom != null && cloneFrom.Trim().Length > 0)
            {
                entry.CloneFrom = NormalizePrefabName(cloneFrom);
            }

            normalized.Add(entry);
        }

        return normalized;
    }

    private static void SetActiveEntries(List<ItemEntry> entries)
    {
        Dictionary<string, string> signatures = BuildEntrySignaturesByItem(entries);
        if (!ForceNextFullApply)
        {
            PendingChangedItemKeys = GetChangedKeys(ActiveEntrySignaturesByItem, signatures);
            HasPendingScopedApply = true;
        }

        ActiveEntries = entries;
        ActiveAmountMultipliers = BuildAmountMultiplierMap(entries);
        ActiveEntrySignaturesByItem = signatures;
    }

    private static HashSet<string>? ConsumePendingChangedItemKeys()
    {
        if (ForceNextFullApply)
        {
            ForceNextFullApply = false;
            PendingChangedItemKeys = null;
            HasPendingScopedApply = false;
            return null;
        }

        if (!HasPendingScopedApply)
        {
            return null;
        }

        HashSet<string> changedKeys = PendingChangedItemKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PendingChangedItemKeys = null;
        HasPendingScopedApply = false;
        return changedKeys;
    }

    private static Dictionary<string, string> BuildEntrySignaturesByItem(List<ItemEntry> entries)
    {
        Dictionary<string, List<ItemEntry>> entriesByItem = new(StringComparer.OrdinalIgnoreCase);
        foreach (ItemEntry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Item))
            {
                continue;
            }

            if (!entriesByItem.TryGetValue(entry.Item, out List<ItemEntry> itemEntries))
            {
                itemEntries = new List<ItemEntry>();
                entriesByItem[entry.Item] = itemEntries;
            }

            itemEntries.Add(entry);
        }

        Dictionary<string, string> signatures = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<ItemEntry>> pair in entriesByItem)
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

    private static Dictionary<string, float> BuildAmountMultiplierMap(List<ItemEntry> entries)
    {
        Dictionary<string, float> multipliers = new(StringComparer.OrdinalIgnoreCase);
        foreach (ItemEntry entry in entries)
        {
            if (!entry.Override || !entry.AmountMultiplier.HasValue)
            {
                continue;
            }

            float multiplier = Math.Max(0f, entry.AmountMultiplier.Value);
            if (IsDefaultAmountMultiplier(multiplier))
            {
                multipliers.Remove(entry.Item);
            }
            else
            {
                multipliers[entry.Item] = multiplier;
            }
        }

        return multipliers;
    }

    private static Dictionary<string, List<ItemEntry>> BuildEnabledEntriesByItem(List<ItemEntry> entries)
    {
        Dictionary<string, List<ItemEntry>> entriesByItem = new(StringComparer.OrdinalIgnoreCase);
        foreach (ItemEntry entry in entries)
        {
            if (!entry.Override || string.IsNullOrWhiteSpace(entry.Item) || !entry.HasPrefabDefinition)
            {
                continue;
            }

            if (!entriesByItem.TryGetValue(entry.Item, out List<ItemEntry> itemEntries))
            {
                itemEntries = new List<ItemEntry>();
                entriesByItem[entry.Item] = itemEntries;
            }

            itemEntries.Add(entry);
        }

        return entriesByItem;
    }

    private static bool HasGlobalItemMultiplierOverrides()
    {
        return DataForgePlugin.StackableStackMultiplier != 1 ||
               Math.Abs(DataForgePlugin.ItemWeightMultiplier - 1f) > 0.0001f;
    }

    private static HashSet<string> GetRuntimeApplyKeys(
        Dictionary<string, List<ItemEntry>> entriesByItem,
        HashSet<string>? changedItemKeys)
    {
        if (changedItemKeys != null)
        {
            return new HashSet<string>(changedItemKeys, StringComparer.OrdinalIgnoreCase);
        }

        HashSet<string> keys = new(entriesByItem.Keys, StringComparer.OrdinalIgnoreCase);
        if (RuntimeStateWasApplied)
        {
            foreach (string key in RuntimeAppliedItemKeys)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static void UpdateRuntimeAppliedItemState(
        Dictionary<string, List<ItemEntry>> entriesByItem,
        bool globalMultiplierActive)
    {
        RuntimeAppliedItemKeys.Clear();
        foreach (string key in entriesByItem.Keys)
        {
            RuntimeAppliedItemKeys.Add(key);
        }

        RuntimeStateWasApplied = RuntimeAppliedItemKeys.Count > 0;
        GlobalMultiplierStateWasApplied = globalMultiplierActive;
    }

    private static bool ShouldRefreshExistingItems(
        Dictionary<string, List<ItemEntry>> entriesByItem,
        bool refreshAllForGlobalMultiplier)
    {
        if (refreshAllForGlobalMultiplier || LiveSafeStateWasApplied)
        {
            return true;
        }

        return entriesByItem.Values
            .SelectMany(entries => entries)
            .Any(entry => entry.HasLiveSafeDefinition);
    }

    private static void UpdateLiveSafeItemState(
        Dictionary<string, List<ItemEntry>> entriesByItem,
        bool refreshWasRun)
    {
        LiveSafeAppliedItemKeys.Clear();
        foreach (KeyValuePair<string, List<ItemEntry>> pair in entriesByItem)
        {
            if (pair.Value.Any(entry => entry.HasLiveSafeDefinition))
            {
                LiveSafeAppliedItemKeys.Add(pair.Key);
            }
        }

        LiveSafeStateWasApplied = refreshWasRun && LiveSafeAppliedItemKeys.Count > 0;
    }

    private static string SerializeEntries(List<ItemEntry> entries)
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
               fileName.StartsWith("items_", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureConfigDirectoryAndDefaultOverride()
    {
        ItemVisualOverrides.EnsureIconDirectory();
        DataForgeOverrideFiles.EnsureDefaultOverride(ConfigDirectory, OverrideFileName, GetOverrideFiles, DefaultOverrideTemplate);
    }

    private static string DefaultOverrideTemplate()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# DataForge item overrides.",
            "# Copy entries from items.reference.yml, or run `dataforge:full item` to generate items.full.yml for exhaustive field examples.",
            "# You can also create additional override files like items_asdf.yml; DataForge loads items.yml and items_*.yml together.",
            "# Omitted fields keep the current item value. Values below are common defaults or examples.",
            "#",
            "# Schema:",
            "# - item: SwordIron, 1                    # required item prefab id; optional second value multiplies world loot/gather/drop amount.",
            "#   override: true                        # default true; false skips this entire item entry.",
            "#   cloneFrom: SwordBronze                 # optional; source item prefab used to clone a new item.",
            "#   name: $item_sword_iron                 # display name token or literal text.",
            "#   description: $item_sword_iron_desc     # tooltip description token or literal text.",
            "#   subtitle:                              # optional secondary text.",
            "#   itemType: OneHandedWeapon              # ItemDrop.ItemData.ItemType enum name.",
            "#   weight: 1                              # item weight.",
            "#   value: 0                               # item value shown to game systems.",
            "#   maxStackSize: 1                        # maximum stack size.",
            "#   maxQuality: 1                          # maximum item quality level.",
            "#   teleportable: true                     # false prevents portal travel.",
            "#   floating: true                         # false removes the Floating component so dropped items sink in water.",
            "#   durability: 100, 50, true, 1, true, false, 0 # maxDurability, durabilityPerLevel, useDurability, useDurabilityDrain, canBeRepaired, destroyBroken, durabilityDrain.",
            "#   equipment:",
            "#     skillType: Swords                    # shown only for sword/axe/club/knife/spear/polearm/fists/shield/pickaxe/bow/crossbow/elemental magic/blood magic items.",
            "#     equipDuration: 0                     # seconds to equip.",
            "#     movementModifier: 0                  # movement modifier while equipped.",
            "#     eitrRegenModifier: 0                 # equipped eitr regen modifier.",
            "#     heatResistanceModifier: 0            # heat resistance modifier.",
            "#     homeItemsStaminaModifier: 0          # build/home item stamina use modifier.",
            "#     attackStaminaModifier: 0             # attack stamina use modifier.",
            "#     blockStaminaModifier: 0              # block stamina use modifier.",
            "#     dodgeStaminaModifier: 0              # dodge stamina use modifier.",
            "#     jumpStaminaModifier: 0               # jump stamina use modifier.",
            "#     runStaminaModifier: 0                # run stamina use modifier.",
            "#     sneakStaminaModifier: 0              # sneak stamina use modifier.",
            "#     swimStaminaModifier: 0               # swim stamina use modifier.",
            "#     armor: 12, 2                         # shown only for Helmet/Chest/Legs/Shoulder/Utility; base armor, armor gained per quality level.",
            "#     maxAdrenaline: 0                     # max adrenaline bonus.",
            "#   damageTakenModifiers:",
            "#     fire: Resistant                      # item damage taken modifier; values include Normal, Resistant, VeryResistant, Weak, VeryWeak, Immune, Ignore, SlightlyResistant.",
            "#     frost: Weak                          # set Normal to remove an existing modifier for that damage type.",
            "#   food: 45, 15, 0, 2, 1800             # health, stamina, eitr, regen, burnTime.",
            "#   shield:",
            "#     shown in reference/full scaffold only for sword/axe/club/knife/spear/polearm/fists/shield/pickaxe/bow/crossbow/elemental magic/blood magic items.",
            "#     blockPower: 40, 5                    # base block power, block power gained per quality level.",
            "#     deflectionForce: 30, 5               # base deflection force, deflection gained per quality level.",
            "#     timedBlockBonus: 1.5                 # parry/timed block multiplier.",
            "#   damage:",
            "#     blunt: 0, 0                          # blunt base damage, damage gained per quality level.",
            "#     slash: 0, 0                          # slash base damage, damage gained per quality level.",
            "#     pierce: 0, 0                         # pierce base damage, damage gained per quality level.",
            "#     chop: 0, 0                           # chop/tree base damage, damage gained per quality level.",
            "#     pickaxe: 0, 0                        # pickaxe base damage, damage gained per quality level.",
            "#     fire: 0, 0                           # fire base damage, damage gained per quality level.",
            "#     frost: 0, 0                          # frost base damage, damage gained per quality level.",
            "#     lightning: 0, 0                      # lightning base damage, damage gained per quality level.",
            "#     poison: 0, 0                         # poison base damage, damage gained per quality level.",
            "#     spirit: 0, 0                         # spirit base damage, damage gained per quality level.",
            "#     backstabBonus: 1                     # backstab damage multiplier.",
            "#     attackForce: 0                       # knockback force.",
            "#   primaryAttack:",
            "#     # shown in reference/full scaffold only for InventorySlots-style sword/axe/club/knife/spear/polearm/fists/shield/pickaxe/tool/bow/crossbow/elemental magic/blood magic items.",
            "#     cost: 0, 0, 0, 0                     # stamina, eitr, health, healthPercentage costs.",
            "#     missingHealth: 0, 0, 0               # damage multiplier per missing HP, damage multiplier by total missing health percent, stamina returned per missing HP.",
            "#     spawnOnTrigger: None                 # prefab spawned when the attack is triggered; None clears it.",
            "#     spawnOnHit: None, 0                  # prefab spawned on hit, chance from 0 to 1; ChainLightning, 0.2 => 20% chance.",
            "#     projectile: None, 0, 0, 1, 0, 0      # projectile prefab, max velocity, min velocity, count, accuracy, min accuracy; None clears it.",
            "#     draw: 0, 0, 0                        # full draw time at skill 0, stamina drain/s while drawing, eitr drain/s while drawing.",
            "#     reload: false, 0, 0, 0               # requires reload, reload time seconds, stamina drain while reloading, eitr drain while reloading.",
            "#     damageMultiplier: 1                  # attack damage multiplier.",
            "#     forceMultiplier: 1                   # attack force multiplier.",
            "#     staggerMultiplier: 1                 # stagger multiplier.",
            "#     lastChainDamageMultiplier: 1         # final combo hit damage multiplier; scaffold shows it when chainLevels > 1; reference omits vanilla default 2.",
            "#     raiseSkillAmount: 1                  # skill experience raised by a successful attack.",
            "#   secondaryAttack:                       # same fields as primaryAttack, except lastChainDamageMultiplier.",
            "#     # shown in reference/full scaffold only when primaryAttack is eligible and secondary attack animation is not empty.",
            "#     cost: 0, 0, 0, 0                     # stamina, eitr, health, healthPercentage costs.",
            "#     missingHealth: 0, 0, 0               # same missing-health tuple for secondary attack.",
            "#     spawnOnTrigger: None                 # same trigger-spawn prefab for secondary attack.",
            "#     spawnOnHit: None, 0                  # same spawn-on-hit tuple for secondary attack.",
            "#     projectile: None, 0, 0, 1, 0, 0      # same projectile tuple for secondary attack.",
            "#   effects:",
            "#     equipStatusEffect: Rested            # status effect applied while equipped.",
            "#     set: WolfArmor, 4, SetEffectName     # setName, setSize, setStatusEffect applied when enough matching set items are equipped.",
            "#     consumeStatusEffect: MeadHealthMedium # status effect applied when consumed.",
            "#     attackStatusEffect: Burning, 0.25    # statusEffect, chance; 0.25 means 25% chance to apply on attack.",
            "#     perfectBlockStatusEffect:            # status effect applied on perfect block.",
            "#     fullAdrenalineStatusEffect:          # status effect applied at full adrenaline.",
            "#   visual:",
            "#     icon: auto                           # auto snapshots visually changed items; use MyIcon to load DataForge/icon/MyIcon.png; 256x256 PNG recommended.",
            "#     iconRotation: 23, 51, 25.8           # x, y, z Euler rotation used only by icon: auto.",
            "#     material: wood                       # material name from z_materials.reference.txt; replaces all item renderer material slots.",
            "#     color: 1, 0.8, 0.6, 1                # RGBA tint applied to cloned item material instances.",
            "#     emission: 0.5                        # emission intensity; 0 disables glow, higher values glow more if the shader supports it.",
            "#",
            "# Example:",
            "# - item: SwordIronHeavy",
            "#   cloneFrom: SwordIron",
            "#   weight: 0.8",
            "#   visual:",
            "#     icon: auto",
            "#     iconRotation: 23, 51, 25.8",
            "#     material: blackmetal",
            "#     color: 0.8, 0.85, 1, 1",
            "#     emission: 0.15",
            "#   damage:",
            "#     slash: 55, 0"
        }) + Environment.NewLine;
    }

    private static void CaptureAllBaselinesIfNeeded()
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        int added = 0;
        foreach ((string prefabName, ItemDrop itemDrop) in GetItemDrops())
        {
            if (CaptureBaseline(prefabName, itemDrop))
            {
                added++;
            }
        }

        if (added > 0)
        {
            DataForgePlugin.Log.LogInfo($"Captured {added} new item prefab baselines. Tracking {Baselines.Count} total.");
        }
    }

    private static void CaptureBaselinesForItemsIfNeeded(IEnumerable<string>? itemNames)
    {
        if (ObjectDB.instance == null || itemNames == null)
        {
            return;
        }

        int added = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string itemName in itemNames)
        {
            string normalizedName = NormalizePrefabName(itemName);
            if (normalizedName.Length == 0 || !seen.Add(normalizedName) || Baselines.ContainsKey(normalizedName))
            {
                continue;
            }

            GameObject? prefab = ResolveItemPrefab(normalizedName);
            if (prefab == null || !prefab.TryGetComponent(out ItemDrop itemDrop))
            {
                continue;
            }

            if (CaptureBaseline(GetPrefabName(prefab), itemDrop))
            {
                added++;
            }
        }

        if (added > 0)
        {
            DataForgePlugin.Log.LogInfo($"Captured {added} targeted item prefab baselines. Tracking {Baselines.Count} total.");
        }
    }

    private static bool CaptureBaseline(string prefabName, ItemDrop itemDrop)
    {
        TrackReferenceVisibility(prefabName, itemDrop);
        if (Baselines.ContainsKey(prefabName))
        {
            return false;
        }

        Baselines[prefabName] = ItemDefinition.From(itemDrop);
        return true;
    }

    private static IEnumerable<(string PrefabName, ItemDrop ItemDrop)> GetItemDrops()
    {
        if (ObjectDB.instance == null)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject prefab in ObjectDB.instance.m_items)
        {
            if (prefab == null)
            {
                continue;
            }

            ItemDrop itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                continue;
            }

            string prefabName = GetPrefabName(prefab);
            if (seen.Add(prefabName))
            {
                yield return (prefabName, itemDrop);
            }
        }
    }

    private static void EnsureClonePrefabs(List<ItemEntry> entries, bool warnIfMissingSource)
    {
        foreach (ItemEntry entry in entries)
        {
            if (!entry.Override || string.IsNullOrWhiteSpace(entry.CloneFrom))
            {
                continue;
            }

            using (DataForgeLogContext.Push(entry.LogContext))
            {
                EnsureClonePrefab(entry, warnIfMissingSource);
            }
        }
    }

    private static void CleanupCreatedPrefabs(List<ItemEntry> entries, bool destroy)
    {
        HashSet<string> activeCloneNames = new(
            entries
                .Where(entry => entry.Override && !string.IsNullOrWhiteSpace(entry.CloneFrom))
                .Select(entry => entry.Item),
            StringComparer.OrdinalIgnoreCase);

        foreach (string cloneName in CreatedClonePrefabs.Keys.ToList())
        {
            if (activeCloneNames.Contains(cloneName))
            {
                continue;
            }

            RemoveCreatedClonePrefab(cloneName, destroy);
        }
    }

    private static void EnsureClonePrefab(ItemEntry entry, bool warnIfMissingSource)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        GameObject? existingPrefab = ResolveItemPrefab(entry.Item);
        if (existingPrefab != null)
        {
            if (CreatedClones.Contains(entry.Item))
            {
                CreatedClonePrefabs[entry.Item] = existingPrefab;
                EnsureStoredClonePrefab(existingPrefab);
                string? cloneSourceName = entry.CloneFrom;
                GameObject? existingCloneSource = !string.IsNullOrWhiteSpace(cloneSourceName)
                    ? ResolveItemPrefab(cloneSourceName!)
                    : null;
                PrepareClonedItemData(existingPrefab, existingCloneSource, entry.Item, entry.Name);
            }

            RegisterObjectDbItemPrefab(existingPrefab);
            ItemDrop existingItemDrop = existingPrefab.GetComponent<ItemDrop>();
            TrackReferenceVisibility(entry.Item, existingItemDrop);
            if (!Baselines.ContainsKey(entry.Item))
            {
                Baselines[entry.Item] = ItemDefinition.From(existingItemDrop);
            }
            return;
        }

        CreatedClones.Remove(entry.Item);

        GameObject sourcePrefab = ObjectDB.instance.GetItemPrefab(entry.CloneFrom);
        if (sourcePrefab == null)
        {
            if (warnIfMissingSource)
            {
                DataForgeLogContext.Warning($"Could not clone item '{entry.Item}': source '{entry.CloneFrom}' was not found.");
            }
            return;
        }

        GameObject clone = InstantiateStoredClone(sourcePrefab);
        clone.name = entry.Item;
        PrepareClonedItemData(clone, sourcePrefab, entry.Item, entry.Name);

        RegisterObjectDbItemPrefab(clone, addToItems: true);
        ItemDrop itemDrop = clone.GetComponent<ItemDrop>();
        TrackReferenceVisibility(entry.Item, itemDrop);
        Baselines[entry.Item] = ItemDefinition.From(itemDrop);
        CreatedClones.Add(entry.Item);
        CreatedClonePrefabs[entry.Item] = clone;
        DataForgePlugin.Log.LogInfo($"Cloned item '{entry.CloneFrom}' as '{entry.Item}'.");
    }

    private static void RemoveCreatedClonePrefab(string cloneName, bool destroy)
    {
        if (!CreatedClonePrefabs.TryGetValue(cloneName, out GameObject clonePrefab) || clonePrefab == null)
        {
            CreatedClonePrefabs.Remove(cloneName);
            CreatedClones.Remove(cloneName);
            Baselines.Remove(cloneName);
            ReferenceVisiblePrefabs.Remove(cloneName);
            return;
        }

        UnregisterObjectDbItemPrefab(cloneName, clonePrefab);
        UnregisterZNetScenePrefab(cloneName, clonePrefab);
        ItemVisualOverrides.Restore(cloneName, clonePrefab.GetComponent<ItemDrop>());

        CreatedClonePrefabs.Remove(cloneName);
        CreatedClones.Remove(cloneName);
        Baselines.Remove(cloneName);
        ReferenceVisiblePrefabs.Remove(cloneName);

        if (destroy)
        {
            UnityEngine.Object.Destroy(clonePrefab);
        }
        else
        {
            clonePrefab.SetActive(false);
        }
    }

    private static GameObject InstantiateStoredClone(GameObject sourcePrefab)
    {
        GameObject root = GetCloneRoot();
        GameObject clone = UnityEngine.Object.Instantiate(sourcePrefab, root.transform, worldPositionStays: false);
        clone.SetActive(true);
        return clone;
    }

    private static void PrepareClonedItemData(
        GameObject clonePrefab,
        GameObject? sourcePrefab,
        string clonePrefabName,
        string? configuredName)
    {
        ItemDrop itemDrop = clonePrefab.GetComponent<ItemDrop>();
        if (itemDrop?.m_itemData?.m_shared == null)
        {
            return;
        }

        ItemDrop.ItemData.SharedData? sourceShared = sourcePrefab != null
            ? sourcePrefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared
            : null;
        ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
        if (sourceShared != null && ReferenceEquals(shared, sourceShared))
        {
            shared = CloneSharedData(shared);
            itemDrop.m_itemData.m_shared = shared;
        }

        EnsureIndependentSharedMembers(shared, sourceShared);
        if (string.IsNullOrWhiteSpace(configuredName) &&
            (sourceShared == null || string.Equals(shared.m_name, sourceShared.m_name, StringComparison.Ordinal)))
        {
            shared.m_name = clonePrefabName;
        }

        itemDrop.m_itemData.m_dropPrefab = clonePrefab;
    }

    private static ItemDrop.ItemData.SharedData CloneSharedData(ItemDrop.ItemData.SharedData source)
    {
        return ShallowClone(source) ?? source;
    }

    private static void EnsureIndependentSharedMembers(
        ItemDrop.ItemData.SharedData shared,
        ItemDrop.ItemData.SharedData? sourceShared)
    {
        if (shared.m_icons != null)
        {
            shared.m_icons = shared.m_icons.ToArray();
        }

        if (shared.m_attack != null &&
            (sourceShared == null || ReferenceEquals(shared.m_attack, sourceShared.m_attack)))
        {
            shared.m_attack = ShallowClone(shared.m_attack) ?? shared.m_attack;
        }

        if (shared.m_secondaryAttack != null &&
            (sourceShared == null || ReferenceEquals(shared.m_secondaryAttack, sourceShared.m_secondaryAttack)))
        {
            shared.m_secondaryAttack = ShallowClone(shared.m_secondaryAttack) ?? shared.m_secondaryAttack;
        }

        if (shared.m_damageModifiers != null &&
            (sourceShared == null || ReferenceEquals(shared.m_damageModifiers, sourceShared.m_damageModifiers)))
        {
            shared.m_damageModifiers = shared.m_damageModifiers.ToList();
        }
    }

    private static T? ShallowClone<T>(T source) where T : class
    {
        if (MemberwiseCloneMethod == null)
        {
            return null;
        }

        try
        {
            return MemberwiseCloneMethod.Invoke(source, null) as T;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not clone {typeof(T).Name}: {ex.Message}");
            return null;
        }
    }

    private static void EnsureStoredClonePrefab(GameObject prefab)
    {
        GameObject root = GetCloneRoot();
        if (prefab.transform.parent != root.transform)
        {
            prefab.transform.SetParent(root.transform, worldPositionStays: false);
        }

        prefab.SetActive(true);
    }

    private static GameObject GetCloneRoot()
    {
        if (CloneRoot != null)
        {
            CloneRoot.SetActive(false);
            return CloneRoot;
        }

        CloneRoot = GameObject.Find(CloneRootName);
        if (CloneRoot == null)
        {
            CloneRoot = new GameObject(CloneRootName);
            UnityEngine.Object.DontDestroyOnLoad(CloneRoot);
        }

        CloneRoot.SetActive(false);
        return CloneRoot;
    }

    private static GameObject? ResolveItemPrefab(string prefabName)
    {
        GameObject? prefab = ObjectDB.instance.GetItemPrefab(prefabName);
        if (prefab != null)
        {
            return prefab;
        }

        return ObjectDB.instance.m_items.FirstOrDefault(item =>
            item != null &&
            string.Equals(NormalizePrefabName(item.name), prefabName, StringComparison.OrdinalIgnoreCase));
    }

    private static GameObject? ResolvePrefab(string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        string normalizedName = NormalizePrefabName(prefabName.Trim());
        if (ZNetScene.instance != null)
        {
            GameObject scenePrefab = ZNetScene.instance.GetPrefab(normalizedName);
            if (scenePrefab != null)
            {
                return scenePrefab;
            }

            int hash = normalizedName.GetStableHashCode();
            if (ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out GameObject namedPrefab) && namedPrefab != null)
            {
                return namedPrefab;
            }
        }

        if (ObjectDB.instance != null)
        {
            GameObject? itemPrefab = ResolveItemPrefab(normalizedName);
            if (itemPrefab != null)
            {
                return itemPrefab;
            }
        }

        return null;
    }

    internal static void RepairDropPrefab(ItemDrop itemDrop)
    {
        if (itemDrop == null || itemDrop.m_itemData == null || itemDrop.m_itemData.m_dropPrefab != null)
        {
            return;
        }

        GameObject? prefab = ResolveItemPrefab(NormalizePrefabName(itemDrop.gameObject.name)) ??
                             ResolveItemPrefab(itemDrop.m_itemData);
        if (prefab != null)
        {
            itemDrop.m_itemData.m_dropPrefab = prefab;
        }
    }

    internal static bool IsCreatedCloneDrop(ItemDrop itemDrop)
    {
        if (itemDrop == null || itemDrop.gameObject == null)
        {
            return false;
        }

        return CreatedClones.Contains(NormalizePrefabName(itemDrop.gameObject.name));
    }

    internal static void RepairDropPrefab(ItemDrop.ItemData item)
    {
        if (item == null || item.m_dropPrefab != null)
        {
            return;
        }

        GameObject? prefab = ResolveItemPrefab(item);
        if (prefab != null)
        {
            item.m_dropPrefab = prefab;
        }
    }

    private static GameObject? ResolveItemPrefab(ItemDrop.ItemData item)
    {
        if (ObjectDB.instance == null || item == null || item.m_shared == null)
        {
            return null;
        }

        foreach (GameObject prefab in ObjectDB.instance.m_items)
        {
            if (prefab == null)
            {
                continue;
            }

            ItemDrop itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop != null && ReferenceEquals(itemDrop.m_itemData.m_shared, item.m_shared))
            {
                return prefab;
            }
        }

        GameObject? clonePrefab = ResolveItemPrefabBySharedFields(item, preferCreatedClones: true);
        return clonePrefab ?? ResolveItemPrefabBySharedFields(item, preferCreatedClones: false);
    }

    private static GameObject? ResolveItemPrefabBySharedFields(ItemDrop.ItemData item, bool preferCreatedClones)
    {
        foreach (GameObject prefab in ObjectDB.instance.m_items)
        {
            if (prefab == null || CreatedClones.Contains(prefab.name) != preferCreatedClones)
            {
                continue;
            }

            ItemDrop itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop != null &&
                itemDrop.m_itemData.m_shared.m_itemType == item.m_shared.m_itemType &&
                string.Equals(itemDrop.m_itemData.m_shared.m_name, item.m_shared.m_name, StringComparison.Ordinal))
            {
                return prefab;
            }
        }

        return null;
    }

    private static void RepairDropPrefabs(HashSet<string>? itemKeys = null)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        if (itemKeys != null)
        {
            foreach (string itemKey in itemKeys)
            {
                GameObject? prefab = ResolveItemPrefab(itemKey);
                if (prefab == null || !prefab.TryGetComponent(out ItemDrop itemDrop))
                {
                    continue;
                }

                itemDrop.m_itemData.m_dropPrefab = prefab;
            }

            return;
        }

        foreach (GameObject prefab in ObjectDB.instance.m_items)
        {
            if (prefab == null)
            {
                continue;
            }

            ItemDrop itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop != null)
            {
                itemDrop.m_itemData.m_dropPrefab = prefab;
            }
        }
    }

    private static void RegisterObjectDbItemPrefab(GameObject prefab, bool addToItems = false)
    {
        if (ObjectDB.instance == null || prefab == null)
        {
            return;
        }

        if (addToItems && !ObjectDB.instance.m_items.Contains(prefab))
        {
            ObjectDB.instance.m_items.Add(prefab);
        }

        int hash = prefab.name.GetStableHashCode();
        ObjectDB.instance.m_itemByHash[hash] = prefab;

        ItemDrop itemDrop = prefab.GetComponent<ItemDrop>();
        if (itemDrop != null)
        {
            itemDrop.m_itemData.m_dropPrefab = prefab;
        }

        RegisterZNetScenePrefab(prefab);
    }

    private static void UnregisterObjectDbItemPrefab(string prefabName, GameObject prefab)
    {
        if (ObjectDB.instance == null || prefab == null)
        {
            return;
        }

        while (ObjectDB.instance.m_items.Remove(prefab))
        {
        }

        int hash = prefabName.GetStableHashCode();
        if (ObjectDB.instance.m_itemByHash.TryGetValue(hash, out GameObject registered) &&
            ReferenceEquals(registered, prefab))
        {
            ObjectDB.instance.m_itemByHash.Remove(hash);
        }
    }

    private static void RegisterZNetScenePrefab(GameObject prefab)
    {
        if (ZNetScene.instance == null || prefab == null)
        {
            return;
        }

        List<GameObject> targetPrefabs = prefab.GetComponent<ZNetView>() != null
            ? ZNetScene.instance.m_prefabs
            : ZNetScene.instance.m_nonNetViewPrefabs;
        List<GameObject> otherPrefabs = prefab.GetComponent<ZNetView>() != null
            ? ZNetScene.instance.m_nonNetViewPrefabs
            : ZNetScene.instance.m_prefabs;

        otherPrefabs.Remove(prefab);
        if (!targetPrefabs.Contains(prefab))
        {
            targetPrefabs.Add(prefab);
        }

        int hash = prefab.name.GetStableHashCode();
        ZNetScene.instance.m_namedPrefabs[hash] = prefab;
    }

    private static void UnregisterZNetScenePrefab(string prefabName, GameObject prefab)
    {
        if (ZNetScene.instance == null || prefab == null)
        {
            return;
        }

        while (ZNetScene.instance.m_prefabs.Remove(prefab))
        {
        }

        while (ZNetScene.instance.m_nonNetViewPrefabs.Remove(prefab))
        {
        }

        int hash = prefabName.GetStableHashCode();
        if (ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out GameObject registered) &&
            ReferenceEquals(registered, prefab))
        {
            ZNetScene.instance.m_namedPrefabs.Remove(hash);
        }
    }

    private static void ApplyToItemPrefabs(
        Dictionary<string, List<ItemEntry>> entriesByItem,
        HashSet<string>? itemKeys = null)
    {
        IEnumerable<(string PrefabName, ItemDrop ItemDrop)> itemDrops = itemKeys == null
            ? GetItemDrops()
            : GetItemDrops(itemKeys);

        foreach ((string prefabName, ItemDrop itemDrop) in itemDrops)
        {
            TrackReferenceVisibility(prefabName, itemDrop);
            ItemVisualOverrides.Restore(prefabName, itemDrop);

            if (Baselines.TryGetValue(prefabName, out ItemDefinition? baseline))
            {
                ApplyDefinition(itemDrop, baseline);
            }

            if (!DataForgePlugin.ItemOverridesEnabled)
            {
                continue;
            }

            if (Baselines.TryGetValue(prefabName, out baseline))
            {
                ApplyGlobalItemMultipliers(itemDrop.m_itemData.m_shared, baseline);
            }

            if (!entriesByItem.TryGetValue(prefabName, out List<ItemEntry> entries))
            {
                continue;
            }

            foreach (ItemEntry entry in entries)
            {
                using (DataForgeLogContext.Push(entry.LogContext))
                {
                    ApplyDefinition(itemDrop, entry.ToDefinition());
                }
            }
        }
    }

    private static IEnumerable<(string PrefabName, ItemDrop ItemDrop)> GetItemDrops(IEnumerable<string> prefabNames)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string prefabName in prefabNames)
        {
            if (!seen.Add(prefabName))
            {
                continue;
            }

            GameObject? prefab = ResolveItemPrefab(prefabName);
            if (prefab != null && prefab.TryGetComponent(out ItemDrop itemDrop))
            {
                yield return (GetPrefabName(prefab), itemDrop);
            }
        }
    }

    private static void ApplyLiveSafeToExistingItems(
        Dictionary<string, List<ItemEntry>> entriesByItem,
        HashSet<string>? itemKeys = null)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        if (itemKeys is { Count: 0 })
        {
            return;
        }

        HashSet<ItemDrop.ItemData> seen = new();
        int applied = 0;

        if (Player.s_players != null)
        {
            foreach (Player player in Player.s_players)
            {
                if (player == null)
                {
                    continue;
                }

                Inventory inventory = player.GetInventory();
                if (inventory == null)
                {
                    continue;
                }

                int inventoryApplied = ApplyLiveSafeToInventory(inventory, entriesByItem, seen, itemKeys);
                if (inventoryApplied <= 0)
                {
                    continue;
                }

                applied += inventoryApplied;
                inventory.Changed();
                RefreshPlayerEquipment(player);
            }
        }

        foreach (Container container in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
        {
            if (container == null)
            {
                continue;
            }

            Inventory inventory = container.GetInventory();
            if (inventory == null)
            {
                continue;
            }

            int inventoryApplied = ApplyLiveSafeToInventory(inventory, entriesByItem, seen, itemKeys);
            if (inventoryApplied <= 0)
            {
                continue;
            }

            applied += inventoryApplied;
            inventory.Changed();
        }

        foreach (ItemDrop itemDrop in ItemDrop.s_instances)
        {
            if (itemDrop == null || itemDrop.m_itemData == null || !seen.Add(itemDrop.m_itemData))
            {
                continue;
            }

            if (ApplyLiveSafeToItemDrop(itemDrop, entriesByItem, itemKeys))
            {
                applied++;
            }
        }

        if (applied > 0)
        {
            DataForgePlugin.Log.LogDebug($"Live-refreshed {applied} existing item instances.");
        }
    }

    private static int ApplyLiveSafeToInventory(
        Inventory inventory,
        Dictionary<string, List<ItemEntry>> entriesByItem,
        HashSet<ItemDrop.ItemData> seen,
        HashSet<string>? itemKeys)
    {
        int applied = 0;
        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (item == null || !seen.Add(item))
            {
                continue;
            }

            if (ApplyLiveSafeToItem(item, entriesByItem, itemKeys))
            {
                applied++;
            }
        }

        return applied;
    }

    private static bool ApplyLiveSafeToItemDrop(
        ItemDrop itemDrop,
        Dictionary<string, List<ItemEntry>> entriesByItem,
        HashSet<string>? itemKeys)
    {
        bool applied = ApplyLiveSafeToItem(itemDrop.m_itemData, entriesByItem, itemKeys);
        GameObject? prefab = itemDrop.m_itemData.m_dropPrefab ?? ResolveItemPrefab(itemDrop.m_itemData);
        if (prefab == null)
        {
            return applied;
        }

        string prefabName = GetPrefabName(prefab);
        if (itemKeys != null && !itemKeys.Contains(prefabName))
        {
            return applied;
        }

        bool floatingApplied = false;
        if (Baselines.TryGetValue(prefabName, out ItemDefinition? baseline))
        {
            ApplyFloating(itemDrop.gameObject, baseline.Basics?.Floating, immediate: false);
            floatingApplied = baseline.Basics?.Floating.HasValue == true;
        }

        if (!DataForgePlugin.ItemOverridesEnabled)
        {
            return applied || floatingApplied;
        }

        if (!entriesByItem.TryGetValue(prefabName, out List<ItemEntry> entries))
        {
            return applied || floatingApplied;
        }

        foreach (ItemEntry entry in entries)
        {
            using (DataForgeLogContext.Push(entry.LogContext))
            {
                BasicsDefinition? basics = entry.ToDefinition().Basics;
                ApplyFloating(itemDrop.gameObject, basics?.Floating, immediate: false);
                floatingApplied = floatingApplied || basics?.Floating.HasValue == true;
            }
        }

        return applied || floatingApplied;
    }

    private static bool ApplyLiveSafeToItem(
        ItemDrop.ItemData item,
        Dictionary<string, List<ItemEntry>> entriesByItem,
        HashSet<string>? itemKeys = null)
    {
        if (item == null || item.m_shared == null)
        {
            return false;
        }

        RepairDropPrefab(item);
        GameObject? prefab = item.m_dropPrefab ?? ResolveItemPrefab(item);
        if (prefab == null)
        {
            return false;
        }

        item.m_dropPrefab ??= prefab;
        string prefabName = GetPrefabName(prefab);
        if (itemKeys != null && !itemKeys.Contains(prefabName))
        {
            return false;
        }

        bool applied = false;

        if (Baselines.TryGetValue(prefabName, out ItemDefinition? baseline))
        {
            ApplyLiveSafeDefinition(item.m_shared, baseline);
            applied = true;
        }

        if (!DataForgePlugin.ItemOverridesEnabled)
        {
            return applied;
        }

        if (Baselines.TryGetValue(prefabName, out baseline))
        {
            ApplyGlobalItemMultipliers(item.m_shared, baseline);
            applied = true;
        }

        if (!entriesByItem.TryGetValue(prefabName, out List<ItemEntry> entries))
        {
            return applied;
        }

        foreach (ItemEntry entry in entries)
        {
            using (DataForgeLogContext.Push(entry.LogContext))
            {
                ApplyLiveSafeDefinition(item.m_shared, entry.ToDefinition());
            }
            applied = true;
        }

        return applied;
    }

    private static void RefreshPlayerEquipment(Player player)
    {
        if (SetupEquipmentMethod == null)
        {
            return;
        }

        try
        {
            SetupEquipmentMethod.Invoke(player, null);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not refresh player equipment after item live update: {ex.Message}");
        }
    }

    private static void TrackReferenceVisibility(string prefabName, ItemDrop itemDrop)
    {
        if (IsReferenceVisibleItem(itemDrop))
        {
            ReferenceVisiblePrefabs.Add(prefabName);
        }
        else
        {
            ReferenceVisiblePrefabs.Remove(prefabName);
        }
    }

    private static bool IsReferenceVisibleItem(ItemDrop itemDrop)
    {
        ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
        return shared.m_icons != null && shared.m_icons.Length > 0;
    }

    private static void ApplyDefinition(ItemDrop itemDrop, ItemDefinition definition)
    {
        ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;

        ApplyBasics(shared, definition.Basics);
        ApplyFloating(itemDrop.gameObject, definition.Basics?.Floating, immediate: true);
        ApplyDurability(shared, definition.Durability);
        ApplyEquipment(shared, definition.Equipment);
        ApplyDamageTakenModifiers(shared, definition.DamageTakenModifiers);
        ApplyFood(shared, definition.Food);
        ApplyShield(shared, definition.Shield);
        ApplyCombat(shared, definition.Combat);
        ApplyEffects(shared, definition.Effects);
        ItemVisualOverrides.Apply(itemDrop, definition.Visual);
    }

    private static void ApplyLiveSafeDefinition(ItemDrop.ItemData.SharedData shared, ItemDefinition definition)
    {
        ApplyLiveSafeBasics(shared, definition.Basics);
        ApplyDurability(shared, definition.Durability);
        ApplyLiveSafeEquipment(shared, definition.Equipment);
        ApplyDamageTakenModifiers(shared, definition.DamageTakenModifiers);
        ApplyFood(shared, definition.Food);
        ApplyShield(shared, definition.Shield);
        ApplyCombat(shared, definition.Combat);
        ApplyLiveSafeEffects(shared, definition.Effects);
    }

    private static void ApplyBasics(ItemDrop.ItemData.SharedData shared, BasicsDefinition? basics)
    {
        if (basics == null)
        {
            return;
        }

        Copy(basics.Name, value => shared.m_name = value);
        Copy(basics.Description, value => shared.m_description = value);
        Copy(basics.Subtitle, value => shared.m_subtitle = value);
        CopyEnum<ItemDrop.ItemData.ItemType>(basics.ItemType, value => shared.m_itemType = value, "itemType");
        CopyFloatValue(basics.Weight, value => shared.m_weight = value);
        Copy(basics.Value, value => shared.m_value = Math.Max(0, value));
        Copy(basics.MaxStackSize, value => shared.m_maxStackSize = Math.Max(1, value));
        Copy(basics.MaxQuality, value => shared.m_maxQuality = Math.Max(1, value));
        Copy(basics.Teleportable, value => shared.m_teleportable = value);
    }

    private static void ApplyLiveSafeBasics(ItemDrop.ItemData.SharedData shared, BasicsDefinition? basics)
    {
        if (basics == null)
        {
            return;
        }

        Copy(basics.Name, value => shared.m_name = value);
        Copy(basics.Description, value => shared.m_description = value);
        Copy(basics.Subtitle, value => shared.m_subtitle = value);
        CopyFloatValue(basics.Weight, value => shared.m_weight = value);
        Copy(basics.Value, value => shared.m_value = Math.Max(0, value));
        Copy(basics.MaxStackSize, value => shared.m_maxStackSize = Math.Max(1, value));
        Copy(basics.Teleportable, value => shared.m_teleportable = value);
    }

    private static void ApplyGlobalItemMultipliers(ItemDrop.ItemData.SharedData shared, ItemDefinition baseline)
    {
        BasicsDefinition? basics = baseline.Basics;
        if (basics == null)
        {
            return;
        }

        int stackMultiplier = DataForgePlugin.StackableStackMultiplier;
        if (stackMultiplier != 1 && basics.MaxStackSize.HasValue && basics.MaxStackSize.Value > 1)
        {
            long multipliedStack = (long)basics.MaxStackSize.Value * stackMultiplier;
            shared.m_maxStackSize = (int)Math.Min(int.MaxValue, Math.Max(1L, multipliedStack));
        }

        float weightMultiplier = DataForgePlugin.ItemWeightMultiplier;
        if (Math.Abs(weightMultiplier - 1f) <= 0.0001f)
        {
            return;
        }

        if (float.TryParse(basics.Weight, NumberStyles.Float, CultureInfo.InvariantCulture, out float baselineWeight))
        {
            shared.m_weight = Math.Max(0f, baselineWeight * weightMultiplier);
        }
    }

    private static void ApplyFloating(GameObject gameObject, bool? floating, bool immediate)
    {
        if (!floating.HasValue || gameObject == null)
        {
            return;
        }

        Floating component = gameObject.GetComponent<Floating>();
        if (floating.Value)
        {
            if (component == null)
            {
                gameObject.AddComponent<Floating>();
            }

            return;
        }

        if (component == null)
        {
            return;
        }

        try
        {
            if (immediate)
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
            else
            {
                UnityEngine.Object.Destroy(component);
            }
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not remove Floating component from '{GetPrefabName(gameObject)}': {ex.Message}");
            UnityEngine.Object.Destroy(component);
        }
    }

    private static void ApplyDurability(ItemDrop.ItemData.SharedData shared, string? value)
    {
        DurabilityDefinition? durability = ParseDurability(value);
        if (durability == null)
        {
            return;
        }

        Copy(durability.UseDurability, value => shared.m_useDurability = value);
        Copy(durability.MaxDurability, value => shared.m_maxDurability = Math.Max(0f, value));
        Copy(durability.DurabilityPerLevel, value => shared.m_durabilityPerLevel = Math.Max(0f, value));
        Copy(durability.UseDurabilityDrain, value => shared.m_useDurabilityDrain = Math.Max(0f, value));
        Copy(durability.CanBeRepaired, value => shared.m_canBeReparied = value);
        Copy(durability.DestroyBroken, value => shared.m_destroyBroken = value);
        Copy(durability.DurabilityDrain, value => shared.m_durabilityDrain = Math.Max(0f, value));
    }

    private static DurabilityDefinition? ParseDurability(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        DurabilityDefinition durability = new();
        if (parts.Length > 0 && parts[0].Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float maxDurability))
        {
            durability.MaxDurability = maxDurability;
        }

        if (parts.Length > 1 && parts[1].Length > 0 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float durabilityPerLevel))
        {
            durability.DurabilityPerLevel = durabilityPerLevel;
        }

        if (parts.Length > 2 && parts[2].Length > 0 && bool.TryParse(parts[2], out bool useDurability))
        {
            durability.UseDurability = useDurability;
        }

        if (parts.Length > 3 && parts[3].Length > 0 && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float useDurabilityDrain))
        {
            durability.UseDurabilityDrain = useDurabilityDrain;
        }

        if (parts.Length > 4 && parts[4].Length > 0 && bool.TryParse(parts[4], out bool canBeRepaired))
        {
            durability.CanBeRepaired = canBeRepaired;
        }

        if (parts.Length > 5 && parts[5].Length > 0 && bool.TryParse(parts[5], out bool destroyBroken))
        {
            durability.DestroyBroken = destroyBroken;
        }

        if (parts.Length > 6 && parts[6].Length > 0 && float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float durabilityDrain))
        {
            durability.DurabilityDrain = durabilityDrain;
        }

        return durability;
    }

    private static void ApplyEquipment(ItemDrop.ItemData.SharedData shared, EquipmentDefinition? equipment)
    {
        if (equipment == null)
        {
            return;
        }

        CopyEnum<Skills.SkillType>(equipment.SkillType, value => shared.m_skillType = value, "skillType");
        Copy(equipment.EquipDuration, value => shared.m_equipDuration = Math.Max(0f, value));
        Copy(equipment.MovementModifier, value => shared.m_movementModifier = value);
        Copy(equipment.EitrRegenModifier, value => shared.m_eitrRegenModifier = value);
        Copy(equipment.HeatResistanceModifier, value => shared.m_heatResistanceModifier = value);
        Copy(equipment.HomeItemsStaminaModifier, value => shared.m_homeItemsStaminaModifier = value);
        Copy(equipment.AttackStaminaModifier, value => shared.m_attackStaminaModifier = value);
        Copy(equipment.BlockStaminaModifier, value => shared.m_blockStaminaModifier = value);
        Copy(equipment.DodgeStaminaModifier, value => shared.m_dodgeStaminaModifier = value);
        Copy(equipment.JumpStaminaModifier, value => shared.m_jumpStaminaModifier = value);
        Copy(equipment.RunStaminaModifier, value => shared.m_runStaminaModifier = value);
        Copy(equipment.SneakStaminaModifier, value => shared.m_sneakStaminaModifier = value);
        Copy(equipment.SwimStaminaModifier, value => shared.m_swimStaminaModifier = value);
        CopyFloatPair(equipment.Armor, value => shared.m_armor = value, value => shared.m_armorPerLevel = value);
        Copy(equipment.MaxAdrenaline, value => shared.m_maxAdrenaline = Math.Max(0f, value));
    }

    private static void ApplyLiveSafeEquipment(ItemDrop.ItemData.SharedData shared, EquipmentDefinition? equipment)
    {
        if (equipment == null)
        {
            return;
        }

        Copy(equipment.MovementModifier, value => shared.m_movementModifier = value);
        Copy(equipment.EitrRegenModifier, value => shared.m_eitrRegenModifier = value);
        Copy(equipment.HeatResistanceModifier, value => shared.m_heatResistanceModifier = value);
        Copy(equipment.HomeItemsStaminaModifier, value => shared.m_homeItemsStaminaModifier = value);
        Copy(equipment.AttackStaminaModifier, value => shared.m_attackStaminaModifier = value);
        Copy(equipment.BlockStaminaModifier, value => shared.m_blockStaminaModifier = value);
        Copy(equipment.DodgeStaminaModifier, value => shared.m_dodgeStaminaModifier = value);
        Copy(equipment.JumpStaminaModifier, value => shared.m_jumpStaminaModifier = value);
        Copy(equipment.RunStaminaModifier, value => shared.m_runStaminaModifier = value);
        Copy(equipment.SneakStaminaModifier, value => shared.m_sneakStaminaModifier = value);
        Copy(equipment.SwimStaminaModifier, value => shared.m_swimStaminaModifier = value);
        CopyFloatPair(equipment.Armor, value => shared.m_armor = value, value => shared.m_armorPerLevel = value);
        Copy(equipment.MaxAdrenaline, value => shared.m_maxAdrenaline = Math.Max(0f, value));
    }

    private static void ApplyDamageTakenModifiers(ItemDrop.ItemData.SharedData shared, DamageTakenModifierDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        shared.m_damageModifiers ??= new List<HitData.DamageModPair>();
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Blunt, definition.Blunt, "damageTakenModifiers.blunt");
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Slash, definition.Slash, "damageTakenModifiers.slash");
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Pierce, definition.Pierce, "damageTakenModifiers.pierce");
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Chop, definition.Chop, "damageTakenModifiers.chop");
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Pickaxe, definition.Pickaxe, "damageTakenModifiers.pickaxe");
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Fire, definition.Fire, "damageTakenModifiers.fire");
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Frost, definition.Frost, "damageTakenModifiers.frost");
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Lightning, definition.Lightning, "damageTakenModifiers.lightning");
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Poison, definition.Poison, "damageTakenModifiers.poison");
        ApplyDamageTakenModifier(shared.m_damageModifiers, HitData.DamageType.Spirit, definition.Spirit, "damageTakenModifiers.spirit");
    }

    private static void ApplyDamageTakenModifier(List<HitData.DamageModPair> modifiers, HitData.DamageType damageType, string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string trimmed = value!.Trim();
        bool removeModifier = trimmed.Equals("None", StringComparison.OrdinalIgnoreCase);
        HitData.DamageModifier modifier = HitData.DamageModifier.Normal;
        if (!removeModifier && !Enum.TryParse(trimmed, ignoreCase: true, out modifier))
        {
            DataForgeLogContext.Warning($"Unknown item {fieldName} value '{trimmed}'.");
            return;
        }

        int index = modifiers.FindIndex(pair => pair.m_type == damageType);
        if (removeModifier || modifier == HitData.DamageModifier.Normal)
        {
            if (index >= 0)
            {
                modifiers.RemoveAt(index);
            }

            return;
        }

        HitData.DamageModPair pair = new()
        {
            m_type = damageType,
            m_modifier = modifier
        };

        if (index >= 0)
        {
            modifiers[index] = pair;
            return;
        }

        modifiers.Add(pair);
    }

    private static void CopyFloatValue(string? value, Action<float> assign)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string firstPart = value!.Split(new[] { ',' }, 2, StringSplitOptions.None)[0].Trim();
        if (firstPart.Length > 0 && float.TryParse(firstPart, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue))
        {
            assign(Math.Max(0f, parsedValue));
        }
    }

    private static void CopyFloatPair(string? value, Action<float> assignFirst, Action<float> assignSecond)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length > 0 && parts[0].Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float first))
        {
            assignFirst(Math.Max(0f, first));
        }

        if (parts.Length > 1 && parts[1].Length > 0 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float second))
        {
            assignSecond(Math.Max(0f, second));
        }
    }

    private static void ApplyFood(ItemDrop.ItemData.SharedData shared, string? value)
    {
        FoodDefinition? food = ParseFood(value);
        if (food == null)
        {
            return;
        }

        Copy(food.Health, value => shared.m_food = Math.Max(0f, value));
        Copy(food.Stamina, value => shared.m_foodStamina = Math.Max(0f, value));
        Copy(food.Eitr, value => shared.m_foodEitr = Math.Max(0f, value));
        Copy(food.Regen, value => shared.m_foodRegen = Math.Max(0f, value));
        Copy(food.BurnTime, value => shared.m_foodBurnTime = Math.Max(0f, value));
    }

    private static FoodDefinition? ParseFood(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length > 5)
        {
            DataForgeLogContext.Warning($"Ignoring item food tuple with {parts.Length} values. Expected: health, stamina, eitr, regen, burnTime.");
            return null;
        }

        FoodDefinition food = new();
        if (parts.Length > 0 && parts[0].Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float health))
        {
            food.Health = health;
        }

        if (parts.Length > 1 && parts[1].Length > 0 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float stamina))
        {
            food.Stamina = stamina;
        }

        if (parts.Length > 2 && parts[2].Length > 0 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float eitr))
        {
            food.Eitr = eitr;
        }

        if (parts.Length > 3 && parts[3].Length > 0 && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float regen))
        {
            food.Regen = regen;
        }

        if (parts.Length > 4 && parts[4].Length > 0 && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float burnTime))
        {
            food.BurnTime = burnTime;
        }

        return food;
    }


    private static void ApplyShield(ItemDrop.ItemData.SharedData shared, ShieldDefinition? shield)
    {
        if (shield == null)
        {
            return;
        }

        CopyFloatPair(shield.BlockPower, value => shared.m_blockPower = value, value => shared.m_blockPowerPerLevel = value);
        CopyFloatPair(shield.DeflectionForce, value => shared.m_deflectionForce = value, value => shared.m_deflectionForcePerLevel = value);
        Copy(shield.TimedBlockBonus, value => shared.m_timedBlockBonus = Math.Max(0f, value));
    }

    private static void ApplyCombat(ItemDrop.ItemData.SharedData shared, CombatDefinition? combat)
    {
        if (combat == null)
        {
            return;
        }

        ApplyDamage(shared, combat.Damage);
        Copy(combat.BackstabBonus, value => shared.m_backstabBonus = Math.Max(0f, value));
        Copy(combat.AttackForce, value => shared.m_attackForce = Math.Max(0f, value));
        ApplyAttack(shared.m_attack, combat.PrimaryAttack, "primaryAttack");
        ApplyPrimaryAttack(shared.m_attack, combat.PrimaryAttack);
        ApplyAttack(shared.m_secondaryAttack, combat.SecondaryAttack, "secondaryAttack");
    }

    private static void ApplyMissingHealth(Attack attack, string? value)
    {
        if (attack == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = SplitTuple(value);
        CopyTupleFloat(parts, 0, parsed => attack.m_damageMultiplierPerMissingHP = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 1, parsed => attack.m_damageMultiplierByTotalHealthMissing = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 2, parsed => attack.m_staminaReturnPerMissingHP = Math.Max(0f, parsed));
    }

    private static void ApplySpawnOnHit(Attack attack, string? value, string fieldName)
    {
        if (attack == null || value == null)
        {
            return;
        }

        string[] parts = SplitTuple(value);
        if (parts.Length == 0)
        {
            return;
        }

        bool changedPrefab = false;
        string prefabName = parts[0];
        if (prefabName.Length > 0)
        {
            if (IsNone(prefabName))
            {
                attack.m_spawnOnHit = null;
                attack.m_spawnOnHitChance = 0f;
                changedPrefab = true;
            }
            else
            {
                GameObject? prefab = ResolvePrefab(prefabName);
                if (prefab == null)
                {
                    if (!ZNetSceneReady)
                    {
                        return;
                    }

                    DataForgeLogContext.Warning($"Could not resolve item {fieldName} prefab '{prefabName}'.");
                    return;
                }

                attack.m_spawnOnHit = prefab;
                changedPrefab = true;
                if (parts.Length == 1 && attack.m_spawnOnHitChance <= 0f)
                {
                    attack.m_spawnOnHitChance = 1f;
                }
            }
        }

        if (parts.Length > 1 && parts[1].Length > 0 &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float chance))
        {
            attack.m_spawnOnHitChance = Mathf.Clamp01(chance);
        }
        else if (changedPrefab && attack.m_spawnOnHit == null)
        {
            attack.m_spawnOnHitChance = 0f;
        }
    }

    private static void ApplySpawnOnTrigger(Attack attack, string? value, string fieldName)
    {
        if (attack == null || value == null)
        {
            return;
        }

        string prefabName = value.Trim();
        if (prefabName.Length == 0)
        {
            return;
        }

        if (IsNone(prefabName))
        {
            attack.m_spawnOnTrigger = null;
            return;
        }

        GameObject? prefab = ResolvePrefab(prefabName);
        if (prefab == null)
        {
            if (!ZNetSceneReady)
            {
                return;
            }

            DataForgeLogContext.Warning($"Could not resolve item {fieldName} prefab '{prefabName}'.");
            return;
        }

        attack.m_spawnOnTrigger = prefab;
    }

    private static void ApplyProjectile(Attack attack, string? value, string fieldName)
    {
        if (attack == null || value == null)
        {
            return;
        }

        string[] parts = SplitTuple(value);
        if (parts.Length == 0)
        {
            return;
        }

        string prefabName = parts[0];
        if (prefabName.Length > 0)
        {
            if (IsNone(prefabName))
            {
                attack.m_attackProjectile = null;
                attack.m_projectileVel = 0f;
                attack.m_projectileVelMin = 0f;
                attack.m_projectiles = 1;
                attack.m_projectileAccuracy = 0f;
                attack.m_projectileAccuracyMin = 0f;
                return;
            }

            GameObject? prefab = ResolvePrefab(prefabName);
            if (prefab == null)
            {
                if (!ZNetSceneReady)
                {
                    return;
                }

                DataForgeLogContext.Warning($"Could not resolve item {fieldName} prefab '{prefabName}'.");
                return;
            }

            attack.m_attackProjectile = prefab;
        }

        CopyTupleFloat(parts, 1, parsed => attack.m_projectileVel = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 2, parsed => attack.m_projectileVelMin = Math.Max(0f, parsed));
        CopyTupleInt(parts, 3, parsed => attack.m_projectiles = Math.Max(1, parsed));
        CopyTupleFloat(parts, 4, parsed => attack.m_projectileAccuracy = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 5, parsed => attack.m_projectileAccuracyMin = Math.Max(0f, parsed));
    }

    private static void ApplyDamage(ItemDrop.ItemData.SharedData shared, DamageDefinition? damage)
    {
        if (damage == null)
        {
            return;
        }

        HitData.DamageTypes damageTarget = shared.m_damages;
        HitData.DamageTypes damagePerLevelTarget = shared.m_damagesPerLevel;
        CopyDamage(damage.Blunt, value => damageTarget.m_blunt = value, value => damagePerLevelTarget.m_blunt = value);
        CopyDamage(damage.Slash, value => damageTarget.m_slash = value, value => damagePerLevelTarget.m_slash = value);
        CopyDamage(damage.Pierce, value => damageTarget.m_pierce = value, value => damagePerLevelTarget.m_pierce = value);
        CopyDamage(damage.Chop, value => damageTarget.m_chop = value, value => damagePerLevelTarget.m_chop = value);
        CopyDamage(damage.Pickaxe, value => damageTarget.m_pickaxe = value, value => damagePerLevelTarget.m_pickaxe = value);
        CopyDamage(damage.Fire, value => damageTarget.m_fire = value, value => damagePerLevelTarget.m_fire = value);
        CopyDamage(damage.Frost, value => damageTarget.m_frost = value, value => damagePerLevelTarget.m_frost = value);
        CopyDamage(damage.Lightning, value => damageTarget.m_lightning = value, value => damagePerLevelTarget.m_lightning = value);
        CopyDamage(damage.Poison, value => damageTarget.m_poison = value, value => damagePerLevelTarget.m_poison = value);
        CopyDamage(damage.Spirit, value => damageTarget.m_spirit = value, value => damagePerLevelTarget.m_spirit = value);
        shared.m_damages = damageTarget;
        shared.m_damagesPerLevel = damagePerLevelTarget;
    }

    private static void CopyDamage(string? value, Action<float> assignDamage, Action<float> assignDamagePerLevel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length > 0 && parts[0].Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float damage))
        {
            assignDamage(Math.Max(0f, damage));
        }

        if (parts.Length > 1 && parts[1].Length > 0 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float damagePerLevel))
        {
            assignDamagePerLevel(Math.Max(0f, damagePerLevel));
        }
    }

    private static void ApplyAttack(Attack attack, AttackDefinition? definition, string fieldName)
    {
        if (attack == null || definition == null)
        {
            return;
        }

        ApplyAttackCost(attack, definition.Cost);
        ApplyMissingHealth(attack, definition.MissingHealth);
        ApplySpawnOnTrigger(attack, definition.SpawnOnTrigger, $"{fieldName}.spawnOnTrigger");
        ApplySpawnOnHit(attack, definition.SpawnOnHit, $"{fieldName}.spawnOnHit");
        ApplyProjectile(attack, definition.Projectile, $"{fieldName}.projectile");
        ApplyAttackDraw(attack, definition.Draw);
        ApplyAttackReload(attack, definition.Reload);
        Copy(definition.DamageMultiplier, value => attack.m_damageMultiplier = Math.Max(0f, value));
        Copy(definition.ForceMultiplier, value => attack.m_forceMultiplier = Math.Max(0f, value));
        Copy(definition.StaggerMultiplier, value => attack.m_staggerMultiplier = Math.Max(0f, value));
        Copy(definition.RaiseSkillAmount, value => attack.m_raiseSkillAmount = Math.Max(0f, value));
    }

    private static void ApplyPrimaryAttack(Attack attack, PrimaryAttackDefinition? definition)
    {
        if (attack == null || definition == null)
        {
            return;
        }

        Copy(definition.LastChainDamageMultiplier, value => attack.m_lastChainDamageMultiplier = Math.Max(0f, value));
    }

    private static void ApplyAttackCost(Attack attack, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = SplitTuple(value);
        CopyTupleFloat(parts, 0, parsed => attack.m_attackStamina = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 1, parsed => attack.m_attackEitr = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 2, parsed => attack.m_attackHealth = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 3, parsed => attack.m_attackHealthPercentage = Mathf.Clamp01(parsed));
    }

    private static void ApplyAttackDraw(Attack attack, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = SplitTuple(value);
        CopyTupleFloat(parts, 0, parsed => attack.m_drawDurationMin = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 1, parsed => attack.m_drawStaminaDrain = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 2, parsed => attack.m_drawEitrDrain = Math.Max(0f, parsed));
    }

    private static void ApplyAttackReload(Attack attack, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = SplitTuple(value);
        CopyTupleBool(parts, 0, parsed => attack.m_requiresReload = parsed);
        CopyTupleFloat(parts, 1, parsed => attack.m_reloadTime = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 2, parsed => attack.m_reloadStaminaDrain = Math.Max(0f, parsed));
        CopyTupleFloat(parts, 3, parsed => attack.m_reloadEitrDrain = Math.Max(0f, parsed));
    }

    private static void CopyTupleFloat(string[] parts, int index, Action<float> assign)
    {
        if (parts.Length > index &&
            parts[index].Length > 0 &&
            float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            assign(parsed);
        }
    }

    private static void CopyTupleInt(string[] parts, int index, Action<int> assign)
    {
        if (parts.Length > index &&
            parts[index].Length > 0 &&
            int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            assign(parsed);
        }
    }

    private static void CopyTupleBool(string[] parts, int index, Action<bool> assign)
    {
        if (parts.Length > index &&
            parts[index].Length > 0 &&
            bool.TryParse(parts[index], out bool parsed))
        {
            assign(parsed);
        }
    }

    private static void ApplyEffects(ItemDrop.ItemData.SharedData shared, EffectsDefinition? effects)
    {
        if (effects == null)
        {
            return;
        }

        CopyStatusEffect(effects.EquipStatusEffect, value => shared.m_equipStatusEffect = value);
        ApplySetEffect(shared, effects.Set);
        CopyStatusEffect(effects.ConsumeStatusEffect, value => shared.m_consumeStatusEffect = value);
        ApplyAttackStatusEffect(shared, effects.AttackStatusEffect);
        CopyStatusEffect(effects.PerfectBlockStatusEffect, value => shared.m_perfectBlockStatusEffect = value);
        CopyStatusEffect(effects.FullAdrenalineStatusEffect, value => shared.m_fullAdrenalineSE = value);
    }

    private static void ApplyLiveSafeEffects(ItemDrop.ItemData.SharedData shared, EffectsDefinition? effects)
    {
        if (effects == null)
        {
            return;
        }

        CopyStatusEffect(effects.ConsumeStatusEffect, value => shared.m_consumeStatusEffect = value);
        ApplyAttackStatusEffect(shared, effects.AttackStatusEffect);
        CopyStatusEffect(effects.PerfectBlockStatusEffect, value => shared.m_perfectBlockStatusEffect = value);
        CopyStatusEffect(effects.FullAdrenalineStatusEffect, value => shared.m_fullAdrenalineSE = value);
    }

    private static void ApplySetEffect(ItemDrop.ItemData.SharedData shared, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length > 0 && parts[0].Length > 0)
        {
            shared.m_setName = IsNone(parts[0]) ? "" : parts[0];
        }

        if (parts.Length > 1 && parts[1].Length > 0 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int setSize))
        {
            shared.m_setSize = Math.Max(0, setSize);
        }

        if (parts.Length > 2 && parts[2].Length > 0)
        {
            CopyStatusEffect(parts[2], statusEffect => shared.m_setStatusEffect = statusEffect);
        }
    }

    private static void ApplyAttackStatusEffect(ItemDrop.ItemData.SharedData shared, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length > 0 && parts[0].Length > 0)
        {
            CopyStatusEffect(parts[0], statusEffect => shared.m_attackStatusEffect = statusEffect);
        }

        if (parts.Length > 1 && parts[1].Length > 0 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float chance))
        {
            shared.m_attackStatusEffectChance = Mathf.Clamp01(chance);
        }
    }

    private static void CopyStatusEffect(string? statusEffectName, Action<StatusEffect?> assign)
    {
        if (statusEffectName == null)
        {
            return;
        }

        if (IsNone(statusEffectName))
        {
            assign(null);
            return;
        }

        StatusEffect? statusEffect = ResolveStatusEffect(statusEffectName);
        if (statusEffect == null)
        {
            if (ZNetSceneReady && DataForgeWorldLifecycle.IsGameStarted)
            {
                DataForgeLogContext.Warning($"Could not resolve status effect '{statusEffectName}'.");
            }

            return;
        }

        assign(statusEffect);
    }

    private static StatusEffect? ResolveStatusEffect(string statusEffectName)
    {
        if (ObjectDB.instance == null)
        {
            return null;
        }

        StatusEffect statusEffect = ObjectDB.instance.GetStatusEffect(statusEffectName.GetStableHashCode());
        if (statusEffect != null)
        {
            return statusEffect;
        }

        return ObjectDB.instance.m_StatusEffects.FirstOrDefault(effect =>
            effect != null &&
            (effect.name.Equals(statusEffectName, StringComparison.OrdinalIgnoreCase) ||
             effect.m_name.Equals(statusEffectName, StringComparison.OrdinalIgnoreCase)));
    }

    internal static float GetAcquisitionAmountMultiplier(GameObject? itemPrefab)
    {
        if (!DataForgePlugin.ItemOverridesEnabled || itemPrefab == null)
        {
            return 1f;
        }

        ItemDrop itemDrop = itemPrefab.GetComponent<ItemDrop>();
        if (itemDrop == null)
        {
            return 1f;
        }

        string prefabName = GetPrefabName(itemPrefab);
        lock (StateLock)
        {
            return ActiveAmountMultipliers.TryGetValue(prefabName, out float multiplier)
                ? multiplier
                : 1f;
        }
    }

    internal static int ApplyAcquisitionAmountMultiplier(GameObject? itemPrefab, int amount)
    {
        return MultiplyAmount(amount, GetAcquisitionAmountMultiplier(itemPrefab));
    }

    internal static int ApplyAcquisitionAmountMultiplier(ItemDrop.ItemData? item, int amount)
    {
        if (item == null)
        {
            return amount;
        }

        GameObject? prefab = item.m_dropPrefab ?? ResolveItemPrefab(item);
        return ApplyAcquisitionAmountMultiplier(prefab, amount);
    }

    internal static int MultiplyAmount(int amount, float multiplier)
    {
        if (amount <= 0 || IsDefaultAmountMultiplier(multiplier))
        {
            return amount;
        }

        return Mathf.Max(0, Mathf.FloorToInt((float)amount * Math.Max(0f, multiplier) + UnityEngine.Random.Range(0f, 1f)));
    }

    private static bool IsDefaultAmountMultiplier(float multiplier)
    {
        return Math.Abs(multiplier - 1f) <= 0.0001f;
    }

    private static void CopyEnum<TEnum>(string? value, Action<TEnum> assign, string fieldName) where TEnum : struct
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (Enum.TryParse(value, ignoreCase: true, out TEnum parsed))
        {
            assign(parsed);
            return;
        }

        DataForgeLogContext.Warning($"Unknown {fieldName} enum value '{value}'.");
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
                var fullEntries = Baselines
                    .Where(pair => ReferenceVisiblePrefabs.Contains(pair.Key))
                    .Select(pair => new
                    {
                        Entry = CreateOutputEntryMap(pair.Key, pair.Value),
                        OwnerKey = pair.Key,
                        SortKey = DataForgeResourceMap.BuildItemSortKey(
                            pair.Key,
                            DataForgeResourceMap.GetItemTierSortValue(pair.Key),
                            pair.Key)
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
        var referenceEntries = Baselines
            .Where(pair => ReferenceVisiblePrefabs.Contains(pair.Key))
            .Select(pair => new
            {
                Entry = ItemReferenceEntry.From(pair.Key, pair.Value),
                SortKey = DataForgeResourceMap.BuildItemSortKey(
                    pair.Key,
                    DataForgeResourceMap.GetItemTierSortValue(pair.Key),
                    pair.Key)
            })
            .ToList();

        return DataForgeReferenceSections.SerializeReferenceSections(
            referenceEntries,
            entry => entry.SortKey,
            entry => DataForgeOwnerResolver.GetPrefabOwnerName(entry.Entry.Item),
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
        foreach ((string prefabName, _) in GetItemDrops()
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

    private static string GetPrefabName(GameObject gameObject)
    {
        return NormalizePrefabName(gameObject.name);
    }

    private static string NormalizePrefabName(string prefabName)
    {
        return prefabName.Replace("(Clone)", "").Trim();
    }

    private static Dictionary<string, object?> CreateOutputEntryMap(string prefab, ItemDefinition definition)
    {
        ItemOutputProfile output = ItemOutputProfile.From(definition);
        Dictionary<string, object?> entry = new()
        {
            ["item"] = prefab,
            ["override"] = true
        };
        AddBasics(entry, definition.Basics);

        if (output.EmitDurability)
        {
            entry["durability"] = definition.Durability;
        }

        if (output.EmitEquipment)
        {
            entry["equipment"] = CreateEquipmentOutputMap(definition.Equipment, output);
        }

        if (output.EmitDamageTakenModifiers)
        {
            entry["damageTakenModifiers"] = definition.DamageTakenModifiers;
        }

        if (output.EmitFood)
        {
            entry["food"] = definition.Food;
        }

        if (output.EmitShield)
        {
            entry["shield"] = definition.Shield;
        }

        if (output.EmitCombat)
        {
            AddCombatOutputFields(entry, definition, output);
        }

        if (output.EmitEffects)
        {
            entry["effects"] = definition.Effects;
        }

        entry["visual"] = definition.Visual ?? new VisualDefinition
        {
            Icon = ItemVisualOverrides.AutoIconValue,
            IconRotation = ItemVisualOverrides.DefaultAutoIconRotationValue
        };
        return entry;
    }

    private static void AddBasics(Dictionary<string, object?> entry, BasicsDefinition? basics)
    {
        if (basics == null)
        {
            return;
        }

        entry["name"] = basics.Name;
        entry["description"] = basics.Description;
        entry["subtitle"] = basics.Subtitle;
        entry["itemType"] = basics.ItemType;
        entry["weight"] = basics.Weight;
        entry["value"] = basics.Value;
        entry["maxStackSize"] = basics.MaxStackSize;
        entry["maxQuality"] = basics.MaxQuality;
        entry["teleportable"] = basics.Teleportable;
        entry["floating"] = basics.Floating;
    }

    private static Dictionary<string, object?>? CreateEquipmentOutputMap(EquipmentDefinition? equipment, ItemOutputProfile output)
    {
        if (equipment == null)
        {
            return null;
        }

        Dictionary<string, object?> entry = new()
        {
            ["equipDuration"] = equipment.EquipDuration,
            ["movementModifier"] = equipment.MovementModifier,
            ["eitrRegenModifier"] = equipment.EitrRegenModifier,
            ["heatResistanceModifier"] = equipment.HeatResistanceModifier,
            ["homeItemsStaminaModifier"] = equipment.HomeItemsStaminaModifier,
            ["attackStaminaModifier"] = equipment.AttackStaminaModifier,
            ["blockStaminaModifier"] = equipment.BlockStaminaModifier,
            ["dodgeStaminaModifier"] = equipment.DodgeStaminaModifier,
            ["jumpStaminaModifier"] = equipment.JumpStaminaModifier,
            ["runStaminaModifier"] = equipment.RunStaminaModifier,
            ["sneakStaminaModifier"] = equipment.SneakStaminaModifier,
            ["swimStaminaModifier"] = equipment.SwimStaminaModifier
        };

        if (output.EmitEquipmentArmor)
        {
            entry["armor"] = equipment.Armor;
        }

        entry["maxAdrenaline"] = equipment.MaxAdrenaline;

        if (output.EmitEquipmentSkillType)
        {
            entry = new Dictionary<string, object?> { ["skillType"] = equipment.SkillType }
                .Concat(entry)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        return entry;
    }

    private static void AddCombatOutputFields(Dictionary<string, object?> entry, ItemDefinition definition, ItemOutputProfile output)
    {
        CombatDefinition? combat = definition.Combat;
        if (combat == null)
        {
            return;
        }

        if (output.EmitDirectCombatStats)
        {
            entry["damage"] = CreateDamageOutputDefinition(combat);
        }

        if (output.EmitPrimaryAttack)
        {
            entry["primaryAttack"] = CreatePrimaryAttackOutputMap(combat.PrimaryAttack);
        }

        if (output.EmitSecondaryAttack)
        {
            entry["secondaryAttack"] = CreateAttackOutputMap(combat.SecondaryAttack);
        }
    }

    private static DamageDefinition? CreateDamageOutputDefinition(CombatDefinition combat)
    {
        DamageDefinition? damage = combat.Damage;
        if (damage == null && combat.BackstabBonus == null && combat.AttackForce == null)
        {
            return null;
        }

        return new DamageDefinition
        {
            Blunt = damage?.Blunt,
            Slash = damage?.Slash,
            Pierce = damage?.Pierce,
            Chop = damage?.Chop,
            Pickaxe = damage?.Pickaxe,
            Fire = damage?.Fire,
            Frost = damage?.Frost,
            Lightning = damage?.Lightning,
            Poison = damage?.Poison,
            Spirit = damage?.Spirit,
            BackstabBonus = combat.BackstabBonus,
            AttackForce = combat.AttackForce
        };
    }

    private static Dictionary<string, object?>? CreatePrimaryAttackOutputMap(PrimaryAttackDefinition? attack)
    {
        Dictionary<string, object?>? entry = CreateAttackOutputMap(attack);
        if (entry == null)
        {
            return null;
        }

        if (attack!.ChainLevels > 1)
        {
            entry["lastChainDamageMultiplier"] = attack.LastChainDamageMultiplier;
        }

        return entry;
    }

    private static Dictionary<string, object?>? CreateAttackOutputMap(AttackDefinition? attack)
    {
        if (attack == null)
        {
            return null;
        }

        Dictionary<string, object?> entry = new()
        {
            ["cost"] = attack.Cost,
            ["missingHealth"] = attack.MissingHealth,
            ["spawnOnTrigger"] = attack.SpawnOnTrigger,
            ["spawnOnHit"] = attack.SpawnOnHit,
            ["projectile"] = attack.Projectile,
            ["damageMultiplier"] = attack.DamageMultiplier,
            ["forceMultiplier"] = attack.ForceMultiplier,
            ["staggerMultiplier"] = attack.StaggerMultiplier,
            ["raiseSkillAmount"] = attack.RaiseSkillAmount
        };

        if (ShouldExposeAttackDraw(attack.Draw))
        {
            entry["draw"] = attack.Draw;
        }

        if (ShouldExposeAttackReload(attack.Reload))
        {
            entry["reload"] = attack.Reload;
        }

        return entry;
    }

    private sealed class ItemOutputProfile
    {
        public bool EmitDurability { get; private set; }
        public bool EmitEquipment { get; private set; }
        public bool EmitFood { get; private set; }
        public bool EmitShield { get; private set; }
        public bool EmitCombat { get; private set; }
        public bool EmitEquipmentArmor { get; private set; }
        public bool EmitEquipmentSkillType { get; private set; }
        public bool EmitDamageTakenModifiers { get; private set; }
        public bool EmitDirectCombatStats { get; private set; }
        public bool EmitPrimaryAttack { get; private set; }
        public bool EmitSecondaryAttack { get; private set; }
        public bool EmitEffects { get; private set; }

        internal static ItemOutputProfile From(ItemDefinition definition)
        {
            bool armorLike = IsArmorLike(definition);
            bool shieldLike = IsShieldLike(definition);
            bool toolLike = IsToolLike(definition);
            bool weaponLike = IsWeaponLike(definition);
            bool ammoLike = IsAmmoLike(definition);
            bool magicLike = IsMagicLike(definition);
            bool itemTypeTool = IsItemType(definition, ItemDrop.ItemData.ItemType.Tool);
            bool itemTypeConsumable = IsItemType(definition, ItemDrop.ItemData.ItemType.Consumable);
            bool itemTypeHelmet = IsItemType(definition, ItemDrop.ItemData.ItemType.Helmet);
            bool itemTypeTrophy = IsItemType(definition, ItemDrop.ItemData.ItemType.Trophy);
            bool suppressCombatSurface = itemTypeConsumable || itemTypeHelmet || itemTypeTrophy;
            bool suppressEquipmentSurface = itemTypeTrophy;
            bool hasDamage = GetTotalDamage(definition.Combat?.Damage) > 0f;
            bool foodLike = itemTypeConsumable;
            bool primaryAttackSurface = IsPrimaryAttackSurface(definition);
            bool shieldSurface = IsShieldBlockSurface(definition);
            bool equipmentArmorSurface = IsEquipmentArmorSurface(definition);
            bool equipmentSkillTypeSurface = IsEquipmentSkillTypeSurface(definition);
            bool primaryAttackSpecial = HasAttackSpecial(definition.Combat?.PrimaryAttack);
            bool primaryAttackOutput = primaryAttackSurface || primaryAttackSpecial;
            bool secondaryAttackSurface = primaryAttackSurface && HasAttackAnimation(definition.Combat?.SecondaryAttack);
            bool combatLike = weaponLike || shieldLike || magicLike || hasDamage || ammoLike && hasDamage || primaryAttackOutput || secondaryAttackSurface;
            bool equipmentLike = armorLike || weaponLike || shieldLike || toolLike || magicLike;
            bool bombLike = definition.Combat?.IsBombLike == true;
            bool damageTakenModifierSurface = armorLike || HasDamageTakenModifiers(definition.DamageTakenModifiers);

            return new ItemOutputProfile
            {
                EmitDurability = !itemTypeConsumable && (equipmentLike || HasDurabilityEnabled(definition.Durability)),
                EmitEquipment = !suppressEquipmentSurface && equipmentLike,
                EmitFood = foodLike,
                EmitShield = !suppressCombatSurface && shieldSurface,
                EmitCombat = !suppressCombatSurface && !shieldLike && combatLike,
                EmitEquipmentArmor = equipmentArmorSurface,
                EmitEquipmentSkillType = !suppressCombatSurface && equipmentSkillTypeSurface,
                EmitDamageTakenModifiers = damageTakenModifierSurface,
                EmitDirectCombatStats = !bombLike,
                EmitPrimaryAttack = !suppressCombatSurface && primaryAttackOutput,
                EmitSecondaryAttack = !suppressCombatSurface && secondaryAttackSurface,
                EmitEffects = equipmentLike || combatLike || foodLike || HasAnyEffect(definition.Effects)
            };
        }
    }

    private static bool IsArmorLike(ItemDefinition definition) =>
        IsItemType(definition,
            ItemDrop.ItemData.ItemType.Helmet,
            ItemDrop.ItemData.ItemType.Chest,
            ItemDrop.ItemData.ItemType.Legs,
            ItemDrop.ItemData.ItemType.Shoulder,
            ItemDrop.ItemData.ItemType.Utility,
            ItemDrop.ItemData.ItemType.Trinket);

    private static bool IsShieldLike(ItemDefinition definition) =>
        IsItemType(definition, ItemDrop.ItemData.ItemType.Shield) ||
        IsSkillType(definition, Skills.SkillType.Blocking);

    private static bool IsToolLike(ItemDefinition definition) =>
        IsItemType(definition, ItemDrop.ItemData.ItemType.Tool, ItemDrop.ItemData.ItemType.Torch) ||
        IsSkillType(definition, Skills.SkillType.Pickaxes, Skills.SkillType.Fishing, Skills.SkillType.Farming);

    private static bool IsWeaponLike(ItemDefinition definition) =>
        IsItemType(definition,
            ItemDrop.ItemData.ItemType.OneHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft,
            ItemDrop.ItemData.ItemType.Bow,
            ItemDrop.ItemData.ItemType.Attach_Atgeir,
            ItemDrop.ItemData.ItemType.Torch) ||
        IsSkillAttack(definition,
            Skills.SkillType.Swords,
            Skills.SkillType.Axes,
            Skills.SkillType.Clubs,
            Skills.SkillType.Knives,
            Skills.SkillType.Spears,
            Skills.SkillType.Polearms,
            Skills.SkillType.Unarmed,
            Skills.SkillType.Bows,
            Skills.SkillType.Crossbows);

    private static bool IsPrimaryAttackSurface(ItemDefinition definition) =>
        IsSwordLike(definition) ||
        IsAxeLike(definition) ||
        IsClubLike(definition) ||
        IsKnifeLike(definition) ||
        IsSpearLike(definition) ||
        IsPolearmLike(definition) ||
        IsFistsLike(definition) ||
        IsShieldLike(definition) ||
        IsPickaxeLike(definition) ||
        IsToolLike(definition) ||
        IsBowLike(definition) ||
        IsCrossbowLike(definition) ||
        IsMagicLike(definition);

    private static bool IsShieldBlockSurface(ItemDefinition definition) =>
        IsSwordLike(definition) ||
        IsAxeLike(definition) ||
        IsClubLike(definition) ||
        IsKnifeLike(definition) ||
        IsSpearLike(definition) ||
        IsPolearmLike(definition) ||
        IsFistsLike(definition) ||
        IsShieldLike(definition) ||
        IsPickaxeLike(definition) ||
        IsBowLike(definition) ||
        IsCrossbowLike(definition) ||
        IsMagicLike(definition);

    private static bool IsEquipmentSkillTypeSurface(ItemDefinition definition) =>
        IsShieldBlockSurface(definition);

    private static bool IsEquipmentArmorSurface(ItemDefinition definition) =>
        IsItemType(definition,
            ItemDrop.ItemData.ItemType.Helmet,
            ItemDrop.ItemData.ItemType.Chest,
            ItemDrop.ItemData.ItemType.Legs,
            ItemDrop.ItemData.ItemType.Shoulder,
            ItemDrop.ItemData.ItemType.Utility);

    private static bool IsSwordLike(ItemDefinition definition) => IsSkillAttack(definition, Skills.SkillType.Swords);

    private static bool IsAxeLike(ItemDefinition definition) => IsSkillAttack(definition, Skills.SkillType.Axes);

    private static bool IsClubLike(ItemDefinition definition) => IsSkillAttack(definition, Skills.SkillType.Clubs);

    private static bool IsKnifeLike(ItemDefinition definition) => IsSkillAttack(definition, Skills.SkillType.Knives);

    private static bool IsSpearLike(ItemDefinition definition) => IsSkillAttack(definition, Skills.SkillType.Spears);

    private static bool IsPolearmLike(ItemDefinition definition) =>
        IsSkillAttack(definition, Skills.SkillType.Polearms) ||
        IsItemType(definition, ItemDrop.ItemData.ItemType.Attach_Atgeir);

    private static bool IsFistsLike(ItemDefinition definition) => IsSkillAttack(definition, Skills.SkillType.Unarmed);

    private static bool IsPickaxeLike(ItemDefinition definition) => IsSkillType(definition, Skills.SkillType.Pickaxes);

    private static bool IsBowLike(ItemDefinition definition) =>
        IsSkillAttack(definition, Skills.SkillType.Bows) ||
        (IsItemType(definition, ItemDrop.ItemData.ItemType.Bow) &&
         !IsSkillType(definition,
             Skills.SkillType.Crossbows,
             Skills.SkillType.ElementalMagic,
             Skills.SkillType.BloodMagic));

    private static bool IsCrossbowLike(ItemDefinition definition) =>
        !IsAmmoLike(definition) &&
        IsSkillType(definition, Skills.SkillType.Crossbows) &&
        (IsItemType(definition, ItemDrop.ItemData.ItemType.Bow) || HasAttackAnimation(definition.Combat?.PrimaryAttack));

    private static bool IsAmmoLike(ItemDefinition definition) =>
        IsItemType(definition, ItemDrop.ItemData.ItemType.Ammo, ItemDrop.ItemData.ItemType.AmmoNonEquipable);

    private static bool IsMagicLike(ItemDefinition definition) =>
        IsSkillType(definition, Skills.SkillType.ElementalMagic, Skills.SkillType.BloodMagic);

    private static bool IsFoodLike(ItemDefinition definition) =>
        IsItemType(definition, ItemDrop.ItemData.ItemType.Consumable) ||
        HasFoodStats(definition.Food) ||
        HasStatusEffectValue(definition.Effects?.ConsumeStatusEffect);

    private static bool HasDamageTakenModifiers(DamageTakenModifierDefinition? modifiers)
    {
        if (modifiers == null)
        {
            return false;
        }

        return IsNonDefaultDamageModifier(modifiers.Blunt) ||
               IsNonDefaultDamageModifier(modifiers.Slash) ||
               IsNonDefaultDamageModifier(modifiers.Pierce) ||
               IsNonDefaultDamageModifier(modifiers.Chop) ||
               IsNonDefaultDamageModifier(modifiers.Pickaxe) ||
               IsNonDefaultDamageModifier(modifiers.Fire) ||
               IsNonDefaultDamageModifier(modifiers.Frost) ||
               IsNonDefaultDamageModifier(modifiers.Lightning) ||
               IsNonDefaultDamageModifier(modifiers.Poison) ||
               IsNonDefaultDamageModifier(modifiers.Spirit);
    }

    private static bool IsNonDefaultDamageModifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value!.Trim();
        return !trimmed.Equals("Normal", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("None", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAttackSpecial(AttackDefinition? attack) =>
        attack != null &&
        (HasMissingHealth(attack.MissingHealth) ||
         HasSpawnOnTrigger(attack.SpawnOnTrigger) ||
         HasSpawnOnHit(attack.SpawnOnHit) ||
         HasProjectile(attack.Projectile));

    private static bool HasMissingHealth(string? value)
    {
        string[] parts = SplitTuple(value);
        return Math.Abs(GetTupleFloat(parts, 0)) > 0.0001f ||
               Math.Abs(GetTupleFloat(parts, 1)) > 0.0001f ||
               Math.Abs(GetTupleFloat(parts, 2)) > 0.0001f;
    }

    private static bool HasSpawnOnHit(string? value)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length == 0 || parts[0].Length == 0 || IsNone(parts[0]))
        {
            return false;
        }

        return true;
    }

    private static bool HasSpawnOnTrigger(string? value)
    {
        string? trimmed = value?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed) && !IsNone(trimmed);
    }

    private static bool HasProjectile(string? value)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length == 0 || parts[0].Length == 0 || IsNone(parts[0]))
        {
            return false;
        }

        return true;
    }

    private static bool IsSkillAttack(ItemDefinition definition, params Skills.SkillType[] skillTypes) =>
        !IsAmmoLike(definition) &&
        IsSkillType(definition, skillTypes) &&
        GetTotalDamage(definition.Combat?.Damage) > 0f;

    private static bool HasAttackAnimation(AttackDefinition? attack) =>
        !string.IsNullOrEmpty(attack?.Animation);

    private static bool IsItemType(ItemDefinition definition, params ItemDrop.ItemData.ItemType[] itemTypes)
    {
        return TryGetItemType(definition, out ItemDrop.ItemData.ItemType itemType) &&
               itemTypes.Contains(itemType);
    }

    private static bool IsSkillType(ItemDefinition definition, params Skills.SkillType[] skillTypes)
    {
        return TryGetSkillType(definition, out Skills.SkillType skillType) &&
               skillTypes.Contains(skillType);
    }

    private static bool TryGetItemType(ItemDefinition definition, out ItemDrop.ItemData.ItemType itemType)
    {
        return Enum.TryParse(definition.Basics?.ItemType, ignoreCase: true, out itemType);
    }

    private static bool TryGetSkillType(ItemDefinition definition, out Skills.SkillType skillType)
    {
        return Enum.TryParse(definition.Equipment?.SkillType, ignoreCase: true, out skillType);
    }

    private static float GetTotalDamage(DamageDefinition? damage)
    {
        if (damage == null)
        {
            return 0f;
        }

        return GetFirstTupleFloat(damage.Blunt) +
               GetFirstTupleFloat(damage.Slash) +
               GetFirstTupleFloat(damage.Pierce) +
               GetFirstTupleFloat(damage.Chop) +
               GetFirstTupleFloat(damage.Pickaxe) +
               GetFirstTupleFloat(damage.Fire) +
               GetFirstTupleFloat(damage.Frost) +
               GetFirstTupleFloat(damage.Lightning) +
               GetFirstTupleFloat(damage.Poison) +
               GetFirstTupleFloat(damage.Spirit);
    }

    private static bool HasFoodStats(string? food)
    {
        string[] parts = SplitTuple(food);
        return GetTupleFloat(parts, 0) > 0f ||
               GetTupleFloat(parts, 1) > 0f ||
               GetTupleFloat(parts, 2) > 0f;
    }

    private static bool HasDurabilityEnabled(string? durability)
    {
        string[] parts = SplitTuple(durability);
        return parts.Length > 2 &&
               bool.TryParse(parts[2], out bool useDurability) &&
               useDurability;
    }

    private static bool HasAnyEffect(EffectsDefinition? effects) =>
        effects != null &&
        (HasStatusEffectValue(effects.EquipStatusEffect) ||
         HasStatusEffectValue(effects.Set) ||
         HasStatusEffectValue(effects.ConsumeStatusEffect) ||
         HasStatusEffectValue(effects.AttackStatusEffect) ||
         HasStatusEffectValue(effects.PerfectBlockStatusEffect) ||
         HasStatusEffectValue(effects.FullAdrenalineStatusEffect));

    private static bool HasStatusEffectValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (string part in SplitTuple(value))
        {
            if (part.Length > 0 && !IsNone(part) && !float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static float GetFirstTupleFloat(string? value)
    {
        string[] parts = SplitTuple(value);
        return GetTupleFloat(parts, 0);
    }

    private static float GetTupleFloat(string[] parts, int index)
    {
        return index < parts.Length &&
               float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : 0f;
    }

    private static string? FormatReferenceAttackCost(string? value)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length == 0)
        {
            return value;
        }

        int lastIndex = parts.Length - 1;
        while (lastIndex >= 0 && IsZeroTupleFloat(parts[lastIndex]))
        {
            lastIndex--;
        }

        return lastIndex >= 0
            ? string.Join(", ", parts.Take(lastIndex + 1))
            : null;
    }

    private static bool ShouldExposeAttackDraw(string? value)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return false;
        }

        return !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
               Math.Abs(parsed) > 0.0001f;
    }

    private static bool ShouldExposeAttackReload(string? value)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return false;
        }

        return !bool.TryParse(parts[0], out bool requiresReload) || requiresReload;
    }

    private static bool ShouldExposeLastChainDamageMultiplier(PrimaryAttackDefinition? attack) =>
        attack?.ChainLevels > 1 &&
        attack.LastChainDamageMultiplier.HasValue &&
        Math.Abs(attack.LastChainDamageMultiplier.Value - 2f) > 0.0001f;

    private static bool IsZeroTupleFloat(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) &&
        Math.Abs(parsed) <= 0.0001f;

    internal sealed class ItemEntry
    {
        internal string LogContext { get; private set; } = "";
        public string Item { get; set; } = "";
        public float? AmountMultiplier { get; set; }
        public bool Override { get; set; } = true;
        public string? CloneFrom { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Subtitle { get; set; }
        public string? ItemType { get; set; }
        public string? Weight { get; set; }
        public int? Value { get; set; }
        public int? MaxStackSize { get; set; }
        public int? MaxQuality { get; set; }
        public bool? Teleportable { get; set; }
        public bool? Floating { get; set; }
        public string? Durability { get; set; }
        public EquipmentDefinition? Equipment { get; set; }
        public DamageTakenModifierDefinition? DamageTakenModifiers { get; set; }
        public string? Food { get; set; }
        public ShieldDefinition? Shield { get; set; }
        public DamageDefinition? Damage { get; set; }
        public PrimaryAttackDefinition? PrimaryAttack { get; set; }
        public AttackDefinition? SecondaryAttack { get; set; }
        public EffectsDefinition? Effects { get; set; }
        public VisualDefinition? Visual { get; set; }

        internal void SetLogContext(string value)
        {
            LogContext = value;
        }

        internal bool HasPrefabDefinition =>
            HasBasicsDefinition ||
            Durability != null ||
            Equipment != null ||
            DamageTakenModifiers != null ||
            Food != null ||
            Shield != null ||
            Damage != null ||
            PrimaryAttack != null ||
            SecondaryAttack != null ||
            Effects != null ||
            Visual != null;

        internal bool HasLiveSafeDefinition =>
            HasBasicsDefinition ||
            Durability != null ||
            Equipment != null ||
            DamageTakenModifiers != null ||
            Food != null ||
            Shield != null ||
            Damage != null ||
            PrimaryAttack != null ||
            SecondaryAttack != null ||
            Effects != null;

        internal ItemDefinition ToDefinition()
        {
            return new ItemDefinition
            {
                Basics = ToBasicsDefinition(),
                Durability = Durability,
                Equipment = Equipment,
                DamageTakenModifiers = DamageTakenModifiers,
                Food = Food,
                Shield = Shield,
                Combat = ToCombatDefinition(),
                Effects = Effects,
                Visual = Visual
            };
        }

        private CombatDefinition? ToCombatDefinition()
        {
            if (Damage == null &&
                PrimaryAttack == null &&
                SecondaryAttack == null)
            {
                return null;
            }

            return new CombatDefinition
            {
                Damage = Damage,
                BackstabBonus = Damage?.BackstabBonus,
                AttackForce = Damage?.AttackForce,
                PrimaryAttack = PrimaryAttack,
                SecondaryAttack = SecondaryAttack
            };
        }

        private BasicsDefinition? ToBasicsDefinition()
        {
            if (!HasBasicsDefinition)
            {
                return null;
            }

            return new BasicsDefinition
            {
                Name = Name,
                Description = Description,
                Subtitle = Subtitle,
                ItemType = ItemType,
                Weight = Weight,
                Value = Value,
                MaxStackSize = MaxStackSize,
                MaxQuality = MaxQuality,
                Teleportable = Teleportable,
                Floating = Floating
            };
        }

        private bool HasBasicsDefinition =>
            Name != null ||
            Description != null ||
            Subtitle != null ||
            ItemType != null ||
            Weight != null ||
            Value != null ||
            MaxStackSize != null ||
            MaxQuality != null ||
            Teleportable != null ||
            Floating != null;
    }

    internal sealed class ItemReferenceEntry
    {
        public string Item { get; set; } = "";
        public string? ItemType { get; set; }
        public string? Weight { get; set; }
        public int? Value { get; set; }
        public int? MaxStackSize { get; set; }
        public int? MaxQuality { get; set; }
        public bool? Teleportable { get; set; }
        public string? Durability { get; set; }
        public EquipmentDefinition? Equipment { get; set; }
        public DamageTakenModifierDefinition? DamageTakenModifiers { get; set; }
        public string? Food { get; set; }
        public ShieldDefinition? Shield { get; set; }
        public DamageDefinition? Damage { get; set; }
        public PrimaryAttackDefinition? PrimaryAttack { get; set; }
        public AttackDefinition? SecondaryAttack { get; set; }
        public EffectsDefinition? Effects { get; set; }

        internal static ItemReferenceEntry From(string prefab, ItemDefinition definition)
        {
            ItemOutputProfile output = ItemOutputProfile.From(definition);
            BasicsDefinition? referenceBasics = ToReferenceItem(definition.Basics);
            ItemReferenceEntry entry = new()
            {
                Item = prefab,
                Weight = referenceBasics?.Weight,
                Value = referenceBasics?.Value,
                MaxStackSize = referenceBasics?.MaxStackSize,
                Teleportable = referenceBasics?.Teleportable,
                Durability = output.EmitDurability && HasDurabilityEnabled(definition.Durability)
                    ? FormatReferenceDurability(definition.Durability)
                    : null,
                Equipment = output.EmitEquipment ? ToReferenceEquipment(definition.Equipment, output) : null,
                DamageTakenModifiers = output.EmitDamageTakenModifiers ? definition.DamageTakenModifiers : null,
                Food = output.EmitFood ? definition.Food : null,
                Shield = output.EmitShield && IsItemType(definition, ItemDrop.ItemData.ItemType.Shield) ? definition.Shield : null,
                Damage = output.EmitCombat && output.EmitDirectCombatStats ? ToReferenceDamage(definition.Combat) : null,
                PrimaryAttack = output.EmitPrimaryAttack ? ToReferencePrimaryAttack(definition.Combat?.PrimaryAttack) : null,
                SecondaryAttack = output.EmitSecondaryAttack ? ToReferenceAttack(definition.Combat?.SecondaryAttack) : null,
                Effects = output.EmitEffects ? definition.Effects : null
            };

            return ReferenceValue.ClonePruned(entry) ?? new ItemReferenceEntry { Item = prefab };
        }

        private static DamageDefinition? ToReferenceDamage(CombatDefinition? combat) =>
            combat != null ? CreateDamageOutputDefinition(combat) : null;

        private static BasicsDefinition? ToReferenceItem(BasicsDefinition? basics)
        {
            if (basics == null)
            {
                return null;
            }

            return new BasicsDefinition
            {
                Weight = IsReferenceDefaultWeight(basics.Weight) ? null : basics.Weight,
                Value = basics.Value,
                MaxStackSize = basics.MaxStackSize,
                Teleportable = basics.Teleportable
            };
        }

        private static bool IsReferenceDefaultWeight(string? value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float weight) &&
            Math.Abs(weight - 1f) <= 0.0001f;

        private static string? FormatReferenceDurability(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
                .Select(part => part.Trim())
                .ToArray();
            if (parts.Length != 7 ||
                !IsBoolPart(parts[2], true) ||
                !IsFloatPart(parts[3], 1f) ||
                !IsBoolPart(parts[4], true) ||
                !IsBoolPart(parts[5], false) ||
                !IsFloatPart(parts[6], 0f))
            {
                return value;
            }

            return string.Join(", ", parts.Take(2));
        }

        private static EquipmentDefinition? ToReferenceEquipment(EquipmentDefinition? equipment, ItemOutputProfile output)
        {
            if (equipment == null)
            {
                return null;
            }

            return new EquipmentDefinition
            {
                SkillType = output.EmitEquipmentSkillType ? equipment.SkillType : null,
                MovementModifier = equipment.MovementModifier,
                EitrRegenModifier = equipment.EitrRegenModifier,
                HeatResistanceModifier = equipment.HeatResistanceModifier,
                HomeItemsStaminaModifier = equipment.HomeItemsStaminaModifier,
                AttackStaminaModifier = equipment.AttackStaminaModifier,
                BlockStaminaModifier = equipment.BlockStaminaModifier,
                DodgeStaminaModifier = equipment.DodgeStaminaModifier,
                JumpStaminaModifier = equipment.JumpStaminaModifier,
                RunStaminaModifier = equipment.RunStaminaModifier,
                SneakStaminaModifier = equipment.SneakStaminaModifier,
                SwimStaminaModifier = equipment.SwimStaminaModifier,
                Armor = output.EmitEquipmentArmor ? equipment.Armor : null,
                MaxAdrenaline = equipment.MaxAdrenaline
            };
        }

        private static bool IsFloatPart(string value, float expected) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue) &&
            Math.Abs(parsedValue - expected) <= 0.0001f;

        private static bool IsBoolPart(string value, bool expected) =>
            bool.TryParse(value, out bool parsedValue) && parsedValue == expected;

        private static PrimaryAttackDefinition? ToReferencePrimaryAttack(PrimaryAttackDefinition? attack)
        {
            if (attack == null)
            {
                return null;
            }

            return new PrimaryAttackDefinition
            {
                Animation = attack.Animation,
                ChainLevels = attack.ChainLevels,
                Cost = FormatReferenceAttackCost(attack.Cost),
                MissingHealth = attack.MissingHealth,
                SpawnOnTrigger = attack.SpawnOnTrigger,
                SpawnOnHit = attack.SpawnOnHit,
                Projectile = attack.Projectile,
                Draw = ShouldExposeAttackDraw(attack.Draw) ? attack.Draw : null,
                Reload = ShouldExposeAttackReload(attack.Reload) ? attack.Reload : null,
                DamageMultiplier = attack.DamageMultiplier,
                ForceMultiplier = attack.ForceMultiplier,
                StaggerMultiplier = attack.StaggerMultiplier,
                LastChainDamageMultiplier = ShouldExposeLastChainDamageMultiplier(attack) ? attack.LastChainDamageMultiplier : null,
                RaiseSkillAmount = attack.RaiseSkillAmount
            };
        }

        private static AttackDefinition? ToReferenceAttack(AttackDefinition? attack)
        {
            if (attack == null)
            {
                return null;
            }

            return new AttackDefinition
            {
                Animation = attack.Animation,
                ChainLevels = attack.ChainLevels,
                Cost = FormatReferenceAttackCost(attack.Cost),
                MissingHealth = attack.MissingHealth,
                SpawnOnTrigger = attack.SpawnOnTrigger,
                SpawnOnHit = attack.SpawnOnHit,
                Projectile = attack.Projectile,
                Draw = ShouldExposeAttackDraw(attack.Draw) ? attack.Draw : null,
                Reload = ShouldExposeAttackReload(attack.Reload) ? attack.Reload : null,
                DamageMultiplier = attack.DamageMultiplier,
                ForceMultiplier = attack.ForceMultiplier,
                StaggerMultiplier = attack.StaggerMultiplier,
                RaiseSkillAmount = attack.RaiseSkillAmount
            };
        }
    }

    internal sealed class ItemDefinition
    {
        public BasicsDefinition? Basics { get; set; }
        public string? Durability { get; set; }
        public EquipmentDefinition? Equipment { get; set; }
        public DamageTakenModifierDefinition? DamageTakenModifiers { get; set; }
        public string? Food { get; set; }
        public ShieldDefinition? Shield { get; set; }
        public CombatDefinition? Combat { get; set; }
        public EffectsDefinition? Effects { get; set; }
        public VisualDefinition? Visual { get; set; }

        internal static ItemDefinition From(ItemDrop itemDrop)
        {
            ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
            return new ItemDefinition
            {
                Basics = BasicsDefinition.From(itemDrop),
                Durability = DurabilityDefinition.From(shared).ToString(),
                Equipment = EquipmentDefinition.From(shared),
                DamageTakenModifiers = DamageTakenModifierDefinition.From(shared.m_damageModifiers),
                Food = FoodDefinition.From(shared).ToString(),
                Shield = ShieldDefinition.From(shared),
                Combat = CombatDefinition.From(shared),
                Effects = EffectsDefinition.From(shared)
            };
        }
    }

    internal sealed class VisualDefinition
    {
        public string? Icon { get; set; }
        public string? IconRotation { get; set; }
        public string? Material { get; set; }
        public string? Color { get; set; }
        public float? Emission { get; set; }
    }

    internal sealed class BasicsDefinition
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Subtitle { get; set; }
        public string? ItemType { get; set; }
        public string? Weight { get; set; }
        public int? Value { get; set; }
        public int? MaxStackSize { get; set; }
        public int? MaxQuality { get; set; }
        public bool? Teleportable { get; set; }
        public bool? Floating { get; set; }

        internal static BasicsDefinition From(ItemDrop itemDrop)
        {
            ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
            return new BasicsDefinition
            {
                Name = shared.m_name,
                Description = shared.m_description,
                Subtitle = shared.m_subtitle,
                ItemType = shared.m_itemType.ToString(),
                Weight = FormatFloat(shared.m_weight),
                Value = shared.m_value,
                MaxStackSize = shared.m_maxStackSize,
                MaxQuality = shared.m_maxQuality,
                Teleportable = shared.m_teleportable,
                Floating = itemDrop.GetComponent<Floating>() != null
            };
        }
    }

    internal sealed class DurabilityDefinition
    {
        public bool? UseDurability { get; set; }
        public float? MaxDurability { get; set; }
        public float? DurabilityPerLevel { get; set; }
        public float? DurabilityDrain { get; set; }
        public float? UseDurabilityDrain { get; set; }
        public bool? CanBeRepaired { get; set; }
        public bool? DestroyBroken { get; set; }

        internal static DurabilityDefinition From(ItemDrop.ItemData.SharedData shared)
        {
            return new DurabilityDefinition
            {
                UseDurability = shared.m_useDurability,
                MaxDurability = shared.m_maxDurability,
                DurabilityPerLevel = shared.m_durabilityPerLevel,
                DurabilityDrain = shared.m_durabilityDrain,
                UseDurabilityDrain = shared.m_useDurabilityDrain,
                CanBeRepaired = shared.m_canBeReparied,
                DestroyBroken = shared.m_destroyBroken
            };
        }

        public override string ToString()
        {
            return string.Join(", ", new[]
            {
                FormatFloat(MaxDurability),
                FormatFloat(DurabilityPerLevel),
                FormatBool(UseDurability),
                FormatFloat(UseDurabilityDrain),
                FormatBool(CanBeRepaired),
                FormatBool(DestroyBroken),
                FormatFloat(DurabilityDrain)
            });
        }
    }

    private static string FormatBool(bool? value)
    {
        return (value ?? false).ToString().ToLowerInvariant();
    }

    private static string FormatFloat(float? value)
    {
        return (value ?? 0f).ToString("0.###", CultureInfo.InvariantCulture);
    }

    internal sealed class EquipmentDefinition
    {
        public string? SkillType { get; set; }
        public float? EquipDuration { get; set; }
        public float? MovementModifier { get; set; }
        public float? EitrRegenModifier { get; set; }
        public float? HeatResistanceModifier { get; set; }
        public float? HomeItemsStaminaModifier { get; set; }
        public float? AttackStaminaModifier { get; set; }
        public float? BlockStaminaModifier { get; set; }
        public float? DodgeStaminaModifier { get; set; }
        public float? JumpStaminaModifier { get; set; }
        public float? RunStaminaModifier { get; set; }
        public float? SneakStaminaModifier { get; set; }
        public float? SwimStaminaModifier { get; set; }
        public string? Armor { get; set; }
        public float? MaxAdrenaline { get; set; }

        internal static EquipmentDefinition From(ItemDrop.ItemData.SharedData shared)
        {
            return new EquipmentDefinition
            {
                SkillType = shared.m_skillType.ToString(),
                EquipDuration = shared.m_equipDuration,
                MovementModifier = shared.m_movementModifier,
                EitrRegenModifier = shared.m_eitrRegenModifier,
                HeatResistanceModifier = shared.m_heatResistanceModifier,
                HomeItemsStaminaModifier = shared.m_homeItemsStaminaModifier,
                AttackStaminaModifier = shared.m_attackStaminaModifier,
                BlockStaminaModifier = shared.m_blockStaminaModifier,
                DodgeStaminaModifier = shared.m_dodgeStaminaModifier,
                JumpStaminaModifier = shared.m_jumpStaminaModifier,
                RunStaminaModifier = shared.m_runStaminaModifier,
                SneakStaminaModifier = shared.m_sneakStaminaModifier,
                SwimStaminaModifier = shared.m_swimStaminaModifier,
                Armor = FormatFloatPair(shared.m_armor, shared.m_armorPerLevel),
                MaxAdrenaline = shared.m_maxAdrenaline
            };
        }
    }

    internal sealed class FoodDefinition
    {
        public float? Health { get; set; }
        public float? Stamina { get; set; }
        public float? Eitr { get; set; }
        public float? Regen { get; set; }
        public float? BurnTime { get; set; }

        internal static FoodDefinition From(ItemDrop.ItemData.SharedData shared)
        {
            return new FoodDefinition
            {
                Health = shared.m_food,
                Stamina = shared.m_foodStamina,
                Eitr = shared.m_foodEitr,
                Regen = shared.m_foodRegen,
                BurnTime = shared.m_foodBurnTime
            };
        }

        public override string ToString()
        {
            return string.Join(", ", new[]
            {
                FormatFloat(Health),
                FormatFloat(Stamina),
                FormatFloat(Eitr),
                FormatFloat(Regen),
                FormatFloat(BurnTime)
            });
        }
    }

    internal sealed class DamageTakenModifierDefinition
    {
        public string? Blunt { get; set; }
        public string? Slash { get; set; }
        public string? Pierce { get; set; }
        public string? Chop { get; set; }
        public string? Pickaxe { get; set; }
        public string? Fire { get; set; }
        public string? Frost { get; set; }
        public string? Lightning { get; set; }
        public string? Poison { get; set; }
        public string? Spirit { get; set; }

        internal static DamageTakenModifierDefinition From(List<HitData.DamageModPair>? mods)
        {
            HitData.DamageModifiers modifiers = new();
            if (mods != null)
            {
                modifiers.Apply(mods);
            }

            return new DamageTakenModifierDefinition
            {
                Blunt = modifiers.m_blunt.ToString(),
                Slash = modifiers.m_slash.ToString(),
                Pierce = modifiers.m_pierce.ToString(),
                Chop = modifiers.m_chop.ToString(),
                Pickaxe = modifiers.m_pickaxe.ToString(),
                Fire = modifiers.m_fire.ToString(),
                Frost = modifiers.m_frost.ToString(),
                Lightning = modifiers.m_lightning.ToString(),
                Poison = modifiers.m_poison.ToString(),
                Spirit = modifiers.m_spirit.ToString()
            };
        }
    }

    internal sealed class ShieldDefinition
    {
        public string? BlockPower { get; set; }
        public string? DeflectionForce { get; set; }
        public float? TimedBlockBonus { get; set; }

        internal static ShieldDefinition From(ItemDrop.ItemData.SharedData shared)
        {
            return new ShieldDefinition
            {
                BlockPower = FormatFloatPair(shared.m_blockPower, shared.m_blockPowerPerLevel),
                DeflectionForce = FormatFloatPair(shared.m_deflectionForce, shared.m_deflectionForcePerLevel),
                TimedBlockBonus = shared.m_timedBlockBonus
            };
        }

    }

    internal sealed class CombatDefinition
    {
        public DamageDefinition? Damage { get; set; }
        public float? BackstabBonus { get; set; }
        public float? AttackForce { get; set; }
        public PrimaryAttackDefinition? PrimaryAttack { get; set; }
        public AttackDefinition? SecondaryAttack { get; set; }
        internal bool IsBombLike { get; set; }

        internal static CombatDefinition From(ItemDrop.ItemData.SharedData shared)
        {
            return new CombatDefinition
            {
                Damage = DamageDefinition.From(shared.m_damages, shared.m_damagesPerLevel),
                BackstabBonus = shared.m_backstabBonus,
                AttackForce = shared.m_attackForce,
                PrimaryAttack = PrimaryAttackDefinition.FromPrimary(shared.m_attack),
                SecondaryAttack = AttackDefinition.From(shared.m_secondaryAttack),
                IsBombLike = IsBombProjectileAttack(shared.m_attack)
            };
        }
    }

    private static bool IsBombProjectileAttack(Attack? attack)
    {
        GameObject? projectilePrefab = attack?.m_attackProjectile;
        if (projectilePrefab == null)
        {
            return false;
        }

        return projectilePrefab.GetComponent<Projectile>() != null &&
               (attack!.m_attackType == Attack.AttackType.Projectile ||
                projectilePrefab.GetComponent<IProjectile>() != null) &&
               string.Equals(attack.m_attackAnimation, "throw_bomb", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class DamageDefinition
    {
        public string? Blunt { get; set; }
        public string? Slash { get; set; }
        public string? Pierce { get; set; }
        public string? Chop { get; set; }
        public string? Pickaxe { get; set; }
        public string? Fire { get; set; }
        public string? Frost { get; set; }
        public string? Lightning { get; set; }
        public string? Poison { get; set; }
        public string? Spirit { get; set; }
        public float? BackstabBonus { get; set; }
        public float? AttackForce { get; set; }

        internal static DamageDefinition From(HitData.DamageTypes damage, HitData.DamageTypes damagePerLevel)
        {
            return new DamageDefinition
            {
                Blunt = FormatDamagePair(damage.m_blunt, damagePerLevel.m_blunt),
                Slash = FormatDamagePair(damage.m_slash, damagePerLevel.m_slash),
                Pierce = FormatDamagePair(damage.m_pierce, damagePerLevel.m_pierce),
                Chop = FormatDamagePair(damage.m_chop, damagePerLevel.m_chop),
                Pickaxe = FormatDamagePair(damage.m_pickaxe, damagePerLevel.m_pickaxe),
                Fire = FormatDamagePair(damage.m_fire, damagePerLevel.m_fire),
                Frost = FormatDamagePair(damage.m_frost, damagePerLevel.m_frost),
                Lightning = FormatDamagePair(damage.m_lightning, damagePerLevel.m_lightning),
                Poison = FormatDamagePair(damage.m_poison, damagePerLevel.m_poison),
                Spirit = FormatDamagePair(damage.m_spirit, damagePerLevel.m_spirit)
            };
        }
    }

    private static string FormatDamagePair(float damage, float damagePerLevel)
    {
        return FormatFloatPair(damage, damagePerLevel);
    }

    private static string FormatFloatPair(float first, float second)
    {
        return $"{FormatFloat(first)}, {FormatFloat(second)}";
    }

    private static string? FormatMissingHealth(Attack attack)
    {
        if (attack == null)
        {
            return null;
        }

        return string.Join(", ", new[]
        {
            FormatFloat(attack.m_damageMultiplierPerMissingHP),
            FormatFloat(attack.m_damageMultiplierByTotalHealthMissing),
            FormatFloat(attack.m_staminaReturnPerMissingHP)
        });
    }

    private static string? FormatSpawnOnHit(Attack attack)
    {
        if (attack == null)
        {
            return null;
        }

        string prefabName = attack.m_spawnOnHit != null
            ? GetPrefabName(attack.m_spawnOnHit)
            : "None";
        return $"{prefabName}, {FormatFloat(Mathf.Clamp01(attack.m_spawnOnHitChance))}";
    }

    private static string? FormatSpawnOnTrigger(Attack attack)
    {
        if (attack == null)
        {
            return null;
        }

        return attack.m_spawnOnTrigger != null
            ? GetPrefabName(attack.m_spawnOnTrigger)
            : "None";
    }

    private static string? FormatProjectile(Attack attack)
    {
        if (attack == null)
        {
            return null;
        }

        string prefabName = attack.m_attackProjectile != null
            ? GetPrefabName(attack.m_attackProjectile)
            : "None";
        return string.Join(", ", new[]
        {
            prefabName,
            FormatFloat(attack.m_projectileVel),
            FormatFloat(attack.m_projectileVelMin),
            attack.m_projectiles.ToString(CultureInfo.InvariantCulture),
            FormatFloat(attack.m_projectileAccuracy),
            FormatFloat(attack.m_projectileAccuracyMin)
        });
    }

    internal class AttackDefinition
    {
        internal string? Animation { get; set; }
        internal int ChainLevels { get; set; }
        public string? Cost { get; set; }
        public string? MissingHealth { get; set; }
        public string? SpawnOnTrigger { get; set; }
        public string? SpawnOnHit { get; set; }
        public string? Projectile { get; set; }
        public string? Draw { get; set; }
        public string? Reload { get; set; }
        public float? DamageMultiplier { get; set; }
        public float? ForceMultiplier { get; set; }
        public float? StaggerMultiplier { get; set; }
        public float? RaiseSkillAmount { get; set; }

        internal static AttackDefinition? From(Attack attack)
        {
            if (attack == null)
            {
                return null;
            }

            return new AttackDefinition
            {
                Animation = attack.m_attackAnimation,
                ChainLevels = attack.m_attackChainLevels,
                Cost = FormatAttackCost(attack),
                MissingHealth = FormatMissingHealth(attack),
                SpawnOnTrigger = FormatSpawnOnTrigger(attack),
                SpawnOnHit = FormatSpawnOnHit(attack),
                Projectile = FormatProjectile(attack),
                Draw = FormatAttackDraw(attack),
                Reload = FormatAttackReload(attack),
                DamageMultiplier = attack.m_damageMultiplier,
                ForceMultiplier = attack.m_forceMultiplier,
                StaggerMultiplier = attack.m_staggerMultiplier,
                RaiseSkillAmount = attack.m_raiseSkillAmount
            };
        }
    }

    internal sealed class PrimaryAttackDefinition : AttackDefinition
    {
        public float? LastChainDamageMultiplier { get; set; }

        internal static PrimaryAttackDefinition? FromPrimary(Attack attack)
        {
            if (attack == null)
            {
                return null;
            }

            return new PrimaryAttackDefinition
            {
                Animation = attack.m_attackAnimation,
                ChainLevels = attack.m_attackChainLevels,
                Cost = FormatAttackCost(attack),
                MissingHealth = FormatMissingHealth(attack),
                SpawnOnTrigger = FormatSpawnOnTrigger(attack),
                SpawnOnHit = FormatSpawnOnHit(attack),
                Projectile = FormatProjectile(attack),
                Draw = FormatAttackDraw(attack),
                Reload = FormatAttackReload(attack),
                DamageMultiplier = attack.m_damageMultiplier,
                ForceMultiplier = attack.m_forceMultiplier,
                StaggerMultiplier = attack.m_staggerMultiplier,
                LastChainDamageMultiplier = attack.m_lastChainDamageMultiplier,
                RaiseSkillAmount = attack.m_raiseSkillAmount
            };
        }
    }

    private static string FormatAttackCost(Attack attack)
    {
        return string.Join(", ", new[]
        {
            FormatFloat(attack.m_attackStamina),
            FormatFloat(attack.m_attackEitr),
            FormatFloat(attack.m_attackHealth),
            FormatFloat(attack.m_attackHealthPercentage)
        });
    }

    private static string FormatAttackDraw(Attack attack)
    {
        return string.Join(", ", new[]
        {
            FormatFloat(attack.m_drawDurationMin),
            FormatFloat(attack.m_drawStaminaDrain),
            FormatFloat(attack.m_drawEitrDrain)
        });
    }

    private static string FormatAttackReload(Attack attack)
    {
        return string.Join(", ", new[]
        {
            FormatBool(attack.m_requiresReload),
            FormatFloat(attack.m_reloadTime),
            FormatFloat(attack.m_reloadStaminaDrain),
            FormatFloat(attack.m_reloadEitrDrain)
        });
    }

    internal sealed class EffectsDefinition
    {
        public string? EquipStatusEffect { get; set; }
        public string? Set { get; set; }
        public string? ConsumeStatusEffect { get; set; }
        public string? AttackStatusEffect { get; set; }
        public string? PerfectBlockStatusEffect { get; set; }
        public string? FullAdrenalineStatusEffect { get; set; }

        internal static EffectsDefinition From(ItemDrop.ItemData.SharedData shared)
        {
            return new EffectsDefinition
            {
                EquipStatusEffect = shared.m_equipStatusEffect != null ? shared.m_equipStatusEffect.name : null,
                Set = FormatSetEffect(shared.m_setName, shared.m_setSize, shared.m_setStatusEffect),
                ConsumeStatusEffect = shared.m_consumeStatusEffect != null ? shared.m_consumeStatusEffect.name : null,
                AttackStatusEffect = FormatAttackStatusEffect(shared.m_attackStatusEffect, shared.m_attackStatusEffectChance),
                PerfectBlockStatusEffect = shared.m_perfectBlockStatusEffect != null ? shared.m_perfectBlockStatusEffect.name : null,
                FullAdrenalineStatusEffect = shared.m_fullAdrenalineSE != null ? shared.m_fullAdrenalineSE.name : null
            };
        }
    }

    private static string? FormatSetEffect(string? setName, int setSize, StatusEffect? setStatusEffect)
    {
        string statusEffectName = setStatusEffect != null ? setStatusEffect.name : "";
        if (string.IsNullOrWhiteSpace(setName) && setSize <= 0 && string.IsNullOrWhiteSpace(statusEffectName))
        {
            return null;
        }

        return $"{setName ?? ""}, {Math.Max(0, setSize).ToString(CultureInfo.InvariantCulture)}, {statusEffectName}";
    }

    private static string? FormatAttackStatusEffect(StatusEffect? statusEffect, float chance)
    {
        string statusEffectName = statusEffect != null ? statusEffect.name : "";
        if (string.IsNullOrWhiteSpace(statusEffectName))
        {
            return null;
        }

        return $"{statusEffectName}, {FormatFloat(Mathf.Clamp01(chance))}";
    }

}

[HarmonyPatch(typeof(ItemDrop), "Awake")]
internal static class DataForgeItemDropAwakePatch
{
    private static void Postfix(ItemDrop __instance)
    {
        ItemOverrideManager.RepairDropPrefab(__instance);
    }
}

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.DropItem))]
internal static class DataForgeItemDropDropItemPatch
{
    private static void Prefix(ItemDrop.ItemData item)
    {
        ItemOverrideManager.RepairDropPrefab(item);
    }

    private static void Postfix(ItemDrop __result)
    {
        if (__result == null)
        {
            return;
        }

        ItemOverrideManager.RepairDropPrefab(__result);
        if (__result.gameObject != null &&
            !__result.gameObject.activeSelf &&
            ItemOverrideManager.IsCreatedCloneDrop(__result))
        {
            __result.gameObject.SetActive(true);
        }
    }
}
