using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using ServerSync;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DataForge;

internal static class LocalizationOverrideManager
{
    private const string DomainName = "localization";
    private const string DefaultLanguageFileName = "English.yml";
    private const string SyncedPayloadKey = "localization";
    private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;

    private static readonly object StateLock = new();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static LocalizationPayload ActivePayload = new();
    private static CustomSyncedValue<string>? SyncedPayload;
    private static FileSystemWatcher? Watcher;
    private static DataForgeFileWatcher.DebouncedAction? ReloadDebouncer;
    private static string? LastParsedPayload;

    private static readonly Dictionary<string, Dictionary<string, string?>> OriginalTranslationsByLanguage =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> AppliedKeysByLanguage =
        new(StringComparer.OrdinalIgnoreCase);

    private static string ConfigDirectory => Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName);
    private static string LocalizationDirectory => Path.Combine(ConfigDirectory, DomainName);

    internal static void Initialize(ConfigSync configSync)
    {
        SyncedPayload = new CustomSyncedValue<string>(configSync, SyncedPayloadKey, "", priority: 100);
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
        LocalizationPayload payload = LoadPayloadFromDisk();
        string serializedPayload = SerializePayload(payload);
        lock (StateLock)
        {
            ActivePayload = payload;
            LastParsedPayload = serializedPayload;
        }

        PublishPayload(serializedPayload);
        ApplyCurrentLocalization();
    }

    internal static void ApplyCurrentLocalization()
    {
        Localization localization = Localization.instance;
        if (localization == null)
        {
            return;
        }

        ApplyCurrentLocalization(localization, localization.GetSelectedLanguage());
    }

    internal static void ApplyCurrentLocalization(Localization localization, string? language)
    {
        if (localization == null)
        {
            return;
        }

        string languageKey = NormalizeLanguage(language);
        Dictionary<string, string> translations;
        lock (StateLock)
        {
            translations = BuildTranslationsForLanguage(ActivePayload, languageKey);
        }

        RestoreRemovedTranslations(localization, languageKey, translations.Keys);
        foreach (KeyValuePair<string, string> translation in translations)
        {
            ApplyTranslation(localization, languageKey, translation.Key, translation.Value);
        }

        AppliedKeysByLanguage[languageKey] = new HashSet<string>(translations.Keys, StringComparer.OrdinalIgnoreCase);
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
            DataForgePlugin.Log.LogDebug("Reloading localization YAML files...");
            ReloadFromDiskAndSync();
            DataForgePlugin.Log.LogInfo("Localization YAML reload complete.");
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Error reloading localization YAML files: {ex}");
        }
    }

    private static bool ShouldReloadForFileEvent(FileSystemEventArgs e)
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles)
        {
            return false;
        }

        if (IsLocalizationFile(e.FullPath))
        {
            return true;
        }

        return e is RenamedEventArgs renamed && IsLocalizationFile(renamed.OldFullPath);
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
        if (!string.Equals(LastParsedPayload, payload, StringComparison.Ordinal))
        {
            LocalizationPayload localizationPayload = DeserializePayload(payload, "synced localization payload");
            lock (StateLock)
            {
                ActivePayload = localizationPayload;
                LastParsedPayload = payload;
            }
        }

        ApplyCurrentLocalization();
    }

    private static void PublishPayload(string payload)
    {
        DataForgeSync.PublishPayload(SyncedPayload, DomainName, payload);
    }

    private static LocalizationPayload LoadPayloadFromDisk()
    {
        LocalizationPayload payload = new();

        if (!Directory.Exists(LocalizationDirectory))
        {
            return payload;
        }

        foreach (string file in Directory.GetFiles(LocalizationDirectory, "*.yml")
                     .Concat(Directory.GetFiles(LocalizationDirectory, "*.yaml"))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string language = Path.GetFileNameWithoutExtension(file);
            Dictionary<string, string> translations = LoadTranslationMap(file, $"{language} localization");
            if (translations.Count == 0)
            {
                continue;
            }

            if (!payload.Languages.TryGetValue(language, out Dictionary<string, string>? languageTranslations))
            {
                languageTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                payload.Languages[language] = languageTranslations;
            }

            MergeTranslations(languageTranslations, translations);
        }

        return payload;
    }

    private static Dictionary<string, string> LoadTranslationMap(string path, string source)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        string yaml = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Dictionary<string, string?>? map = Deserializer.Deserialize<Dictionary<string, string?>>(yaml);
            return NormalizeTranslationMap(map, source);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Failed to parse {source} from '{path}': {ex.Message}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> NormalizeTranslationMap(Dictionary<string, string?>? map, string source)
    {
        Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
        if (map == null)
        {
            return normalized;
        }

        foreach (KeyValuePair<string, string?> pair in map)
        {
            string token = NormalizeToken(pair.Key);
            if (token.Length == 0)
            {
                DataForgePlugin.Log.LogWarning($"Skipping localization entry with an empty token in {source}.");
                continue;
            }

            normalized[token] = pair.Value ?? "";
        }

        return normalized;
    }

    private static string SerializePayload(LocalizationPayload payload)
    {
        return Serializer.Serialize(payload);
    }

    private static LocalizationPayload DeserializePayload(string payload, string source)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new LocalizationPayload();
        }

        try
        {
            LocalizationPayload? parsed = Deserializer.Deserialize<LocalizationPayload>(payload);
            return NormalizePayload(parsed, source);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Failed to parse {source}: {ex.Message}");
            return new LocalizationPayload();
        }
    }

    private static LocalizationPayload NormalizePayload(LocalizationPayload? payload, string source)
    {
        LocalizationPayload normalized = new();
        if (payload == null)
        {
            return normalized;
        }

        MergeTranslations(normalized.All, NormalizeStringTranslationMap(payload.All, $"{source} common"));
        foreach (KeyValuePair<string, Dictionary<string, string>> language in payload.Languages)
        {
            string languageName = NormalizeLanguage(language.Key);
            if (languageName.Length == 0)
            {
                continue;
            }

            normalized.Languages[languageName] = NormalizeStringTranslationMap(language.Value, $"{source} {languageName}");
        }

        return normalized;
    }

    private static Dictionary<string, string> NormalizeStringTranslationMap(Dictionary<string, string>? map, string source)
    {
        return NormalizeTranslationMap(map?.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase), source);
    }

    private static Dictionary<string, string> BuildTranslationsForLanguage(LocalizationPayload payload, string language)
    {
        Dictionary<string, string> translations = new(StringComparer.OrdinalIgnoreCase);
        MergeTranslations(translations, payload.All);
        if (!language.Equals("English", StringComparison.OrdinalIgnoreCase) &&
            payload.Languages.TryGetValue("English", out Dictionary<string, string>? englishTranslations))
        {
            MergeTranslations(translations, englishTranslations);
        }

        if (payload.Languages.TryGetValue(language, out Dictionary<string, string>? languageTranslations))
        {
            MergeTranslations(translations, languageTranslations);
        }

        return translations;
    }

    private static void MergeTranslations(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        foreach (KeyValuePair<string, string> pair in source)
        {
            target[NormalizeToken(pair.Key)] = pair.Value;
        }
    }

    private static void ApplyTranslation(Localization localization, string language, string token, string text)
    {
        token = NormalizeToken(token);
        if (token.Length == 0)
        {
            return;
        }

        Dictionary<string, string?> originalTranslations = GetOriginalTranslations(language);
        if (!originalTranslations.ContainsKey(token))
        {
            originalTranslations[token] = localization.m_translations.TryGetValue(token, out string? originalText)
                ? originalText
                : null;
        }

        localization.m_translations[token] = text;
    }

    private static void RestoreRemovedTranslations(
        Localization localization,
        string language,
        IEnumerable<string> currentTokens)
    {
        HashSet<string> current = new(currentTokens.Select(NormalizeToken), StringComparer.OrdinalIgnoreCase);
        if (!AppliedKeysByLanguage.TryGetValue(language, out HashSet<string>? previous))
        {
            return;
        }

        Dictionary<string, string?> originalTranslations = GetOriginalTranslations(language);
        foreach (string token in previous.Where(token => !current.Contains(token)).ToArray())
        {
            if (!originalTranslations.TryGetValue(token, out string? originalText))
            {
                continue;
            }

            if (originalText == null)
            {
                localization.m_translations.Remove(token);
            }
            else
            {
                localization.m_translations[token] = originalText;
            }

            originalTranslations.Remove(token);
        }
    }

    private static Dictionary<string, string?> GetOriginalTranslations(string language)
    {
        if (!OriginalTranslationsByLanguage.TryGetValue(language, out Dictionary<string, string?>? translations))
        {
            translations = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            OriginalTranslationsByLanguage[language] = translations;
        }

        return translations;
    }

    private static void EnsureConfigDirectoryAndDefaultOverride()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LocalizationDirectory);
        string path = Path.Combine(LocalizationDirectory, DefaultLanguageFileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, DefaultEnglishLocalizationTemplate());
        }
    }

    private static string DefaultEnglishLocalizationTemplate()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# DataForge server-synced localization.",
            "#",
            "# Put language files in this folder using Valheim language names:",
            "# English.yml, Korean.yml, Turkish.yml, German.yml, etc.",
            "#",
            "# English.yml is the fallback file. If a client uses another language,",
            "# DataForge first applies English.yml and then applies that client's language file.",
            "#",
            "# To use a localization key, put a token like $df_item_meadhealthtest in an override field.",
            "# To override text directly, put plain text in the field instead of a $ token.",
            "#",
            "# Example localization entry:",
            "# $df_item_meadhealthtest: \"Test item\"",
            "# $df_item_meadhealthtest_description: \"A test item cloned from major healing mead.\"",
            "#",
            "# Example item override:",
            "# - item: MeadHealthtest",
            "#   cloneFrom: MeadHealthMajor",
            "#   name: $df_item_meadhealthtest",
            "#   description: Direct text override without localization",
            ""
        });
    }

    private static bool IsLocalizationFile(string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        string localizationRoot = Path.GetFullPath(LocalizationDirectory);
        return fullPath.StartsWith(localizationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeToken(string token)
    {
        return (token ?? "").Trim().TrimStart('$').Trim();
    }

    private static string NormalizeLanguage(string? language)
    {
        string normalized = language?.Trim() ?? "";
        return normalized.Length == 0 ? "English" : normalized;
    }

    internal sealed class LocalizationPayload
    {
        public Dictionary<string, string> All { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> Languages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

[HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
internal static class DataForgeLocalizationSetupLanguagePatch
{
    private static void Postfix(Localization __instance, string language)
    {
        LocalizationOverrideManager.ApplyCurrentLocalization(__instance, language);
    }
}
