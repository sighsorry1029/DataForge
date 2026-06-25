using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;

namespace DataForge;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class DataForgePlugin : BaseUnityPlugin
{
    internal const string ModName = "DataForge";
    internal const string ModVersion = "1.0.3";
    internal const string Author = "sighsorry";
    internal const string ModGUID = $"{Author}.{ModName}";

    private static readonly string ConfigFileName = $"{ModGUID}.cfg";
    private static readonly string ConfigFileFullPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
    private static readonly ConfigSync ConfigSync = new(ModGUID)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion
    };

    private readonly Harmony _harmony = new(ModGUID);
    private readonly object _reloadLock = new();
    private FileSystemWatcher? _watcher;
    private DataForgeFileWatcher.DebouncedAction? _configReloadDebouncer;
    private string? _lastConfigFileText;
    private static bool _sourceOfTruthFileModeReady;

    internal static string ConnectionError = "";
    internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);

    private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;
    private const int MaxStoredFireplaceFuelLimit = 9999;
    private static ConfigEntry<Toggle> _serverConfigLocked = null!;
    private static ConfigEntry<Toggle> _enableItemOverrides = null!;
    private static ConfigEntry<Toggle> _enableRecipeOverrides = null!;
    private static ConfigEntry<Toggle> _enableStatusEffectOverrides = null!;
    private static ConfigEntry<Toggle> _enablePieceOverrides = null!;
    private static ConfigEntry<int> _stackableStackMultiplier = null!;
    private static ConfigEntry<float> _itemWeightMultiplier = null!;
    private static ConfigEntry<Toggle> _showPieceComfortInHammer = null!;
    private static ConfigEntry<Toggle> _highlightStationExtensionsInHammer = null!;
    private static ConfigEntry<Toggle> _ignoreStationExtensionSpacing = null!;
    private static ConfigEntry<int> _maxStoredFireplaceFuel = null!;
    private static ConfigEntry<Toggle> _logStartupTimings = null!;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    internal static bool IsSourceOfTruth => ConfigSync.IsSourceOfTruth;
    internal static bool UsesLocalAuthorityFiles => IsSourceOfTruth && !IsRemoteServerClient;
    internal static bool IsRemoteServerClient
    {
        get
        {
            try
            {
                return ZNet.HasServerHost() && (ZNet.instance == null || !ZNet.instance.IsServer());
            }
            catch
            {
                return false;
            }
        }
    }
    internal static bool ItemOverridesEnabled => _enableItemOverrides.Value.IsOn();
    internal static bool RecipeOverridesEnabled => _enableRecipeOverrides.Value.IsOn();
    internal static bool StatusEffectOverridesEnabled => _enableStatusEffectOverrides.Value.IsOn();
    internal static bool PieceOverridesEnabled => _enablePieceOverrides.Value.IsOn();
    internal static int StackableStackMultiplier => Math.Min(10, Math.Max(1, _stackableStackMultiplier.Value));
    internal static float ItemWeightMultiplier => Math.Min(2f, Math.Max(0f, _itemWeightMultiplier.Value));
    internal static bool ShowPieceComfortInHammer => _showPieceComfortInHammer.Value.IsOn();
    internal static bool HighlightStationExtensionsInHammer => _highlightStationExtensionsInHammer.Value.IsOn();
    internal static bool IgnoreStationExtensionSpacing => _ignoreStationExtensionSpacing.Value.IsOn();
    internal static int MaxStoredFireplaceFuel => Math.Min(MaxStoredFireplaceFuelLimit, Math.Max(0, _maxStoredFireplaceFuel.Value));
    internal static bool LogStartupTimings => _logStartupTimings.Value.IsOn();

    public void Awake()
    {
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        _serverConfigLocked = ConfigEntry(
            "1 - General",
            "Lock Configuration",
            Toggle.On,
            "If on, the configuration is locked and can be changed by server admins only.",
            order: 1000);
        _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);

        _enableItemOverrides = ConfigEntry(
            "1 - General",
            "Enable Item Overrides",
            Toggle.On,
            "If on, item YAML overrides are applied to matching ObjectDB item prefabs.",
            order: 900);
        _enableItemOverrides.SettingChanged += (_, _) => ItemOverrideManager.ApplyCurrentConfiguration();

        _enableRecipeOverrides = ConfigEntry(
            "1 - General",
            "Enable Recipe Overrides",
            Toggle.On,
            "If on, recipe YAML overrides are applied to ObjectDB recipes.",
            order: 800);
        _enableRecipeOverrides.SettingChanged += (_, _) => RecipeOverrideManager.ApplyCurrentConfiguration();

        _enableStatusEffectOverrides = ConfigEntry(
            "1 - General",
            "Enable Status Effect Overrides",
            Toggle.On,
            "If on, status effect YAML overrides are applied to ObjectDB status effects.",
            order: 700);
        _enableStatusEffectOverrides.SettingChanged += (_, _) => StatusEffectOverrideManager.ApplyCurrentConfiguration();

        _enablePieceOverrides = ConfigEntry(
            "1 - General",
            "Enable Piece Overrides",
            Toggle.On,
            "If on, piece YAML overrides are applied to matching prefabs and loaded pieces.",
            order: 600);
        _enablePieceOverrides.SettingChanged += (_, _) => PieceOverrideManager.ApplyCurrentConfiguration();

        _stackableStackMultiplier = ConfigEntry(
            "2 - Misc",
            "Stackable Stack Multiplier",
            1,
            new ConfigDescription(
                "Integer multiplier applied to baseline max stack size for stackable items unless maxStackSize is explicitly set in item YAML. 1 disables this feature.",
                new AcceptableValueRange<int>(1, 10)),
            order: 500);
        _stackableStackMultiplier.SettingChanged += (_, _) => ItemOverrideManager.ApplyCurrentConfiguration();

        _itemWeightMultiplier = ConfigEntry(
            "2 - Misc",
            "Item Weight Multiplier",
            1f,
            new ConfigDescription(
                "Multiplier applied to baseline item weight for all items unless weight is explicitly set in item YAML. 1 disables this feature; 0 makes affected items weightless.",
                new AcceptableValueRange<float>(0f, 2f)),
            order: 400);
        _itemWeightMultiplier.SettingChanged += (_, _) => ItemOverrideManager.ApplyCurrentConfiguration();

        _showPieceComfortInHammer = ConfigEntry(
            "2 - Misc",
            "Show Comfort In Hammer",
            Toggle.On,
            "If on, hammer build icons show an orange comfort value badge for pieces with comfort 1 or higher.",
            synchronizedSetting: false,
            order: 300);
        _showPieceComfortInHammer.SettingChanged += (_, _) => PieceComfortHudBadges.RefreshVisibleHud();

        _highlightStationExtensionsInHammer = ConfigEntry(
            "2 - Misc",
            "Highlight Station Extensions In Hammer",
            Toggle.On,
            "If on, hovering a crafting station or station extension in the hammer tab highlights related station/extension pieces in pale cyan. This setting is client-side only.",
            synchronizedSetting: false,
            order: 250);
        _highlightStationExtensionsInHammer.SettingChanged += (_, _) => PieceComfortHudBadges.RefreshVisibleHud();

        _ignoreStationExtensionSpacing = ConfigEntry(
            "2 - Misc",
            "Ignore Station Extension Spacing",
            Toggle.On,
            "If on, station extensions ignore the vanilla spacing check against other station extensions, allowing close or overlapping extension placement. Other placement restrictions remain unchanged.",
            order: 200);

        _maxStoredFireplaceFuel = ConfigEntry(
            "2 - Misc",
            "maxStoredFuel",
            100,
            $"Maximum stored fuel allowed in fireplaces without changing each fireplace's displayed max fuel. 0 disables this feature. Values are clamped to 0-{MaxStoredFireplaceFuelLimit}. If this value is not greater than a fireplace's max fuel, that fireplace uses vanilla behavior.",
            order: 100);
        _maxStoredFireplaceFuel.SettingChanged += (_, _) => ClampMaxStoredFireplaceFuel();
        ClampMaxStoredFireplaceFuel();

        _logStartupTimings = ConfigEntry(
            "2 - Misc",
            "Log Startup Timings",
            Toggle.Off,
            "If on, logs a lobby-to-world connection timeline plus DataForge synced payload parsing and world-data apply timings. Use only while diagnosing connection or loading delays.",
            synchronizedSetting: false,
            order: 50);

        LocalizationOverrideManager.Initialize(ConfigSync);
        StatusEffectOverrideManager.Initialize(ConfigSync);
        ItemOverrideManager.Initialize(ConfigSync);
        RecipeOverrideManager.Initialize(ConfigSync);
        PieceOverrideManager.Initialize(ConfigSync);
        ConfigSync.SourceOfTruthChanged += OnSourceOfTruthChanged;
        DataForgeConsoleCommands.Register();

        Assembly assembly = Assembly.GetExecutingAssembly();
        _harmony.PatchAll(assembly);
        SetupWatcher();

        Config.Save();
        _lastConfigFileText = ReadFileTextIfExists(ConfigFileFullPath);
        if (saveOnSet)
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void OnDestroy()
    {
        SaveWithRespectToConfigSet();
        _watcher?.Dispose();
        _watcher = null;
        _configReloadDebouncer?.Dispose();
        _configReloadDebouncer = null;
        LocalizationOverrideManager.Dispose();
        StatusEffectOverrideManager.Dispose();
        ItemOverrideManager.Dispose();
        RecipeOverrideManager.Dispose();
        PieceOverrideManager.Dispose();
        ConfigSync.SourceOfTruthChanged -= OnSourceOfTruthChanged;
        _harmony.UnpatchSelf();
    }

    private static void OnSourceOfTruthChanged(bool isSourceOfTruth)
    {
        if (isSourceOfTruth)
        {
            EnsureSourceOfTruthFileMode();
            return;
        }

        _sourceOfTruthFileModeReady = false;
        LocalizationOverrideManager.SetupFileWatcher();
        StatusEffectOverrideManager.SetupFileWatcher();
        ItemOverrideManager.SetupFileWatcher();
        RecipeOverrideManager.SetupFileWatcher();
        PieceOverrideManager.SetupFileWatcher();
    }

    internal static void EnsureSourceOfTruthFileMode()
    {
        if (!UsesLocalAuthorityFiles || _sourceOfTruthFileModeReady)
        {
            return;
        }

        _sourceOfTruthFileModeReady = true;
        LocalizationOverrideManager.SetupFileWatcher();
        StatusEffectOverrideManager.SetupFileWatcher();
        ItemOverrideManager.SetupFileWatcher();
        RecipeOverrideManager.SetupFileWatcher();
        PieceOverrideManager.SetupFileWatcher();
        LocalizationOverrideManager.ReloadFromDiskAndSync();
        StatusEffectOverrideManager.ReloadFromDiskAndSync();
        ItemOverrideManager.ReloadFromDiskAndSync();
        RecipeOverrideManager.ReloadFromDiskAndSync();
        PieceOverrideManager.ReloadFromDiskAndSync();
    }

    private void Update()
    {
        VneiPrefabCleanupGuard.TryPatchVneiIndexAll(_harmony);
    }

    private static void ClampMaxStoredFireplaceFuel()
    {
        int clamped = Math.Min(MaxStoredFireplaceFuelLimit, Math.Max(0, _maxStoredFireplaceFuel.Value));
        if (_maxStoredFireplaceFuel.Value != clamped)
        {
            _maxStoredFireplaceFuel.Value = clamped;
        }
    }

    private void SetupWatcher()
    {
        _watcher?.Dispose();
        _configReloadDebouncer?.Dispose();
        _configReloadDebouncer = DataForgeFileWatcher.CreateDebouncedAction(ReloadDelayTicks, ReloadConfigValues);
        _watcher = DataForgeFileWatcher.Create(Paths.ConfigPath, ConfigFileName, includeSubdirectories: false, ReadConfigValues);
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        _configReloadDebouncer?.Schedule();
    }

    private void ReloadConfigValues()
    {
        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                Log.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                string? configText = ReadFileTextIfExists(ConfigFileFullPath);
                if (string.Equals(_lastConfigFileText, configText, StringComparison.Ordinal))
                {
                    Log.LogDebug("Skipping configuration reload because the config file content did not change.");
                    return;
                }

                Log.LogDebug("Reloading configuration...");
                SaveWithRespectToConfigSet(reload: true);
                _lastConfigFileText = ReadFileTextIfExists(ConfigFileFullPath);
                StatusEffectOverrideManager.ApplyCurrentConfiguration();
                ItemOverrideManager.ApplyCurrentConfiguration();
                RecipeOverrideManager.ApplyCurrentConfiguration();
                PieceOverrideManager.ApplyCurrentConfiguration();
                Log.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                Log.LogError($"Error reloading configuration: {ex}");
            }
        }
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        if (reload)
        {
            Config.Reload();
        }
        else
        {
            Config.Save();
        }

        if (originalSaveOnSet)
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
    }

    private static string? ReadFileTextIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private ConfigEntry<T> ConfigEntry<T>(
        string group,
        string name,
        T value,
        ConfigDescription description,
        bool synchronizedSetting = true,
        int? order = null)
    {
        object[] tags = description.Tags ?? Array.Empty<object>();
        if (order is not null)
        {
            object[] orderedTags = new object[tags.Length + 1];
            Array.Copy(tags, orderedTags, tags.Length);
            orderedTags[tags.Length] = new ConfigurationManagerAttributes { Order = order.Value };
            tags = orderedTags;
        }

        ConfigDescription extendedDescription = new(
            description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
            description.AcceptableValues,
            tags);
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }

    private ConfigEntry<T> ConfigEntry<T>(
        string group,
        string name,
        T value,
        string description,
        bool synchronizedSetting = true,
        int? order = null)
    {
        return ConfigEntry(group, name, value, new ConfigDescription(description), synchronizedSetting, order);
    }

    private sealed class ConfigurationManagerAttributes
    {
        [UsedImplicitly] public int? Order = null!;
        [UsedImplicitly] public bool? Browsable = null!;
        [UsedImplicitly] public string? Category = null!;
        [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
    }
}

public static class ToggleExtensions
{
    public static bool IsOn(this DataForgePlugin.Toggle value)
    {
        return value == DataForgePlugin.Toggle.On;
    }

    public static bool IsOff(this DataForgePlugin.Toggle value)
    {
        return value == DataForgePlugin.Toggle.Off;
    }
}
