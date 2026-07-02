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
using UnityEngine.Rendering;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static DataForge.DataForgeValue;

namespace DataForge;

internal static class StatusEffectOverrideManager
{
    private const string DomainName = "effects";
    private const string OverrideFileName = "effects.yml";
    private const string ReferenceFileName = "effects.reference.yml";
    private const string FullScaffoldFileName = "effects.full.yml";
    private const string SyncedPayloadKey = "effects";
    private const string ItemIconPrefix = "item:";
    private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;
    private const string ReferenceStateKey = "effects";
    private const string ReferenceLogicVersion = "2026-06-24-effect-reference-state-v2";

    private static readonly object StateLock = new();
    private static readonly Dictionary<string, StatusEffectDefinition> Baselines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, StatusEffect> BaselineEffects = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Sprite?> BaselineIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, EffectList> BaselineStartEffects = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, EffectList> BaselineStopEffects = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IconCacheEntry> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CreatedClones = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RuntimeAppliedEffectKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly MethodInfo? LoadImageMethod = ResolveLoadImageMethod();
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

    private static List<StatusEffectEntry> ActiveEntries = new();
    private static Dictionary<string, string> ActiveEntrySignaturesByEffect = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string>? PendingChangedEffectKeys;
    private static bool HasPendingScopedApply;
    private static bool ForceNextFullApply = true;
    private static CustomSyncedValue<string>? SyncedPayload;
    private static string? LastAppliedSyncedPayload;
    private static FileSystemWatcher? Watcher;
    private static DataForgeFileWatcher.DebouncedAction? ReloadDebouncer;
    private static bool ObjectDbReady;
    private static bool ZNetSceneReady;
    private static bool RuntimeStateWasApplied;

    private static string ConfigDirectory => Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName);
    private static string IconDirectory => Path.Combine(ConfigDirectory, "icon");

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
        List<StatusEffectEntry> entries = LoadEntriesFromDisk();
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
        ApplyCurrentConfiguration();
    }

    internal static void ApplyCurrentConfiguration()
    {
        if (!ObjectDbReady ||
            !ZNetSceneReady ||
            !DataForgeWorldLifecycle.IsGameStarted ||
            ObjectDB.instance == null)
        {
            return;
        }

        if (ShouldSkipRemoteClientBaselineWork())
        {
            return;
        }

        List<StatusEffectEntry> entries;
        HashSet<string>? changedEffectKeys;
        lock (StateLock)
        {
            entries = ActiveEntries.ToList();
            changedEffectKeys = ConsumePendingChangedEffectKeys();
        }

        if (changedEffectKeys is { Count: 0 })
        {
            return;
        }

        List<StatusEffectEntry> entriesToApply = FilterEntries(entries, changedEffectKeys);
        Dictionary<string, List<StatusEffectEntry>> entriesByEffect = BuildEnabledDefinitionEntriesByEffect(entriesToApply);
        HashSet<string> runtimeEffectKeys = GetRuntimeApplyEffectKeys(entriesByEffect);

        DataForgeStatusEffectOwnership.NotifyStatusEffectOverridesWillApply();
        CaptureBaselinesForEntriesIfNeeded(entriesToApply);
        CleanupCreatedEffects(entries);
        EnsureCloneEffects(entries);
        RestoreBaselineEffects(runtimeEffectKeys);

        if (!DataForgePlugin.StatusEffectOverridesEnabled)
        {
            ApplyLiveSafeToActiveStatusEffects(entriesByEffect);
            UpdateRuntimeAppliedEffectState(new Dictionary<string, List<StatusEffectEntry>>(StringComparer.OrdinalIgnoreCase));
            DataForgeStatusEffectOwnership.NotifyStatusEffectOverridesApplied();
            return;
        }

        foreach (StatusEffectEntry entry in entriesToApply)
        {
            using (DataForgeLogContext.Push(entry.LogContext))
            {
                if (!entry.Override || !entry.HasDefinition)
                {
                    continue;
                }

                StatusEffect? statusEffect = ResolveStatusEffect(entry.Effect);
                if (statusEffect == null)
                {
                    DataForgeLogContext.Warning($"Could not find status effect '{entry.Effect}'.");
                    continue;
                }

                ApplyDefinition(statusEffect, entry.ToDefinition());
            }
        }

        ApplyLiveSafeToActiveStatusEffects(entriesByEffect);
        UpdateRuntimeAppliedEffectState(entriesByEffect);
        DataForgeStatusEffectOwnership.NotifyStatusEffectOverridesApplied();
    }

    internal static bool HasActiveStatusEffectOverride(string? effectName)
    {
        if (!DataForgePlugin.StatusEffectOverridesEnabled || string.IsNullOrWhiteSpace(effectName))
        {
            return false;
        }

        string normalized = NormalizeStatusEffectName(Utils.GetPrefabName(effectName));
        if (normalized.Length == 0)
        {
            return false;
        }

        lock (StateLock)
        {
            return ActiveEntries.Any(entry =>
                entry.Override &&
                entry.HasDefinition &&
                string.Equals(NormalizeStatusEffectName(entry.Effect), normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static void RefreshItemIconReferences()
    {
        if (!DataForgePlugin.StatusEffectOverridesEnabled || ObjectDB.instance == null)
        {
            return;
        }

        List<StatusEffectEntry> entries;
        lock (StateLock)
        {
            entries = ActiveEntries
                .Where(entry => entry.Override && IsItemIconReference(entry.Icon))
                .ToList();
        }

        if (entries.Count == 0)
        {
            return;
        }

        foreach (StatusEffectEntry entry in entries)
        {
            using (DataForgeLogContext.Push(entry.LogContext))
            {
                StatusEffect? statusEffect = ResolveStatusEffect(entry.Effect);
                if (statusEffect == null)
                {
                    continue;
                }

                ApplyBaseIcon(statusEffect, entry.Icon, warnMissingItemIcon: true);
            }
        }

        RefreshActiveItemIconReferences(entries);
    }

    private static bool ShouldSkipRemoteClientBaselineWork()
    {
        if (!DataForgePlugin.IsRemoteServerClient)
        {
            return false;
        }

        lock (StateLock)
        {
            return ActiveEntries.Count == 0 && CreatedClones.Count == 0;
        }
    }

    private static Dictionary<string, List<StatusEffectEntry>> BuildEnabledDefinitionEntriesByEffect(List<StatusEffectEntry> entries)
    {
        Dictionary<string, List<StatusEffectEntry>> entriesByEffect = new(StringComparer.OrdinalIgnoreCase);
        foreach (StatusEffectEntry entry in entries)
        {
            if (!entry.Override || !entry.HasDefinition || string.IsNullOrWhiteSpace(entry.Effect))
            {
                continue;
            }

            if (!entriesByEffect.TryGetValue(entry.Effect, out List<StatusEffectEntry> effectEntries))
            {
                effectEntries = new List<StatusEffectEntry>();
                entriesByEffect[entry.Effect] = effectEntries;
            }

            effectEntries.Add(entry);
        }

        return entriesByEffect;
    }

    private static HashSet<string> GetRuntimeApplyEffectKeys(Dictionary<string, List<StatusEffectEntry>> entriesByEffect)
    {
        HashSet<string> keys = new(entriesByEffect.Keys, StringComparer.OrdinalIgnoreCase);
        if (RuntimeStateWasApplied)
        {
            foreach (string key in RuntimeAppliedEffectKeys)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static void UpdateRuntimeAppliedEffectState(Dictionary<string, List<StatusEffectEntry>> entriesByEffect)
    {
        RuntimeAppliedEffectKeys.Clear();
        foreach (string key in entriesByEffect.Keys)
        {
            RuntimeAppliedEffectKeys.Add(key);
        }

        RuntimeStateWasApplied = RuntimeAppliedEffectKeys.Count > 0;
    }

    private static void ApplyLiveSafeToActiveStatusEffects(Dictionary<string, List<StatusEffectEntry>> entriesByEffect)
    {
        if (Player.s_players == null)
        {
            return;
        }

        int applied = 0;
        foreach (Player player in Player.s_players)
        {
            if (player == null)
            {
                continue;
            }

            SEMan seMan = player.GetSEMan();
            if (seMan == null)
            {
                continue;
            }

            foreach (StatusEffect statusEffect in seMan.GetStatusEffects())
            {
                if (statusEffect == null)
                {
                    continue;
                }

                if (ApplyLiveSafeToActiveStatusEffect(statusEffect, entriesByEffect))
                {
                    applied++;
                }
            }
        }

        if (applied > 0)
        {
            DataForgePlugin.Log.LogDebug($"Live-refreshed {applied} active status effect instances.");
        }
    }

    private static void RefreshActiveItemIconReferences(List<StatusEffectEntry> entries)
    {
        if (Player.s_players == null)
        {
            return;
        }

        Dictionary<string, StatusEffectEntry> entriesByEffect = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Effect))
            .GroupBy(entry => NormalizeStatusEffectName(entry.Effect), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        if (entriesByEffect.Count == 0)
        {
            return;
        }

        foreach (Player player in Player.s_players)
        {
            if (player == null)
            {
                continue;
            }

            SEMan seMan = player.GetSEMan();
            if (seMan == null)
            {
                continue;
            }

            foreach (StatusEffect statusEffect in seMan.GetStatusEffects())
            {
                if (statusEffect == null)
                {
                    continue;
                }

                string statusEffectName = NormalizeStatusEffectName(statusEffect.name);
                if (entriesByEffect.TryGetValue(statusEffectName, out StatusEffectEntry entry))
                {
                    using (DataForgeLogContext.Push(entry.LogContext))
                    {
                        ApplyBaseIcon(statusEffect, entry.Icon, warnMissingItemIcon: false);
                    }
                }
            }
        }
    }

    private static bool ApplyLiveSafeToActiveStatusEffect(StatusEffect statusEffect, Dictionary<string, List<StatusEffectEntry>> entriesByEffect)
    {
        string statusEffectName = NormalizeStatusEffectName(statusEffect.name);
        if (statusEffectName.Length == 0)
        {
            return false;
        }

        bool applied = false;
        if (Baselines.TryGetValue(statusEffectName, out StatusEffectDefinition? baseline))
        {
            ApplyLiveSafeDefinition(statusEffect, baseline);
            if (BaselineIcons.TryGetValue(statusEffectName, out Sprite? baselineIcon))
            {
                statusEffect.m_icon = baselineIcon;
            }

            applied = true;
        }

        if (!DataForgePlugin.StatusEffectOverridesEnabled)
        {
            return applied;
        }

        if (!entriesByEffect.TryGetValue(statusEffectName, out List<StatusEffectEntry> entries))
        {
            return applied;
        }

        foreach (StatusEffectEntry entry in entries)
        {
            using (DataForgeLogContext.Push(entry.LogContext))
            {
                ApplyLiveSafeDefinition(statusEffect, entry.ToDefinition());
            }
            applied = true;
        }

        return applied;
    }

    private static string NormalizeStatusEffectName(string statusEffectName)
    {
        return (statusEffectName ?? "").Replace("(Clone)", "").Trim();
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
            DataForgePlugin.Log.LogDebug("Reloading status effect YAML files...");
            ReloadFromDiskAndSync();
            DataForgePlugin.Log.LogInfo("Status effect YAML reload complete.");
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Error reloading status effect YAML files: {ex}");
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

        if (IsIconFile(e.FullPath))
        {
            return true;
        }

        return e is RenamedEventArgs renamed &&
               (IsOverrideFile(renamed.OldFullPath) || IsIconFile(renamed.OldFullPath));
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
        List<StatusEffectEntry> entries = DeserializeEntries(payload, "synced status effect payload");
        lock (StateLock)
        {
            SetActiveEntries(entries);
        }

        ApplyCurrentConfiguration();
    }

    private static void SetActiveEntries(List<StatusEffectEntry> entries)
    {
        Dictionary<string, string> signatures = BuildEntrySignaturesByEffect(entries);
        if (!ForceNextFullApply)
        {
            PendingChangedEffectKeys = GetChangedKeys(ActiveEntrySignaturesByEffect, signatures);
            HasPendingScopedApply = true;
        }

        ActiveEntries = entries;
        ActiveEntrySignaturesByEffect = signatures;
    }

    private static HashSet<string>? ConsumePendingChangedEffectKeys()
    {
        if (ForceNextFullApply)
        {
            ForceNextFullApply = false;
            PendingChangedEffectKeys = null;
            HasPendingScopedApply = false;
            return null;
        }

        if (!HasPendingScopedApply)
        {
            return null;
        }

        HashSet<string> changedKeys = PendingChangedEffectKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PendingChangedEffectKeys = null;
        HasPendingScopedApply = false;
        return changedKeys;
    }

    private static Dictionary<string, string> BuildEntrySignaturesByEffect(List<StatusEffectEntry> entries)
    {
        Dictionary<string, List<StatusEffectEntry>> entriesByEffect = new(StringComparer.OrdinalIgnoreCase);
        foreach (StatusEffectEntry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Effect))
            {
                continue;
            }

            if (!entriesByEffect.TryGetValue(entry.Effect, out List<StatusEffectEntry> effectEntries))
            {
                effectEntries = new List<StatusEffectEntry>();
                entriesByEffect[entry.Effect] = effectEntries;
            }

            effectEntries.Add(entry);
        }

        Dictionary<string, string> signatures = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<StatusEffectEntry>> pair in entriesByEffect)
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

    private static List<StatusEffectEntry> FilterEntries(List<StatusEffectEntry> entries, HashSet<string>? effectKeys)
    {
        return effectKeys == null
            ? entries
            : entries.Where(entry => effectKeys.Contains(entry.Effect)).ToList();
    }

    private static void PublishPayload(string payload)
    {
        DataForgeSync.PublishPayload(SyncedPayload, DomainName, payload);
    }

    private static List<StatusEffectEntry> LoadEntriesFromDisk()
    {
        return DataForgeOverrideFiles.LoadEntries(GetOverrideFiles(), DeserializeEntries);
    }

    private static List<StatusEffectEntry> DeserializeEntries(string yaml, string source)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new List<StatusEffectEntry>();
        }

        try
        {
            List<StatusEffectEntry>? entries = Deserializer.Deserialize<List<StatusEffectEntry>>(yaml);
            return NormalizeEntries(entries, source);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Failed to parse {source}: {ex.Message}");
            return new List<StatusEffectEntry>();
        }
    }

    private static List<StatusEffectEntry> NormalizeEntries(List<StatusEffectEntry>? entries, string source)
    {
        List<StatusEffectEntry> normalized = new();
        if (entries == null)
        {
            return normalized;
        }

        int entryIndex = 0;
        foreach (StatusEffectEntry entry in entries)
        {
            entryIndex++;
            string sourceContext = DataForgeLogContext.FormatSource(source, entryIndex);
            if (string.IsNullOrWhiteSpace(entry.Effect))
            {
                DataForgeLogContext.Warning($"{sourceContext}: Skipping status effect entry without effect.");
                continue;
            }

            entry.Effect = entry.Effect.Trim();
            entry.SetLogContext($"{sourceContext} effect={entry.Effect}");
            string? cloneFrom = entry.CloneFrom;
            if (cloneFrom != null && cloneFrom.Trim().Length > 0)
            {
                entry.CloneFrom = cloneFrom.Trim();
            }

            normalized.Add(entry);
        }

        return normalized;
    }

    private static string SerializeEntries(List<StatusEffectEntry> entries)
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
               fileName.StartsWith("effects_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIconFile(string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        string iconRoot = Path.GetFullPath(IconDirectory);
        return fullPath.StartsWith(iconRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureConfigDirectoryAndDefaultOverride()
    {
        Directory.CreateDirectory(IconDirectory);
        DataForgeOverrideFiles.EnsureDefaultOverride(ConfigDirectory, OverrideFileName, GetOverrideFiles, DefaultOverrideTemplate);
    }

    private static string DefaultOverrideTemplate()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# DataForge effect overrides.",
            "# Copy entries from effects.reference.yml, or run `dataforge:full effect` to generate effects.full.yml for exhaustive field examples.",
            "# You can also create additional override files like effects_asdf.yml; DataForge loads effects.yml and effects_*.yml together.",
            "# Omitted fields keep the current status effect value. Inline comments show example values and their effect.",
            "#",
            "# Schema:",
            "# - effect: Rested                       # e.g. Rested => override the Rested status effect.",
            "#   override: true                       # default true; false skips this entire status effect entry.",
            "#   cloneFrom: Rested                     # MyRested, cloneFrom: Rested => create a new effect from Rested.",
            "#   displayName: $se_rested_name          # $se_custom_name => use this localization token or literal text.",
            "#   tooltip: $se_rested_tooltip           # $se_custom_tooltip => use this tooltip token or literal text.",
            "#   icon: MyEffectIcon                    # MyEffectIcon => load DataForge/icon/MyEffectIcon.png; item:MeadHealthMinor => reuse that item icon; 256x256 PNG recommended.",
            "#   startEffects: vfx_A, sfx_A            # vfx_A, sfx_A => replace start EffectList with these prefabs; None => clear.",
            "#   stopEffects: vfx_B, sfx_B             # vfx_B, sfx_B => replace stop EffectList with these prefabs; None => clear.",
            "#   category:                             # food => group/category string shown by game logic.",
            "#   time: 0, 0                            # 600, 30 => lasts 600s, then cannot reapply for 30s.",
            "#   iconFlags: false, false               # true, false => show cooldown-style icon, do not flash the icon.",
            "#   repeatInterval: 0                     # 5 => repeat message/tick every 5 seconds.",
            "#   startMessage:                         # You feel strong => shown when the effect starts.",
            "#   stopMessage:                          # Strength fades => shown when the effect ends.",
            "#   repeatMessage:                        # Still strong => shown each repeatInterval.",
            "#   startMessageType: TopLeft             # Center => show startMessage in the center.",
            "#   stopMessageType: TopLeft              # TopLeft => show stopMessage in the top-left feed.",
            "#   repeatMessageType: TopLeft            # TopLeft => show repeatMessage in the top-left feed.",
            "#   attributes: None                      # ColdResistance => grant that StatusAttribute flag.",
            "#   stats:",
            "#     upFront: 0, 0, 0, 0                 # 50, 40, 25, 20 => instantly give 50 health, 40 stamina, 25 eitr, 20 adrenaline on start.",
            "#     healthPerTick: 0, 0, Undefined      # -5, 0.1, Poison => deal 5 Poison damage each existing effect tick, stopping below 10% health; hitType is mostly meaningful for damage, not healing.",
            "#     healthOverTime: 0, 0, 5             # 100, 20, 5 => heal 100 total health over 20s, every 5s.",
            "#     staminaOverTime: 0, 0, false        # 60, 10, false => restore 60 stamina over 10s; 0.5, 10, true => restore 50% max stamina over 10s.",
            "#     eitrOverTime: 0, 0                  # 60, 6 => restore 60 eitr over 6s.",
            "#     regenMultiplier: 1, 1, 1            # 1.5, 0.5, 0 => health regen +50%, stamina regen x0.5, eitr regen disabled.",
            "#     staminaDrainPerSec: 0               # 2 => drain 2 stamina per second.",
            "#     adrenalineModifier: 0               # 0.25 => adrenaline gain/use value +25%.",
            "#     speedModifier: 0                    # 0.2 => movement speed +20%.",
            "#     swimSpeedModifier: 0                # 0.3 => swim speed +30%.",
            "#     jumpModifier: 0, 0, 0               # 0, 0.25, 0 => jump height +25%; 0.2, 0, 0.2 => jump distance +20%.",
            "#     windRun: 0, 0                       # 0.2, -0.25 => tailwind movement speed up to +20%, run stamina drain up to -25%.",
            "#     sneak: 0, 0                         # 0.25, -0.5 => stealth +25%, generated noise -50%; not sneak stamina cost.",
            "#     fall: 0, 0                          # -0.5, 8 => fall damage -50%, cap downward fall speed to 8 m/s.",
            "#     armor: 0, 0                         # 10, 0.25 => (armor + 10) * 1.25.",
            "#     block: 0, 0                         # 0.5, -5 => timed block/parry bonus +50%, block stamina cost -5 flat.",
            "#     staggerModifier: 0                  # -0.25 => stagger taken -25%.",
            "#     addMaxCarryWeight: 0                # 100 => carry weight +100.",
            "#     Skill values for attackDamage/raiseSkill/skillLevel/skillLevel2: None, Swords, Knives, Clubs, Polearms, Spears, Blocking, Axes, Bows, ElementalMagic, BloodMagic, Unarmed, Pickaxes, WoodCutting, Crossbows, Jump, Sneak, Run, Swim, Fishing, Cooking, Farming, Crafting, Dodge, Ride, All.",
            "#     attackDamage: None, 1               # Swords, 1.25 => Swords attacks deal x1.25 (*125%) total damage; tooltip uses $df_se_tooltip_attack_damage.",
            "#     raiseSkill: None, 0                 # Swords, 1.0 => Swords skill XP gain +100%; tooltip uses $df_se_tooltip_raise_skill.",
            "#     skillLevel: None, 0                 # Swords, 15 => treat Swords as +15 levels while active.",
            "#     skillLevel2: None, 0                # Blocking, 10 => treat Blocking as +10 levels while active.",
            "#   staminaDrainModifier:",
            "#     run: 0                              # -0.25 => running stamina drain -25%.",
            "#     attack: 0                           # -0.2 => attack stamina cost -20%.",
            "#     block: 0                            # -0.2 => block stamina cost -20%.",
            "#     dodge: 0                            # -0.2 => dodge stamina cost -20%.",
            "#     jump: 0                             # -0.2 => jump stamina cost -20%.",
            "#     sneak: 0                            # -0.2 => sneak stamina cost -20%.",
            "#     swim: 0                             # -0.2 => swim stamina cost -20%.",
            "#     homeItem: 0                         # -0.2 => hammer/build stamina cost -20%.",
            "#   damageTakenModifiers:",
            "#     blunt: Normal                       # Resistant => take 50% blunt damage; Normal => remove this effect's blunt modifier.",
            "#     slash: Normal                       # Weak => take 150% slash damage.",
            "#     pierce: Normal                      # VeryResistant => take 25% pierce damage.",
            "#     chop: Normal                        # SlightlyWeak => take 125% chop damage.",
            "#     pickaxe: Normal                     # Immune => take 0 pickaxe damage.",
            "#     fire: Normal                        # Resistant => take 50% fire damage.",
            "#     frost: Normal                       # VeryResistant => take 25% frost damage.",
            "#     lightning: Normal                   # SlightlyResistant => take 75% lightning damage.",
            "#     poison: Normal                      # Weak => take 150% poison damage.",
            "#     spirit: Normal                      # Immune => take 0 spirit damage.",
            "#   percentageDamageModifiers:",
            "#     blunt: 0                            # 0.25 => blunt damage modifier +25%.",
            "#     slash: 0                            # 0.25 => slash damage modifier +25%.",
            "#     pierce: 0                           # 0.25 => pierce damage modifier +25%.",
            "#     chop: 0                             # 0.25 => chop damage modifier +25%.",
            "#     pickaxe: 0                          # 0.25 => pickaxe damage modifier +25%.",
            "#     fire: 0                             # 0.25 => fire damage modifier +25%.",
            "#     frost: 0                            # 0.25 => frost damage modifier +25%.",
            "#     lightning: 0                        # 0.25 => lightning damage modifier +25%.",
            "#     poison: 0                           # 0.25 => poison damage modifier +25%.",
            "#     spirit: 0                           # 0.25 => spirit damage modifier +25%.",
            "#   poison:",
            "#     baseTtl: 0                          # 10 => poison lasts at least 10 seconds.",
            "#     damageInterval: 0                   # 1 => poison ticks every second.",
            "#     damagePerHit: 0                     # 5 => poison deals 5 damage per tick/hit.",
            "#     ttlPerDamage: 0                     # 0.2 => poison duration scales by damage against non-players.",
            "#     ttlPerDamagePlayer: 0               # 0.1 => poison duration scales by damage against players.",
            "#     ttlPower: 0                         # 0.5 => apply duration power curve.",
            "#   shield:",
            "#     absorbDamage: 0                     # 200 => shield absorbs 200 damage.",
            "#     absorbDamagePerSkillLevel: 0        # 2 => absorb +2 per relevant skill level.",
            "#     absorbDamageWorldLevel: 0           # 50 => absorb +50 per world level.",
            "#     ttlPerItemLevel: 0                  # 5 => duration +5 seconds per item level.",
            "#     levelUpSkillFactor: 0               # 0.5 => shield use grants skill XP at 50% factor.",
            "#     levelUpSkillOnBreak: None           # BloodMagic => raise BloodMagic when shield breaks.",
            "#   frost:",
            "#     freezeTimeEnemy: 0                  # 5 => enemies freeze for 5 seconds.",
            "#     freezeTimePlayer: 0                 # 2 => players freeze for 2 seconds.",
            "#     minSpeedFactor: 0                   # 0.2 => slowed target keeps at least 20% speed.",
            "#   rested:",
            "#     baseTtl: 0                          # 480 => rested starts at 8 minutes.",
            "#     ttlPerComfortLevel: 0               # 60 => each comfort level adds 60 seconds.",
            "#   healthUpgrade:",
            "#     moreHealth: 0                       # 25 => max/current health +25 while active.",
            "#     moreStamina: 0                      # 25 => max/current stamina +25 while active.",
            "#",
            "# Example:",
            "# - effect: Rested",
            "#   time: 600, 0",
            "#   stats:",
            "#     regenMultiplier: 1, 1.5, 1",
            "#     raiseSkill: Swords, 1.0",
            "#     attackDamage: Swords, 1.25",
            "#   staminaDrainModifier:",
            "#     run: -0.25"
        }) + Environment.NewLine;
    }

    private static bool CaptureAllBaselinesIfNeeded()
    {
        if (ObjectDB.instance == null)
        {
            return false;
        }

        int added = 0;
        int refreshed = 0;
        foreach (StatusEffect statusEffect in ObjectDB.instance.m_StatusEffects)
        {
            CaptureBaseline(statusEffect, out bool baselineAdded, out bool baselineRefreshed);
            added += baselineAdded ? 1 : 0;
            refreshed += baselineRefreshed ? 1 : 0;
        }

        if (added > 0 || refreshed > 0)
        {
            DataForgePlugin.Log.LogInfo($"Captured {added} new status effect baselines. Tracking {Baselines.Count} total.");
        }

        return added > 0;
    }

    private static void CaptureBaselinesForEntriesIfNeeded(List<StatusEffectEntry> entries)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        int added = 0;
        int refreshed = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (StatusEffectEntry entry in entries)
        {
            if (!entry.Override || string.IsNullOrWhiteSpace(entry.Effect) || !seen.Add(entry.Effect))
            {
                continue;
            }

            StatusEffect? statusEffect = ResolveStatusEffect(entry.Effect);
            CaptureBaseline(statusEffect, out bool baselineAdded, out bool baselineRefreshed);
            added += baselineAdded ? 1 : 0;
            refreshed += baselineRefreshed ? 1 : 0;
        }

        if (added > 0 || refreshed > 0)
        {
            DataForgePlugin.Log.LogInfo($"Captured {added} targeted status effect baselines. Tracking {Baselines.Count} total.");
        }
    }

    private static void CaptureBaseline(StatusEffect? statusEffect, out bool added, out bool refreshed)
    {
        added = false;
        refreshed = false;
        if (statusEffect == null || string.IsNullOrWhiteSpace(statusEffect.name))
        {
            return;
        }

        string statusEffectName = statusEffect.name.Trim();
        if (!Baselines.ContainsKey(statusEffectName))
        {
            Baselines[statusEffectName] = StatusEffectDefinition.From(statusEffect);
            BaselineIcons[statusEffectName] = statusEffect.m_icon;
            BaselineStartEffects[statusEffectName] = CloneEffectList(statusEffect.m_startEffects);
            BaselineStopEffects[statusEffectName] = CloneEffectList(statusEffect.m_stopEffects);
            added = true;
        }

        if (!BaselineEffects.TryGetValue(statusEffectName, out StatusEffect? existing) ||
            !ReferenceEquals(existing, statusEffect))
        {
            BaselineEffects[statusEffectName] = statusEffect;
            BaselineStartEffects[statusEffectName] = CloneEffectList(statusEffect.m_startEffects);
            BaselineStopEffects[statusEffectName] = CloneEffectList(statusEffect.m_stopEffects);
            refreshed = true;
        }
    }

    private static void CleanupCreatedEffects(List<StatusEffectEntry> entries)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        HashSet<string> activeCloneNames = new(
            entries
                .Where(entry => entry.Override && !string.IsNullOrWhiteSpace(entry.CloneFrom))
                .Select(entry => entry.Effect),
            StringComparer.OrdinalIgnoreCase);

        foreach (string cloneName in CreatedClones.ToList())
        {
            if (activeCloneNames.Contains(cloneName))
            {
                continue;
            }

            RemoveCreatedEffectClone(cloneName, destroy: false);
        }
    }

    internal static void CleanupCreatedClonesForWorldTransition()
    {
        if (ObjectDB.instance == null)
        {
            CreatedClones.Clear();
            return;
        }

        foreach (string cloneName in CreatedClones.ToList())
        {
            RemoveCreatedEffectClone(cloneName, destroy: true);
        }
    }

    internal static void OnWorldShutdown()
    {
        ObjectDbReady = false;
        ZNetSceneReady = false;
        RuntimeStateWasApplied = false;
        RuntimeAppliedEffectKeys.Clear();
        CleanupCreatedClonesForWorldTransition();
    }

    private static void RemoveCreatedEffectClone(string cloneName, bool destroy)
    {
        StatusEffect? clone = ResolveStatusEffect(cloneName);
        if (clone != null && ObjectDB.instance != null)
        {
            ObjectDB.instance.m_StatusEffects.Remove(clone);
            if (destroy)
            {
                UnityEngine.Object.Destroy(clone);
            }
        }

        CreatedClones.Remove(cloneName);
        Baselines.Remove(cloneName);
        BaselineEffects.Remove(cloneName);
        BaselineIcons.Remove(cloneName);
        BaselineStartEffects.Remove(cloneName);
        BaselineStopEffects.Remove(cloneName);
    }

    private static void EnsureCloneEffects(List<StatusEffectEntry> entries)
    {
        foreach (StatusEffectEntry entry in entries)
        {
            if (!entry.Override || string.IsNullOrWhiteSpace(entry.CloneFrom))
            {
                continue;
            }

            using (DataForgeLogContext.Push(entry.LogContext))
            {
                EnsureCloneEffect(entry);
            }
        }
    }

    private static void EnsureCloneEffect(StatusEffectEntry entry)
    {
        if (ObjectDB.instance == null)
        {
            return;
        }

        StatusEffect? existing = ResolveStatusEffect(entry.Effect);
        if (existing != null)
        {
            CreatedClones.Add(entry.Effect);
            return;
        }

        if (CreatedClones.Contains(entry.Effect))
        {
            CreatedClones.Remove(entry.Effect);
            Baselines.Remove(entry.Effect);
            BaselineEffects.Remove(entry.Effect);
            BaselineIcons.Remove(entry.Effect);
            BaselineStartEffects.Remove(entry.Effect);
            BaselineStopEffects.Remove(entry.Effect);
        }

        StatusEffect? source = ResolveStatusEffect(entry.CloneFrom);
        if (source == null)
        {
            DataForgeLogContext.Warning($"Could not clone status effect '{entry.Effect}': source '{entry.CloneFrom}' was not found.");
            return;
        }

        StatusEffect clone = UnityEngine.Object.Instantiate(source);
        clone.name = entry.Effect;
        clone.m_nameHash = entry.Effect.GetStableHashCode();
        UnityEngine.Object.DontDestroyOnLoad(clone);

        ObjectDB.instance.m_StatusEffects.Add(clone);
        Baselines[entry.Effect] = StatusEffectDefinition.From(clone);
        BaselineEffects[entry.Effect] = clone;
        BaselineIcons[entry.Effect] = clone.m_icon;
        BaselineStartEffects[entry.Effect] = CloneEffectList(clone.m_startEffects);
        BaselineStopEffects[entry.Effect] = CloneEffectList(clone.m_stopEffects);
        CreatedClones.Add(entry.Effect);
        DataForgePlugin.Log.LogInfo($"Cloned status effect '{entry.CloneFrom}' as '{entry.Effect}'.");
    }

    private static void RestoreBaselineEffects(IReadOnlyCollection<string> effectNames)
    {
        foreach (string effectName in effectNames)
        {
            if (!BaselineEffects.TryGetValue(effectName, out StatusEffect? statusEffect) ||
                !Baselines.TryGetValue(effectName, out StatusEffectDefinition? baseline))
            {
                continue;
            }

            ApplyDefinition(statusEffect, baseline, applyEffectLists: false);
            if (BaselineIcons.TryGetValue(effectName, out Sprite? baselineIcon))
            {
                statusEffect.m_icon = baselineIcon;
            }

            if (BaselineStartEffects.TryGetValue(effectName, out EffectList? startEffects))
            {
                statusEffect.m_startEffects = CloneEffectList(startEffects);
            }

            if (BaselineStopEffects.TryGetValue(effectName, out EffectList? stopEffects))
            {
                statusEffect.m_stopEffects = CloneEffectList(stopEffects);
            }
        }
    }

    private static StatusEffect? ResolveStatusEffect(string? statusEffectName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(statusEffectName))
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

    private static void ApplyDefinition(StatusEffect statusEffect, StatusEffectDefinition definition, bool applyEffectLists = true)
    {
        ApplyBase(statusEffect, definition.Base, applyEffectLists);

        if (statusEffect is SE_Stats stats)
        {
            ApplyStats(stats, definition.Stats);
            ApplyStaminaDrainModifier(stats, definition.StaminaDrainModifier);
            ApplyDamageTakenModifiers(stats.m_mods, definition.DamageTakenModifiers);
            ApplyDamage(stats.m_percentigeDamageModifiers, definition.PercentageDamageModifiers);
        }

        if (statusEffect is SE_Poison poison)
        {
            ApplyPoison(poison, definition.Poison);
        }

        if (statusEffect is SE_Shield shield)
        {
            ApplyShield(shield, definition.Shield);
        }

        if (statusEffect is SE_Frost frost)
        {
            ApplyFrost(frost, definition.Frost);
        }

        if (statusEffect is SE_Rested rested)
        {
            ApplyRested(rested, definition.Rested);
        }

        if (statusEffect is SE_HealthUpgrade healthUpgrade)
        {
            ApplyHealthUpgrade(healthUpgrade, definition.HealthUpgrade);
        }
    }

    private static void ApplyLiveSafeDefinition(StatusEffect statusEffect, StatusEffectDefinition definition)
    {
        ApplyLiveSafeBase(statusEffect, definition.Base);

        if (statusEffect is SE_Stats stats)
        {
            ApplyLiveSafeStats(stats, definition.Stats);
            ApplyStaminaDrainModifier(stats, definition.StaminaDrainModifier);
            ApplyDamageTakenModifiers(stats.m_mods, definition.DamageTakenModifiers);
            ApplyDamage(stats.m_percentigeDamageModifiers, definition.PercentageDamageModifiers);
        }
    }

    private static void ApplyLiveSafeBase(StatusEffect statusEffect, StatusEffectBaseDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        Copy(definition.DisplayName, value => statusEffect.m_name = value);
        Copy(definition.Tooltip, value => statusEffect.m_tooltip = value);
        ApplyBaseIcon(statusEffect, definition.Icon);
        Copy(definition.Category, value => statusEffect.m_category = value);
        CopyIconFlags(definition.IconFlags, statusEffect);
    }

    private static void ApplyBase(StatusEffect statusEffect, StatusEffectBaseDefinition? definition, bool applyEffectLists)
    {
        if (definition == null)
        {
            return;
        }

        Copy(definition.DisplayName, value => statusEffect.m_name = value);
        Copy(definition.Tooltip, value => statusEffect.m_tooltip = value);
        ApplyBaseIcon(statusEffect, definition.Icon);
        if (applyEffectLists)
        {
            ApplyEffectList(statusEffect.name, definition.StartEffects, value => statusEffect.m_startEffects = value, "startEffects");
            ApplyEffectList(statusEffect.name, definition.StopEffects, value => statusEffect.m_stopEffects = value, "stopEffects");
        }

        Copy(definition.Category, value => statusEffect.m_category = value);
        CopyBaseTime(definition.Time, statusEffect);
        Copy(definition.RepeatInterval, value => statusEffect.m_repeatInterval = Math.Max(0f, value));
        Copy(definition.StartMessage, value => statusEffect.m_startMessage = value);
        Copy(definition.StopMessage, value => statusEffect.m_stopMessage = value);
        Copy(definition.RepeatMessage, value => statusEffect.m_repeatMessage = value);
        CopyIconFlags(definition.IconFlags, statusEffect);
        CopyEnum<MessageHud.MessageType>(definition.StartMessageType, value => statusEffect.m_startMessageType = value, "startMessageType");
        CopyEnum<MessageHud.MessageType>(definition.StopMessageType, value => statusEffect.m_stopMessageType = value, "stopMessageType");
        CopyEnum<MessageHud.MessageType>(definition.RepeatMessageType, value => statusEffect.m_repeatMessageType = value, "repeatMessageType");
        CopyEnum<StatusEffect.StatusAttribute>(definition.Attributes, value => statusEffect.m_attributes = value, "attributes");
    }

    private static void CopyBaseTime(string? value, StatusEffect statusEffect)
    {
        CopyFloatTuple(
            value,
            ttl => statusEffect.m_ttl = Math.Max(0f, ttl),
            cooldown => statusEffect.m_cooldown = Math.Max(0f, cooldown),
            null,
            "time",
            0f,
            0f,
            null);
    }

    private static void CopyIconFlags(string? value, StatusEffect statusEffect)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string rawValue = value!;
        string[] parts = rawValue.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (!TryParseBoolPart(parts, 0, false, "iconFlags", out bool cooldownIcon) ||
            !TryParseBoolPart(parts, 1, false, "iconFlags", out bool flashIcon))
        {
            return;
        }

        statusEffect.m_cooldownIcon = cooldownIcon;
        statusEffect.m_flashIcon = flashIcon;
    }

    private static void ApplyBaseIcon(StatusEffect statusEffect, string? iconName, bool warnMissingItemIcon = false)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return;
        }

        string iconKey = iconName!.Trim();
        if (TryResolveItemIconSprite(iconKey, out string itemName, out Sprite? itemIcon))
        {
            if (itemIcon != null)
            {
                statusEffect.m_icon = itemIcon;
            }
            else if (warnMissingItemIcon && !IsHeadlessGraphics())
            {
                DataForgeLogContext.Warning($"{statusEffect.name} could not resolve status effect icon item '{itemName}'.");
            }

            return;
        }

        Sprite? icon = ResolveIconSprite(iconKey);
        if (icon == null)
        {
            if (!IsHeadlessGraphics())
            {
                DataForgeLogContext.Warning($"{statusEffect.name} has unknown status effect icon '{iconKey}'. Expected a png under DataForge/icon.");
            }

            return;
        }

        statusEffect.m_icon = icon;
    }

    private static bool IsHeadlessGraphics()
    {
        return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    }

    private static bool TryResolveItemIconSprite(string iconKey, out string itemName, out Sprite? icon)
    {
        itemName = "";
        icon = null;
        if (!TryGetItemIconReference(iconKey, out itemName))
        {
            return false;
        }

        GameObject? itemPrefab = ResolveItemPrefab(itemName);
        ItemDrop? itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
        Sprite[]? icons = itemDrop?.m_itemData.m_shared.m_icons;
        icon = icons?.FirstOrDefault(sprite => sprite != null);
        return true;
    }

    private static bool IsItemIconReference(string? iconName)
    {
        return TryGetItemIconReference(iconName, out _);
    }

    private static bool TryGetItemIconReference(string? iconName, out string itemName)
    {
        itemName = "";
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return false;
        }

        string trimmed = iconName!.Trim();
        if (!trimmed.StartsWith(ItemIconPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        itemName = trimmed.Substring(ItemIconPrefix.Length).Trim();
        return itemName.Length > 0;
    }

    private static GameObject? ResolveItemPrefab(string itemName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        GameObject? prefab = ObjectDB.instance.GetItemPrefab(itemName);
        if (prefab != null)
        {
            return prefab;
        }

        return ObjectDB.instance.m_items.FirstOrDefault(item =>
            item != null &&
            (string.Equals(Utils.GetPrefabName(item.name), itemName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.name, itemName, StringComparison.OrdinalIgnoreCase)));
    }

    private static Sprite? ResolveIconSprite(string iconName)
    {
        string? iconPath = ResolveIconPath(iconName);
        if (iconPath == null || !File.Exists(iconPath))
        {
            return null;
        }

        DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(iconPath);
        if (IconCache.TryGetValue(iconPath, out IconCacheEntry? cached) &&
            cached.LastWriteTimeUtc == lastWriteTimeUtc)
        {
            return cached.Sprite;
        }

        try
        {
            Texture2D texture = new(2, 2, TextureFormat.RGBA32, mipChain: false)
            {
                name = Path.GetFileNameWithoutExtension(iconPath)
            };

            if (!TryLoadImage(texture, File.ReadAllBytes(iconPath)))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            sprite.name = Path.GetFileNameWithoutExtension(iconPath);
            IconCache[iconPath] = new IconCacheEntry(lastWriteTimeUtc, sprite);
            return sprite;
        }
        catch (Exception ex)
        {
            DataForgeLogContext.Warning($"Could not load status effect icon '{iconName}' from '{iconPath}': {ex.Message}");
            return null;
        }
    }

    private static bool TryLoadImage(Texture2D texture, byte[] data)
    {
        if (LoadImageMethod == null)
        {
            DataForgeLogContext.Warning("Could not locate UnityEngine.ImageConversion.LoadImage for status effect icon.");
            return false;
        }

        object?[] args = LoadImageMethod.GetParameters().Length == 3
            ? new object?[] { texture, data, false }
            : new object?[] { texture, data };
        return LoadImageMethod.Invoke(null, args) is bool loaded && loaded;
    }

    private static MethodInfo? ResolveLoadImageMethod()
    {
        return typeof(ImageConversion).GetMethod(
                   "LoadImage",
                   BindingFlags.Public | BindingFlags.Static,
                   null,
                   new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) },
                   null)
               ?? typeof(ImageConversion).GetMethod(
                   "LoadImage",
                   BindingFlags.Public | BindingFlags.Static,
                   null,
                   new[] { typeof(Texture2D), typeof(byte[]) },
                   null);
    }

    private static string? ResolveIconPath(string iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return null;
        }

        string relativePath = iconName.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (!Path.HasExtension(relativePath))
        {
            relativePath += ".png";
        }

        string fullPath = Path.GetFullPath(Path.Combine(IconDirectory, relativePath));
        string iconRoot = Path.GetFullPath(IconDirectory);
        if (!fullPath.StartsWith(iconRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    private static void ApplyEffectList(string statusEffectName, string? value, Action<EffectList> assign, string fieldName)
    {
        if (value == null)
        {
            return;
        }

        EffectList? effectList = ParseEffectList(statusEffectName, value, fieldName);
        if (effectList != null)
        {
            assign(effectList);
        }
    }

    private static EffectList? ParseEffectList(string statusEffectName, string value, string fieldName)
    {
        string rawValue = value.Trim();
        if (rawValue.Length == 0 || rawValue.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return EmptyEffectList();
        }

        List<EffectList.EffectData> effects = new();
        foreach (string token in rawValue.Split(new[] { ',' }, StringSplitOptions.None))
        {
            string prefabName = token.Trim();
            if (prefabName.Length == 0)
            {
                continue;
            }

            if (prefabName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            GameObject? prefab = ResolveEffectPrefab(prefabName);
            if (prefab == null)
            {
                DataForgeLogContext.Warning($"{statusEffectName} has unknown {fieldName} prefab '{prefabName}'.");
                return null;
            }

            effects.Add(new EffectList.EffectData
            {
                m_prefab = prefab,
                m_enabled = true,
                m_variant = -1
            });
        }

        return new EffectList
        {
            m_effectPrefabs = effects.ToArray()
        };
    }

    private static EffectList EmptyEffectList()
    {
        return new EffectList
        {
            m_effectPrefabs = Array.Empty<EffectList.EffectData>()
        };
    }

    private static EffectList CloneEffectList(EffectList? source)
    {
        if (source?.m_effectPrefabs == null || source.m_effectPrefabs.Length == 0)
        {
            return EmptyEffectList();
        }

        return new EffectList
        {
            m_effectPrefabs = source.m_effectPrefabs
                .Where(effect => effect != null)
                .Select(CloneEffectData)
                .ToArray()
        };
    }

    private static EffectList.EffectData CloneEffectData(EffectList.EffectData source)
    {
        return new EffectList.EffectData
        {
            m_prefab = source.m_prefab,
            m_enabled = source.m_enabled,
            m_variant = source.m_variant,
            m_attach = source.m_attach,
            m_follow = source.m_follow,
            m_inheritParentRotation = source.m_inheritParentRotation,
            m_inheritParentScale = source.m_inheritParentScale,
            m_multiplyParentVisualScale = source.m_multiplyParentVisualScale,
            m_randomRotation = source.m_randomRotation,
            m_scale = source.m_scale,
            m_childTransform = source.m_childTransform
        };
    }

    private static string? FormatEffectList(EffectList? effectList)
    {
        if (effectList?.m_effectPrefabs == null || effectList.m_effectPrefabs.Length == 0)
        {
            return null;
        }

        List<string> prefabNames = effectList.m_effectPrefabs
            .Where(effect => effect != null && effect.m_enabled && effect.m_prefab != null)
            .Select(effect => Utils.GetPrefabName(effect.m_prefab.name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        return prefabNames.Count > 0
            ? string.Join(", ", prefabNames)
            : null;
    }

    private static GameObject? ResolveEffectPrefabFromStatusEffects(string prefabName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        foreach (StatusEffect statusEffect in ObjectDB.instance.m_StatusEffects)
        {
            GameObject? prefab = ResolveEffectPrefabFromEffectList(statusEffect?.m_startEffects, prefabName) ??
                                 ResolveEffectPrefabFromEffectList(statusEffect?.m_stopEffects, prefabName);
            if (prefab != null)
            {
                return prefab;
            }
        }

        return null;
    }

    private static GameObject? ResolveEffectPrefabFromEffectList(EffectList? effectList, string prefabName)
    {
        if (effectList?.m_effectPrefabs == null)
        {
            return null;
        }

        foreach (EffectList.EffectData effectData in effectList.m_effectPrefabs)
        {
            GameObject? prefab = effectData.m_prefab;
            if (prefab == null)
            {
                continue;
            }

            string cleanName = Utils.GetPrefabName(prefab.name);
            if (string.Equals(cleanName, prefabName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prefab.name, prefabName, StringComparison.OrdinalIgnoreCase))
            {
                return prefab;
            }
        }

        return null;
    }

    private static GameObject? ResolveEffectPrefab(string prefabName)
    {
        if (ZNetScene.instance == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return ResolveEffectPrefabFromStatusEffects(prefabName);
        }

        GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
        if (prefab != null)
        {
            return prefab;
        }

        int hash = prefabName.GetStableHashCode();
        if (ZNetScene.instance.m_namedPrefabs.TryGetValue(hash, out GameObject namedPrefab) && namedPrefab != null)
        {
            return namedPrefab;
        }

        return ResolveEffectPrefabFromStatusEffects(prefabName);
    }

    private static void ApplyStats(SE_Stats stats, StatsDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        CopyFloatTuple(
            definition.UpFront,
            value => stats.m_healthUpFront = value,
            value => stats.m_staminaUpFront = value,
            value => stats.m_eitrUpFront = value,
            value => stats.m_adrenalineUpFront = value,
            "upFront",
            0f,
            0f,
            0f,
            0f);
        CopyHealthPerTick(definition.HealthPerTick, stats);
        CopyFloatTuple(
            definition.HealthOverTime,
            value => stats.m_healthOverTime = value,
            value => stats.m_healthOverTimeDuration = Math.Max(0f, value),
            value => stats.m_healthOverTimeInterval = Math.Max(0f, value),
            "healthOverTime",
            0f,
            0f,
            5f);
        CopyFloatBoolTuple(
            definition.StaminaOverTime,
            value => stats.m_staminaOverTime = value,
            value => stats.m_staminaOverTimeDuration = Math.Max(0f, value),
            value => stats.m_staminaOverTimeIsFraction = value,
            "staminaOverTime",
            0f,
            0f,
            false);
        CopyFloatTuple(
            definition.EitrOverTime,
            value => stats.m_eitrOverTime = value,
            value => stats.m_eitrOverTimeDuration = Math.Max(0f, value),
            null,
            "eitrOverTime",
            0f,
            0f,
            null);
        CopyFloatTuple(
            definition.RegenMultiplier,
            value => stats.m_healthRegenMultiplier = Math.Max(0f, value),
            value => stats.m_staminaRegenMultiplier = Math.Max(0f, value),
            value => stats.m_eitrRegenMultiplier = Math.Max(0f, value),
            "regenMultiplier",
            1f,
            1f,
            1f);
        Copy(definition.StaminaDrainPerSec, value => stats.m_staminaDrainPerSec = value);
        Copy(definition.AdrenalineModifier, value => stats.m_adrenalineModifier = value);
        Copy(definition.SpeedModifier, value => stats.m_speedModifier = value);
        Copy(definition.SwimSpeedModifier, value => stats.m_swimSpeedModifier = value);
        CopyFloatTuple(
            definition.JumpModifier,
            value => stats.m_jumpModifier.x = value,
            value => stats.m_jumpModifier.y = value,
            value => stats.m_jumpModifier.z = value,
            "jumpModifier",
            0f,
            0f,
            0f);
        CopyFloatTuple(
            definition.WindRun,
            value => stats.m_windMovementModifier = value,
            value => stats.m_windRunStaminaModifier = value,
            null,
            "windRun",
            0f,
            0f,
            null);
        CopyFloatTuple(
            definition.Sneak,
            value => stats.m_stealthModifier = value,
            value => stats.m_noiseModifier = value,
            null,
            "sneak",
            0f,
            0f,
            null);
        CopyFloatTuple(
            definition.Fall,
            value => stats.m_fallDamageModifier = value,
            value => stats.m_maxMaxFallSpeed = Math.Max(0f, value),
            null,
            "fall",
            0f,
            0f,
            null);
        CopyFloatTuple(
            definition.Armor,
            value => stats.m_addArmor = value,
            value => stats.m_armorMultiplier = value,
            null,
            "armor",
            0f,
            0f,
            null);
        CopyFloatTuple(
            definition.Block,
            value => stats.m_timedBlockBonus = value,
            value => stats.m_blockStaminaUseFlatValue = value,
            null,
            "block",
            0f,
            0f,
            null);
        Copy(definition.StaggerModifier, value => stats.m_staggerModifier = value);
        Copy(definition.AddMaxCarryWeight, value => stats.m_addMaxCarryWeight = value);
        // Pheromone fields are intentionally held for a separate domain.
        CopySkillValuePair(definition.AttackDamage, value => stats.m_modifyAttackSkill = value, value => stats.m_damageModifier = value, "attackDamage", 1f);
        CopySkillValuePair(definition.RaiseSkill, value => stats.m_raiseSkill = value, value => stats.m_raiseSkillModifier = value, "raiseSkill", 0f);
        CopySkillValuePair(definition.SkillLevel, value => stats.m_skillLevel = value, value => stats.m_skillLevelModifier = value, "skillLevel", 0f);
        CopySkillValuePair(definition.SkillLevel2, value => stats.m_skillLevel2 = value, value => stats.m_skillLevelModifier2 = value, "skillLevel2", 0f);
    }

    private static void ApplyLiveSafeStats(SE_Stats stats, StatsDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        CopyFloatTuple(
            definition.RegenMultiplier,
            value => stats.m_healthRegenMultiplier = Math.Max(0f, value),
            value => stats.m_staminaRegenMultiplier = Math.Max(0f, value),
            value => stats.m_eitrRegenMultiplier = Math.Max(0f, value),
            "regenMultiplier",
            1f,
            1f,
            1f);
        Copy(definition.StaminaDrainPerSec, value => stats.m_staminaDrainPerSec = value);
        Copy(definition.AdrenalineModifier, value => stats.m_adrenalineModifier = value);
        Copy(definition.SpeedModifier, value => stats.m_speedModifier = value);
        Copy(definition.SwimSpeedModifier, value => stats.m_swimSpeedModifier = value);
        CopyFloatTuple(
            definition.JumpModifier,
            value => stats.m_jumpModifier.x = value,
            value => stats.m_jumpModifier.y = value,
            value => stats.m_jumpModifier.z = value,
            "jumpModifier",
            0f,
            0f,
            0f);
        CopyFloatTuple(
            definition.WindRun,
            value => stats.m_windMovementModifier = value,
            value => stats.m_windRunStaminaModifier = value,
            null,
            "windRun",
            0f,
            0f,
            null);
        CopyFloatTuple(
            definition.Sneak,
            value => stats.m_stealthModifier = value,
            value => stats.m_noiseModifier = value,
            null,
            "sneak",
            0f,
            0f,
            null);
        CopyFloatTuple(
            definition.Fall,
            value => stats.m_fallDamageModifier = value,
            value => stats.m_maxMaxFallSpeed = Math.Max(0f, value),
            null,
            "fall",
            0f,
            0f,
            null);
        CopyFloatTuple(
            definition.Armor,
            value => stats.m_addArmor = value,
            value => stats.m_armorMultiplier = value,
            null,
            "armor",
            0f,
            0f,
            null);
        CopyFloatTuple(
            definition.Block,
            value => stats.m_timedBlockBonus = value,
            value => stats.m_blockStaminaUseFlatValue = value,
            null,
            "block",
            0f,
            0f,
            null);
        Copy(definition.StaggerModifier, value => stats.m_staggerModifier = value);
        Copy(definition.AddMaxCarryWeight, value => stats.m_addMaxCarryWeight = value);
        CopySkillValuePair(definition.AttackDamage, value => stats.m_modifyAttackSkill = value, value => stats.m_damageModifier = value, "attackDamage", 1f);
        CopySkillValuePair(definition.RaiseSkill, value => stats.m_raiseSkill = value, value => stats.m_raiseSkillModifier = value, "raiseSkill", 0f);
        CopySkillValuePair(definition.SkillLevel, value => stats.m_skillLevel = value, value => stats.m_skillLevelModifier = value, "skillLevel", 0f);
        CopySkillValuePair(definition.SkillLevel2, value => stats.m_skillLevel2 = value, value => stats.m_skillLevelModifier2 = value, "skillLevel2", 0f);
    }

    private static void ApplyStaminaDrainModifier(SE_Stats stats, StaminaDrainModifierDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        Copy(definition.Run, value => stats.m_runStaminaDrainModifier = value);
        Copy(definition.Attack, value => stats.m_attackStaminaUseModifier = value);
        Copy(definition.Block, value => stats.m_blockStaminaUseModifier = value);
        Copy(definition.Dodge, value => stats.m_dodgeStaminaUseModifier = value);
        Copy(definition.Jump, value => stats.m_jumpStaminaUseModifier = value);
        Copy(definition.Sneak, value => stats.m_sneakStaminaUseModifier = value);
        Copy(definition.Swim, value => stats.m_swimStaminaUseModifier = value);
        Copy(definition.HomeItem, value => stats.m_homeItemStaminaUseModifier = value);
    }

    /*
    Pheromone support belongs in a separate domain because it targets creature spawn/love/flee behavior,
    not ordinary player stat overrides.

    private static void ApplyPheromone(SE_Stats stats, PheromoneDefinition? definition)
    {
        ...
    }
    */

    private static void ApplyPoison(SE_Poison poison, PoisonDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        Copy(definition.BaseTtl, value => poison.m_baseTTL = Math.Max(0f, value));
        Copy(definition.DamageInterval, value => poison.m_damageInterval = Math.Max(0f, value));
        Copy(definition.DamagePerHit, value => poison.m_damagePerHit = Math.Max(0f, value));
        Copy(definition.TtlPerDamage, value => poison.m_TTLPerDamage = Math.Max(0f, value));
        Copy(definition.TtlPerDamagePlayer, value => poison.m_TTLPerDamagePlayer = Math.Max(0f, value));
        Copy(definition.TtlPower, value => poison.m_TTLPower = value);
    }

    private static void ApplyShield(SE_Shield shield, ShieldStatusDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        Copy(definition.AbsorbDamage, value => shield.m_absorbDamage = Math.Max(0f, value));
        Copy(definition.AbsorbDamagePerSkillLevel, value => shield.m_absorbDamagePerSkillLevel = Math.Max(0f, value));
        Copy(definition.AbsorbDamageWorldLevel, value => shield.m_absorbDamageWorldLevel = Math.Max(0f, value));
        Copy(definition.TtlPerItemLevel, value => shield.m_ttlPerItemLevel = Math.Max(0, value));
        Copy(definition.LevelUpSkillFactor, value => shield.m_levelUpSkillFactor = Math.Max(0f, value));
        CopyEnum<Skills.SkillType>(definition.LevelUpSkillOnBreak, value => shield.m_levelUpSkillOnBreak = value, "levelUpSkillOnBreak");
    }

    private static void ApplyFrost(SE_Frost frost, FrostDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        Copy(definition.FreezeTimeEnemy, value => frost.m_freezeTimeEnemy = Math.Max(0f, value));
        Copy(definition.FreezeTimePlayer, value => frost.m_freezeTimePlayer = Math.Max(0f, value));
        Copy(definition.MinSpeedFactor, value => frost.m_minSpeedFactor = value);
    }

    private static void ApplyRested(SE_Rested rested, RestedDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        Copy(definition.BaseTtl, value => rested.m_baseTTL = Math.Max(0f, value));
        Copy(definition.TtlPerComfortLevel, value => rested.m_TTLPerComfortLevel = Math.Max(0f, value));
    }

    private static void ApplyHealthUpgrade(SE_HealthUpgrade healthUpgrade, HealthUpgradeDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        Copy(definition.MoreHealth, value => healthUpgrade.m_moreHealth = value);
        Copy(definition.MoreStamina, value => healthUpgrade.m_moreStamina = value);
    }

    private static void ApplyDamage(HitData.DamageTypes target, StatusDamageDefinition? damage)
    {
        if (damage == null)
        {
            return;
        }

        Copy(damage.Blunt, value => target.m_blunt = value);
        Copy(damage.Slash, value => target.m_slash = value);
        Copy(damage.Pierce, value => target.m_pierce = value);
        Copy(damage.Chop, value => target.m_chop = value);
        Copy(damage.Pickaxe, value => target.m_pickaxe = value);
        Copy(damage.Fire, value => target.m_fire = value);
        Copy(damage.Frost, value => target.m_frost = value);
        Copy(damage.Lightning, value => target.m_lightning = value);
        Copy(damage.Poison, value => target.m_poison = value);
        Copy(damage.Spirit, value => target.m_spirit = value);
    }

    private static void ApplyDamageTakenModifiers(List<HitData.DamageModPair> modifiers, DamageTakenModifierDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Blunt, definition.Blunt, "damageTakenModifiers.blunt");
        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Slash, definition.Slash, "damageTakenModifiers.slash");
        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Pierce, definition.Pierce, "damageTakenModifiers.pierce");
        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Chop, definition.Chop, "damageTakenModifiers.chop");
        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Pickaxe, definition.Pickaxe, "damageTakenModifiers.pickaxe");
        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Fire, definition.Fire, "damageTakenModifiers.fire");
        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Frost, definition.Frost, "damageTakenModifiers.frost");
        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Lightning, definition.Lightning, "damageTakenModifiers.lightning");
        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Poison, definition.Poison, "damageTakenModifiers.poison");
        ApplyDamageTakenModifier(modifiers, HitData.DamageType.Spirit, definition.Spirit, "damageTakenModifiers.spirit");
    }

    private static void ApplyDamageTakenModifier(List<HitData.DamageModPair> modifiers, HitData.DamageType damageType, string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Enum.TryParse(value, ignoreCase: true, out HitData.DamageModifier modifier))
        {
            DataForgeLogContext.Warning($"Unknown {fieldName} value '{value}'.");
            return;
        }

        int index = modifiers.FindIndex(pair => pair.m_type == damageType);
        if (modifier == HitData.DamageModifier.Normal)
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

    private static void CopySkillValuePair(string? value, Action<Skills.SkillType> assignSkill, Action<float> assignValue, string fieldName, float defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string rawValue = value!;
        string[] parts = rawValue.Split(new[] { ',' }, 2, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return;
        }

        if (!Enum.TryParse(parts[0], ignoreCase: true, out Skills.SkillType skill))
        {
            DataForgeLogContext.Warning($"Unknown {fieldName} skill value '{parts[0]}'.");
            return;
        }

        float numericValue = defaultValue;
        if (parts.Length > 1 && parts[1].Length > 0 &&
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out numericValue))
        {
            DataForgeLogContext.Warning($"Could not parse {fieldName} value '{parts[1]}'. Expected '{fieldName}: Skill, value'.");
            return;
        }

        assignSkill(skill);
        assignValue(numericValue);
    }

    private static void CopyHealthPerTick(string? value, SE_Stats stats)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (!TryParseFloatPart(parts, 0, 0f, "healthPerTick", out float amount) ||
            !TryParseFloatPart(parts, 1, 0f, "healthPerTick", out float minHealthPercentage))
        {
            return;
        }

        HitData.HitType hitType = HitData.HitType.Undefined;
        if (parts.Length > 2 && parts[2].Length > 0 &&
            !Enum.TryParse(parts[2], ignoreCase: true, out hitType))
        {
            DataForgeLogContext.Warning($"Unknown healthPerTick hitType value '{parts[2]}'.");
            return;
        }

        stats.m_healthPerTick = amount;
        stats.m_healthPerTickMinHealthPercentage = minHealthPercentage;
        stats.m_hitType = hitType;
    }

    private static void CopyFloatTuple(string? value, Action<float> assignFirst, Action<float> assignSecond, Action<float>? assignThird, string fieldName, float defaultFirst, float defaultSecond, float? defaultThird)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (!TryParseFloatPart(parts, 0, defaultFirst, fieldName, out float first) ||
            !TryParseFloatPart(parts, 1, defaultSecond, fieldName, out float second))
        {
            return;
        }

        float third = defaultThird ?? 0f;
        if (assignThird != null && defaultThird.HasValue)
        {
            if (!TryParseFloatPart(parts, 2, defaultThird.Value, fieldName, out third))
            {
                return;
            }
        }

        assignFirst(first);
        assignSecond(second);
        assignThird?.Invoke(third);
    }

    private static void CopyFloatTuple(string? value, Action<float> assignFirst, Action<float> assignSecond, Action<float> assignThird, Action<float> assignFourth, string fieldName, float defaultFirst, float defaultSecond, float defaultThird, float defaultFourth)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (!TryParseFloatPart(parts, 0, defaultFirst, fieldName, out float first) ||
            !TryParseFloatPart(parts, 1, defaultSecond, fieldName, out float second) ||
            !TryParseFloatPart(parts, 2, defaultThird, fieldName, out float third) ||
            !TryParseFloatPart(parts, 3, defaultFourth, fieldName, out float fourth))
        {
            return;
        }

        assignFirst(first);
        assignSecond(second);
        assignThird(third);
        assignFourth(fourth);
    }

    private static void CopyFloatBoolTuple(string? value, Action<float> assignFirst, Action<float> assignSecond, Action<bool> assignThird, string fieldName, float defaultFirst, float defaultSecond, bool defaultThird)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] parts = value!.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (!TryParseFloatPart(parts, 0, defaultFirst, fieldName, out float first) ||
            !TryParseFloatPart(parts, 1, defaultSecond, fieldName, out float second))
        {
            return;
        }

        bool third = defaultThird;
        if (parts.Length > 2 && parts[2].Length > 0 && !bool.TryParse(parts[2], out third))
        {
            DataForgeLogContext.Warning($"Could not parse {fieldName} value '{parts[2]}'. Expected true or false.");
            return;
        }

        assignFirst(first);
        assignSecond(second);
        assignThird(third);
    }

    private static bool TryParseFloatPart(string[] parts, int index, float defaultValue, string fieldName, out float value)
    {
        value = defaultValue;
        if (parts.Length <= index || parts[index].Length == 0)
        {
            return true;
        }

        if (float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        DataForgeLogContext.Warning($"Could not parse {fieldName} value '{parts[index]}'. Expected a number.");
        return false;
    }

    private static bool TryParseBoolPart(string[] parts, int index, bool defaultValue, string fieldName, out bool value)
    {
        value = defaultValue;
        if (parts.Length <= index || parts[index].Length == 0)
        {
            return true;
        }

        if (bool.TryParse(parts[index], out value))
        {
            return true;
        }

        DataForgeLogContext.Warning($"Could not parse {fieldName} value '{parts[index]}'. Expected true or false.");
        return false;
    }

    private static string FormatSkillValuePair(Skills.SkillType skill, float value)
    {
        return $"{skill}, {value.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    private static string FormatHealthPerTick(SE_Stats stats)
    {
        return $"{FormatFloat(stats.m_healthPerTick)}, {FormatFloat(stats.m_healthPerTickMinHealthPercentage)}, {stats.m_hitType}";
    }

    private static string FormatFloatTuple(float first, float second, float? third)
    {
        return third.HasValue
            ? $"{FormatFloat(first)}, {FormatFloat(second)}, {FormatFloat(third.Value)}"
            : $"{FormatFloat(first)}, {FormatFloat(second)}";
    }

    private static string FormatFloatTuple(float first, float second, float third, float fourth)
    {
        return $"{FormatFloat(first)}, {FormatFloat(second)}, {FormatFloat(third)}, {FormatFloat(fourth)}";
    }

    private static string FormatFloatBoolTuple(float first, float second, bool third)
    {
        return $"{FormatFloat(first)}, {FormatFloat(second)}, {third.ToString().ToLowerInvariant()}";
    }

    private static string FormatBoolTuple(bool first, bool second)
    {
        return $"{first.ToString().ToLowerInvariant()}, {second.ToString().ToLowerInvariant()}";
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
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
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new
                    {
                        Entry = StatusEffectEntry.FromDefinition(pair.Key, pair.Value, overrideEntry: true),
                        OwnerKey = pair.Key,
                        SortKey = pair.Key
                    })
                    .ToList();

                return GeneratedArtifactWriter.GeneratedHeader(DomainName, OverrideFileName, "full scaffold") +
                       DataForgeReferenceSections.SerializeReferenceSections(
                           fullEntries,
                           entry => entry.SortKey,
                           entry => DataForgeOwnerResolver.GetAssetOwnerName(entry.OwnerKey),
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
        List<StatusEffectReferenceEntry> referenceEntries = Baselines
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => StatusEffectReferenceEntry.From(pair.Key, pair.Value))
            .ToList();

        return DataForgeReferenceSections.SerializeReferenceSections(
            referenceEntries,
            entry => entry.Effect,
            entry => DataForgeOwnerResolver.GetAssetOwnerName(entry.Effect),
            entry => entry,
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
        if (ObjectDB.instance != null)
        {
            foreach (StatusEffect statusEffect in ObjectDB.instance.m_StatusEffects
                         .Where(statusEffect => statusEffect != null && !string.IsNullOrWhiteSpace(statusEffect.name))
                         .OrderBy(statusEffect => statusEffect.name, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(statusEffect.name.Trim());
                builder.Append('|');
                builder.AppendLine(SparseSerializer.Serialize(StatusEffectDefinition.From(statusEffect)));
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

    internal sealed class StatusEffectEntry
    {
        internal string LogContext { get; private set; } = "";
        public string Effect { get; set; } = "";
        public bool Override { get; set; } = true;
        public string? CloneFrom { get; set; }
        public string? DisplayName { get; set; }
        public string? Tooltip { get; set; }
        public string? Icon { get; set; }
        public string? StartEffects { get; set; }
        public string? StopEffects { get; set; }
        public string? Category { get; set; }
        public string? Time { get; set; }
        public string? IconFlags { get; set; }
        public float? RepeatInterval { get; set; }
        public string? StartMessage { get; set; }
        public string? StopMessage { get; set; }
        public string? RepeatMessage { get; set; }
        public string? StartMessageType { get; set; }
        public string? StopMessageType { get; set; }
        public string? RepeatMessageType { get; set; }
        public string? Attributes { get; set; }
        public StatsDefinition? Stats { get; set; }
        public StaminaDrainModifierDefinition? StaminaDrainModifier { get; set; }
        public DamageTakenModifierDefinition? DamageTakenModifiers { get; set; }
        public StatusDamageDefinition? PercentageDamageModifiers { get; set; }
        public PoisonDefinition? Poison { get; set; }
        public ShieldStatusDefinition? Shield { get; set; }
        public FrostDefinition? Frost { get; set; }
        public RestedDefinition? Rested { get; set; }
        public HealthUpgradeDefinition? HealthUpgrade { get; set; }

        internal void SetLogContext(string value)
        {
            LogContext = value;
        }

        internal bool HasDefinition =>
            HasBaseDefinition ||
            Stats != null ||
            StaminaDrainModifier != null ||
            DamageTakenModifiers != null ||
            PercentageDamageModifiers != null ||
            Poison != null ||
            Shield != null ||
            Frost != null ||
            Rested != null ||
            HealthUpgrade != null;

        private bool HasBaseDefinition =>
            DisplayName != null ||
            Tooltip != null ||
            Icon != null ||
            StartEffects != null ||
            StopEffects != null ||
            Category != null ||
            Time != null ||
            IconFlags != null ||
            RepeatInterval.HasValue ||
            StartMessage != null ||
            StopMessage != null ||
            RepeatMessage != null ||
            StartMessageType != null ||
            StopMessageType != null ||
            RepeatMessageType != null ||
            Attributes != null;

        internal StatusEffectDefinition ToDefinition()
        {
            return new StatusEffectDefinition
            {
                Base = ToBaseDefinition(),
                Stats = Stats,
                StaminaDrainModifier = StaminaDrainModifier,
                DamageTakenModifiers = DamageTakenModifiers,
                PercentageDamageModifiers = PercentageDamageModifiers,
                Poison = Poison,
                Shield = Shield,
                Frost = Frost,
                Rested = Rested,
                HealthUpgrade = HealthUpgrade
            };
        }

        private StatusEffectBaseDefinition? ToBaseDefinition()
        {
            if (!HasBaseDefinition)
            {
                return null;
            }

            return new StatusEffectBaseDefinition
            {
                DisplayName = DisplayName,
                Tooltip = Tooltip,
                Icon = Icon,
                StartEffects = StartEffects,
                StopEffects = StopEffects,
                Category = Category,
                Time = Time,
                IconFlags = IconFlags,
                RepeatInterval = RepeatInterval,
                StartMessage = StartMessage,
                StopMessage = StopMessage,
                RepeatMessage = RepeatMessage,
                StartMessageType = StartMessageType,
                StopMessageType = StopMessageType,
                RepeatMessageType = RepeatMessageType,
                Attributes = Attributes
            };
        }

        internal static StatusEffectEntry FromDefinition(string name, StatusEffectDefinition definition, bool overrideEntry)
        {
            StatusEffectBaseDefinition? baseDefinition = definition.Base;
            return new StatusEffectEntry
            {
                Effect = name,
                Override = overrideEntry,
                DisplayName = baseDefinition?.DisplayName,
                Tooltip = baseDefinition?.Tooltip,
                Icon = baseDefinition?.Icon,
                StartEffects = baseDefinition?.StartEffects,
                StopEffects = baseDefinition?.StopEffects,
                Category = baseDefinition?.Category,
                Time = baseDefinition?.Time,
                IconFlags = baseDefinition?.IconFlags,
                RepeatInterval = baseDefinition?.RepeatInterval,
                StartMessage = baseDefinition?.StartMessage,
                StopMessage = baseDefinition?.StopMessage,
                RepeatMessage = baseDefinition?.RepeatMessage,
                StartMessageType = baseDefinition?.StartMessageType,
                StopMessageType = baseDefinition?.StopMessageType,
                RepeatMessageType = baseDefinition?.RepeatMessageType,
                Attributes = baseDefinition?.Attributes,
                Stats = definition.Stats,
                StaminaDrainModifier = definition.StaminaDrainModifier,
                DamageTakenModifiers = definition.DamageTakenModifiers,
                PercentageDamageModifiers = definition.PercentageDamageModifiers,
                Poison = definition.Poison,
                Shield = definition.Shield,
                Frost = definition.Frost,
                Rested = definition.Rested,
                HealthUpgrade = definition.HealthUpgrade
            };
        }
    }

    internal sealed class StatusEffectReferenceEntry
    {
        public string Effect { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? Tooltip { get; set; }
        public string? Icon { get; set; }
        public string? StartEffects { get; set; }
        public string? StopEffects { get; set; }
        public string? Category { get; set; }
        public string? Time { get; set; }
        public string? IconFlags { get; set; }
        public float? RepeatInterval { get; set; }
        public string? StartMessage { get; set; }
        public string? StopMessage { get; set; }
        public string? RepeatMessage { get; set; }
        public string? StartMessageType { get; set; }
        public string? StopMessageType { get; set; }
        public string? RepeatMessageType { get; set; }
        public string? Attributes { get; set; }
        public StatsDefinition? Stats { get; set; }
        public StaminaDrainModifierDefinition? StaminaDrainModifier { get; set; }
        public DamageTakenModifierDefinition? DamageTakenModifiers { get; set; }
        public StatusDamageDefinition? PercentageDamageModifiers { get; set; }
        public PoisonDefinition? Poison { get; set; }
        public ShieldStatusDefinition? Shield { get; set; }
        public FrostDefinition? Frost { get; set; }
        public RestedDefinition? Rested { get; set; }
        public HealthUpgradeDefinition? HealthUpgrade { get; set; }

        internal static StatusEffectReferenceEntry From(string name, StatusEffectDefinition definition)
        {
            StatusEffectReferenceEntry entry = new()
            {
                Effect = name,
                Stats = definition.Stats,
                StaminaDrainModifier = definition.StaminaDrainModifier,
                DamageTakenModifiers = definition.DamageTakenModifiers,
                PercentageDamageModifiers = definition.PercentageDamageModifiers,
                Poison = definition.Poison,
                Shield = definition.Shield,
                Frost = definition.Frost,
                Rested = definition.Rested,
                HealthUpgrade = definition.HealthUpgrade
            };
            entry.ApplyBase(ToReferenceBase(definition.Base));
            return ReferenceValue.ClonePruned(entry) ?? new StatusEffectReferenceEntry { Effect = name };
        }

        private void ApplyBase(StatusEffectBaseDefinition? definition)
        {
            if (definition == null)
            {
                return;
            }

            DisplayName = definition.DisplayName;
            Tooltip = definition.Tooltip;
            Icon = definition.Icon;
            StartEffects = definition.StartEffects;
            StopEffects = definition.StopEffects;
            Category = definition.Category;
            Time = definition.Time;
            IconFlags = definition.IconFlags;
            RepeatInterval = definition.RepeatInterval;
            StartMessage = definition.StartMessage;
            StopMessage = definition.StopMessage;
            RepeatMessage = definition.RepeatMessage;
            StartMessageType = definition.StartMessageType;
            StopMessageType = definition.StopMessageType;
            RepeatMessageType = definition.RepeatMessageType;
            Attributes = definition.Attributes;
        }

        private static StatusEffectBaseDefinition? ToReferenceBase(StatusEffectBaseDefinition? definition)
        {
            if (definition == null)
            {
                return null;
            }

            return new StatusEffectBaseDefinition
            {
                Category = definition.Category,
                StopEffects = definition.StopEffects,
                Time = definition.Time,
                IconFlags = definition.IconFlags,
                Attributes = definition.Attributes
            };
        }
    }

    internal sealed class StatusEffectDefinition
    {
        public StatusEffectBaseDefinition? Base { get; set; }
        public StatsDefinition? Stats { get; set; }
        public StaminaDrainModifierDefinition? StaminaDrainModifier { get; set; }
        public DamageTakenModifierDefinition? DamageTakenModifiers { get; set; }
        public StatusDamageDefinition? PercentageDamageModifiers { get; set; }
        public PoisonDefinition? Poison { get; set; }
        public ShieldStatusDefinition? Shield { get; set; }
        public FrostDefinition? Frost { get; set; }
        public RestedDefinition? Rested { get; set; }
        public HealthUpgradeDefinition? HealthUpgrade { get; set; }

        internal static StatusEffectDefinition From(StatusEffect statusEffect)
        {
            return new StatusEffectDefinition
            {
                Base = StatusEffectBaseDefinition.From(statusEffect),
                Stats = statusEffect is SE_Stats stats ? StatsDefinition.From(stats) : null,
                StaminaDrainModifier = statusEffect is SE_Stats staminaStats ? StaminaDrainModifierDefinition.From(staminaStats) : null,
                DamageTakenModifiers = statusEffect is SE_Stats damageTakenStats ? DamageTakenModifierDefinition.From(damageTakenStats.m_mods) : null,
                PercentageDamageModifiers = statusEffect is SE_Stats percentageStats ? StatusDamageDefinition.From(percentageStats.m_percentigeDamageModifiers) : null,
                Poison = statusEffect is SE_Poison poison ? PoisonDefinition.From(poison) : null,
                Shield = statusEffect is SE_Shield shield ? ShieldStatusDefinition.From(shield) : null,
                Frost = statusEffect is SE_Frost frost ? FrostDefinition.From(frost) : null,
                Rested = statusEffect is SE_Rested rested ? RestedDefinition.From(rested) : null,
                HealthUpgrade = statusEffect is SE_HealthUpgrade healthUpgrade ? HealthUpgradeDefinition.From(healthUpgrade) : null
            };
        }
    }

    internal sealed class StatusEffectBaseDefinition
    {
        public string? DisplayName { get; set; }
        public string? Tooltip { get; set; }
        public string? Icon { get; set; }
        public string? StartEffects { get; set; }
        public string? StopEffects { get; set; }
        public string? Category { get; set; }
        public string? Time { get; set; }
        public string? IconFlags { get; set; }
        public float? RepeatInterval { get; set; }
        public string? StartMessage { get; set; }
        public string? StopMessage { get; set; }
        public string? RepeatMessage { get; set; }
        public string? StartMessageType { get; set; }
        public string? StopMessageType { get; set; }
        public string? RepeatMessageType { get; set; }
        public string? Attributes { get; set; }

        internal static StatusEffectBaseDefinition From(StatusEffect statusEffect)
        {
            return new StatusEffectBaseDefinition
            {
                DisplayName = statusEffect.m_name,
                Tooltip = statusEffect.m_tooltip,
                StartEffects = FormatEffectList(statusEffect.m_startEffects),
                StopEffects = FormatEffectList(statusEffect.m_stopEffects),
                Category = statusEffect.m_category,
                Time = FormatFloatTuple(statusEffect.m_ttl, statusEffect.m_cooldown, null),
                IconFlags = FormatBoolTuple(statusEffect.m_cooldownIcon, statusEffect.m_flashIcon),
                RepeatInterval = statusEffect.m_repeatInterval,
                StartMessage = statusEffect.m_startMessage,
                StopMessage = statusEffect.m_stopMessage,
                RepeatMessage = statusEffect.m_repeatMessage,
                StartMessageType = statusEffect.m_startMessageType.ToString(),
                StopMessageType = statusEffect.m_stopMessageType.ToString(),
                RepeatMessageType = statusEffect.m_repeatMessageType.ToString(),
                Attributes = statusEffect.m_attributes.ToString()
            };
        }
    }

    internal sealed class StatsDefinition
    {
        public string? UpFront { get; set; }
        public string? HealthPerTick { get; set; }
        public string? HealthOverTime { get; set; }
        public string? StaminaOverTime { get; set; }
        public string? EitrOverTime { get; set; }
        public string? RegenMultiplier { get; set; }
        public float? StaminaDrainPerSec { get; set; }
        public float? AdrenalineModifier { get; set; }
        public float? SpeedModifier { get; set; }
        public float? SwimSpeedModifier { get; set; }
        public string? JumpModifier { get; set; }
        public string? WindRun { get; set; }
        public string? Sneak { get; set; }
        public string? Fall { get; set; }
        public string? Armor { get; set; }
        public string? Block { get; set; }
        public float? StaggerModifier { get; set; }
        public float? AddMaxCarryWeight { get; set; }
        public string? AttackDamage { get; set; }
        public string? RaiseSkill { get; set; }
        public string? SkillLevel { get; set; }
        public string? SkillLevel2 { get; set; }

        internal static StatsDefinition From(SE_Stats stats)
        {
            return new StatsDefinition
            {
                UpFront = FormatFloatTuple(stats.m_healthUpFront, stats.m_staminaUpFront, stats.m_eitrUpFront, stats.m_adrenalineUpFront),
                HealthPerTick = FormatHealthPerTick(stats),
                HealthOverTime = FormatFloatTuple(stats.m_healthOverTime, stats.m_healthOverTimeDuration, stats.m_healthOverTimeInterval),
                StaminaOverTime = FormatFloatBoolTuple(stats.m_staminaOverTime, stats.m_staminaOverTimeDuration, stats.m_staminaOverTimeIsFraction),
                EitrOverTime = FormatFloatTuple(stats.m_eitrOverTime, stats.m_eitrOverTimeDuration, null),
                RegenMultiplier = FormatFloatTuple(stats.m_healthRegenMultiplier, stats.m_staminaRegenMultiplier, stats.m_eitrRegenMultiplier),
                StaminaDrainPerSec = stats.m_staminaDrainPerSec,
                AdrenalineModifier = stats.m_adrenalineModifier,
                SpeedModifier = stats.m_speedModifier,
                SwimSpeedModifier = stats.m_swimSpeedModifier,
                JumpModifier = FormatFloatTuple(stats.m_jumpModifier.x, stats.m_jumpModifier.y, stats.m_jumpModifier.z),
                WindRun = FormatFloatTuple(stats.m_windMovementModifier, stats.m_windRunStaminaModifier, null),
                Sneak = FormatFloatTuple(stats.m_stealthModifier, stats.m_noiseModifier, null),
                Fall = FormatFloatTuple(stats.m_fallDamageModifier, stats.m_maxMaxFallSpeed, null),
                Armor = FormatFloatTuple(stats.m_addArmor, stats.m_armorMultiplier, null),
                Block = FormatFloatTuple(stats.m_timedBlockBonus, stats.m_blockStaminaUseFlatValue, null),
                StaggerModifier = stats.m_staggerModifier,
                AddMaxCarryWeight = stats.m_addMaxCarryWeight,
                AttackDamage = FormatSkillValuePair(stats.m_modifyAttackSkill, stats.m_damageModifier),
                RaiseSkill = FormatSkillValuePair(stats.m_raiseSkill, stats.m_raiseSkillModifier),
                SkillLevel = FormatSkillValuePair(stats.m_skillLevel, stats.m_skillLevelModifier),
                SkillLevel2 = FormatSkillValuePair(stats.m_skillLevel2, stats.m_skillLevelModifier2)
            };
        }
    }

    internal sealed class StaminaDrainModifierDefinition
    {
        public float? Run { get; set; }
        public float? Attack { get; set; }
        public float? Block { get; set; }
        public float? Dodge { get; set; }
        public float? Jump { get; set; }
        public float? Sneak { get; set; }
        public float? Swim { get; set; }
        public float? HomeItem { get; set; }

        internal static StaminaDrainModifierDefinition From(SE_Stats stats)
        {
            return new StaminaDrainModifierDefinition
            {
                Run = stats.m_runStaminaDrainModifier,
                Attack = stats.m_attackStaminaUseModifier,
                Block = stats.m_blockStaminaUseModifier,
                Dodge = stats.m_dodgeStaminaUseModifier,
                Jump = stats.m_jumpStaminaUseModifier,
                Sneak = stats.m_sneakStaminaUseModifier,
                Swim = stats.m_swimStaminaUseModifier,
                HomeItem = stats.m_homeItemStaminaUseModifier
            };
        }
    }

    internal sealed class PoisonDefinition
    {
        public float? BaseTtl { get; set; }
        public float? DamageInterval { get; set; }
        public float? DamagePerHit { get; set; }
        public float? TtlPerDamage { get; set; }
        public float? TtlPerDamagePlayer { get; set; }
        public float? TtlPower { get; set; }

        internal static PoisonDefinition From(SE_Poison poison)
        {
            return new PoisonDefinition
            {
                BaseTtl = poison.m_baseTTL,
                DamageInterval = poison.m_damageInterval,
                DamagePerHit = poison.m_damagePerHit,
                TtlPerDamage = poison.m_TTLPerDamage,
                TtlPerDamagePlayer = poison.m_TTLPerDamagePlayer,
                TtlPower = poison.m_TTLPower
            };
        }
    }

    internal sealed class ShieldStatusDefinition
    {
        public float? AbsorbDamage { get; set; }
        public float? AbsorbDamagePerSkillLevel { get; set; }
        public float? AbsorbDamageWorldLevel { get; set; }
        public int? TtlPerItemLevel { get; set; }
        public float? LevelUpSkillFactor { get; set; }
        public string? LevelUpSkillOnBreak { get; set; }

        internal static ShieldStatusDefinition From(SE_Shield shield)
        {
            return new ShieldStatusDefinition
            {
                AbsorbDamage = shield.m_absorbDamage,
                AbsorbDamagePerSkillLevel = shield.m_absorbDamagePerSkillLevel,
                AbsorbDamageWorldLevel = shield.m_absorbDamageWorldLevel,
                TtlPerItemLevel = shield.m_ttlPerItemLevel,
                LevelUpSkillFactor = shield.m_levelUpSkillFactor,
                LevelUpSkillOnBreak = shield.m_levelUpSkillOnBreak.ToString()
            };
        }
    }

    internal sealed class FrostDefinition
    {
        public float? FreezeTimeEnemy { get; set; }
        public float? FreezeTimePlayer { get; set; }
        public float? MinSpeedFactor { get; set; }

        internal static FrostDefinition From(SE_Frost frost)
        {
            return new FrostDefinition
            {
                FreezeTimeEnemy = frost.m_freezeTimeEnemy,
                FreezeTimePlayer = frost.m_freezeTimePlayer,
                MinSpeedFactor = frost.m_minSpeedFactor
            };
        }
    }

    internal sealed class RestedDefinition
    {
        public float? BaseTtl { get; set; }
        public float? TtlPerComfortLevel { get; set; }

        internal static RestedDefinition From(SE_Rested rested)
        {
            return new RestedDefinition
            {
                BaseTtl = rested.m_baseTTL,
                TtlPerComfortLevel = rested.m_TTLPerComfortLevel
            };
        }
    }

    internal sealed class HealthUpgradeDefinition
    {
        public float? MoreHealth { get; set; }
        public float? MoreStamina { get; set; }

        internal static HealthUpgradeDefinition From(SE_HealthUpgrade healthUpgrade)
        {
            return new HealthUpgradeDefinition
            {
                MoreHealth = healthUpgrade.m_moreHealth,
                MoreStamina = healthUpgrade.m_moreStamina
            };
        }
    }

    internal sealed class StatusDamageDefinition
    {
        public float? Blunt { get; set; }
        public float? Slash { get; set; }
        public float? Pierce { get; set; }
        public float? Chop { get; set; }
        public float? Pickaxe { get; set; }
        public float? Fire { get; set; }
        public float? Frost { get; set; }
        public float? Lightning { get; set; }
        public float? Poison { get; set; }
        public float? Spirit { get; set; }

        internal static StatusDamageDefinition From(HitData.DamageTypes damage)
        {
            return new StatusDamageDefinition
            {
                Blunt = damage.m_blunt,
                Slash = damage.m_slash,
                Pierce = damage.m_pierce,
                Chop = damage.m_chop,
                Pickaxe = damage.m_pickaxe,
                Fire = damage.m_fire,
                Frost = damage.m_frost,
                Lightning = damage.m_lightning,
                Poison = damage.m_poison,
                Spirit = damage.m_spirit
            };
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

        internal static DamageTakenModifierDefinition From(List<HitData.DamageModPair> mods)
        {
            HitData.DamageModifiers modifiers = new();
            modifiers.Apply(mods);

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


    private sealed class IconCacheEntry
    {
        public IconCacheEntry(DateTime lastWriteTimeUtc, Sprite sprite)
        {
            LastWriteTimeUtc = lastWriteTimeUtc;
            Sprite = sprite;
        }

        public DateTime LastWriteTimeUtc { get; }
        public Sprite Sprite { get; }
    }
}

[HarmonyPatch(typeof(SE_Stats), nameof(SE_Stats.GetTooltipString))]
internal static class DataForgeSeStatsTooltipPatch
{
    private const string AttackDamageToken = "$df_se_tooltip_attack_damage";
    private const string AttackDamageFallback = "{0} attack damage: <color=orange>x{1}%</color>";
    private const string RaiseSkillToken = "$df_se_tooltip_raise_skill";
    private const string RaiseSkillFallback = "{0} skill XP: <color=orange>{1}</color>";

    private static void Postfix(SE_Stats __instance, ref string __result)
    {
        try
        {
            if (__instance.m_modifyAttackSkill != Skills.SkillType.None &&
                !Mathf.Approximately(__instance.m_damageModifier, 1f))
            {
                AppendLine(
                    ref __result,
                    FormatLocalized(
                        AttackDamageToken,
                        AttackDamageFallback,
                        LocalizeSkill(__instance.m_modifyAttackSkill),
                        FormatUnsignedPercent(__instance.m_damageModifier)));
            }

            if (__instance.m_raiseSkill != Skills.SkillType.None &&
                !Mathf.Approximately(__instance.m_raiseSkillModifier, 0f))
            {
                AppendLine(
                    ref __result,
                    FormatLocalized(
                        RaiseSkillToken,
                        RaiseSkillFallback,
                        LocalizeSkill(__instance.m_raiseSkill),
                        FormatSignedPercent(__instance.m_raiseSkillModifier)));
            }
        }
        catch (Exception exception)
        {
            DataForgePlugin.Log.LogWarning($"Failed to append DataForge status effect tooltip lines: {exception.Message}");
        }
    }

    private static string LocalizeSkill(Skills.SkillType skill)
    {
        if (skill == Skills.SkillType.All)
        {
            return TryLocalize("$df_skill_all", "All");
        }

        string fallback = skill.ToString();
        string token = "$skill_" + fallback.ToLowerInvariant();
        return TryLocalize(token, fallback);
    }

    private static string TryLocalize(string token, string fallback)
    {
        Localization localization = Localization.instance;
        if (localization == null)
        {
            return fallback;
        }

        string localized = localization.Localize(token);
        return string.IsNullOrWhiteSpace(localized) ||
               localized.Equals(token, StringComparison.OrdinalIgnoreCase) ||
               localized.Equals(FormatMissingToken(token), StringComparison.OrdinalIgnoreCase)
            ? fallback
            : localized;
    }

    private static string FormatMissingToken(string token)
    {
        return "[" + token.TrimStart('$') + "]";
    }

    private static string FormatLocalized(string token, string fallback, params object[] args)
    {
        string template = TryLocalize(token, fallback);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.InvariantCulture, fallback, args);
        }
    }

    private static string FormatUnsignedPercent(float multiplier)
    {
        return FormatNumber(multiplier * 100f);
    }

    private static string FormatSignedPercent(float modifier)
    {
        return (modifier * 100f).ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatNumber(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static void AppendLine(ref string tooltip, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (string.IsNullOrEmpty(tooltip))
        {
            tooltip = line + "\n";
            return;
        }

        if (!tooltip.EndsWith("\n", StringComparison.Ordinal))
        {
            tooltip += "\n";
        }

        tooltip += line + "\n";
    }
}
