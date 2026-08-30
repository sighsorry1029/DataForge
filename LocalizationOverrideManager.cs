using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using ServerSync;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DataForge;

internal static class LocalizationOverrideManager
{
    private const string DomainName = "localization";
    private const string DefaultLanguageFileName = "English.yml";
    private const string KoreanLanguageFileName = "Korean.yml";
    private const string SyncedPayloadKey = "localization";
    private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;
    private const int PayloadVersion = 1;
    private const int MaxPayloadBytes = 2 * 1024 * 1024;
    private const int MaxLanguageCount = 32;
    private const int MaxLanguageNameLength = 64;
    private const int MaxTokensPerLanguage = 8192;
    private const int MaxTokenLength = 128;
    private const int MaxTextLength = 4096;
    private static readonly (string Token, string Text)[] BuiltInEnglishTranslations =
    {
        ("$df_se_tooltip_attack_damage", "{0} attack damage: <color=orange>x{1}%</color>"),
        ("$df_se_tooltip_raise_skill", "{0} skill XP: <color=orange>{1}</color>"),
        ("$df_se_tooltip_max_health", "Max health: <color=orange>{0}</color>"),
        ("$df_se_tooltip_max_stamina", "Max stamina: <color=orange>{0}</color>"),
        ("$df_se_tooltip_max_eitr", "Max eitr: <color=orange>{0}</color>"),
        ("$df_skill_all", "All")
    };
    private static readonly (string Token, string Text)[] BuiltInKoreanTranslations =
    {
        ("$df_se_tooltip_attack_damage", "{0} 공격 피해: <color=orange>x{1}%</color>"),
        ("$df_se_tooltip_raise_skill", "{0} 기술 경험치: <color=orange>{1}</color>"),
        ("$df_se_tooltip_max_health", "최대 체력: <color=orange>{0}</color>"),
        ("$df_se_tooltip_max_stamina", "최대 스태미나: <color=orange>{0}</color>"),
        ("$df_se_tooltip_max_eitr", "최대 에이트르: <color=orange>{0}</color>"),
        ("$df_skill_all", "전체")
    };

    private static readonly object StateLock = new();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static LocalizationPayload ActivePayload = CreateEmptyPayload();
    private static ConfigSync? ConfigSyncInstance;
    private static CustomSyncedValue<string>? SyncedPayload;
    private static FileSystemWatcher? Watcher;
    private static DataForgeFileWatcher.DebouncedAction? ReloadDebouncer;
    private static string? LastParsedPayload;
    private static bool LocalFileModeReady;

    private static readonly Dictionary<string, TranslationLease> AppliedTranslations =
        new(StringComparer.Ordinal);
    private static Localization? AppliedLocalization;
    private static string AppliedLanguage = "";

    private static string ConfigDirectory => Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName);
    private static string LocalizationDirectory => Path.Combine(ConfigDirectory, DomainName);
    private static bool HasLocalAuthority => ConfigSyncInstance?.IsSourceOfTruth == true;

    internal static void Initialize(ConfigSync configSync)
    {
        ConfigSyncInstance = configSync;
        SyncedPayload = new CustomSyncedValue<string>(configSync, SyncedPayloadKey, "", priority: 100);
        SyncedPayload.ValueChanged += OnSyncedPayloadChanged;
        configSync.SourceOfTruthChanged += OnSourceOfTruthChanged;
        _ = EnsureSourceOfTruthFileMode();
    }

    internal static void Dispose()
    {
        RestoreAppliedTranslations();
        if (SyncedPayload != null)
        {
            SyncedPayload.ValueChanged -= OnSyncedPayloadChanged;
            SyncedPayload = null;
        }

        if (ConfigSyncInstance != null)
        {
            ConfigSyncInstance.SourceOfTruthChanged -= OnSourceOfTruthChanged;
            ConfigSyncInstance = null;
        }

        Watcher?.Dispose();
        Watcher = null;
        ReloadDebouncer?.Dispose();
        ReloadDebouncer = null;
        LocalFileModeReady = false;
        lock (StateLock)
        {
            ActivePayload = CreateEmptyPayload();
            LastParsedPayload = null;
        }
    }

    internal static bool EnsureSourceOfTruthFileMode()
    {
        if (!HasLocalAuthority)
        {
            return false;
        }

        if (LocalFileModeReady)
        {
            return true;
        }

        LocalFileModeReady = true;
        try
        {
            SetupFileWatcher();
            DataForgeFileWatcher.CancelPendingRecreate("localization");
            if (!ReloadFromDiskAndSync())
            {
                NotifyLocalizationChanged();
                ReloadDebouncer?.Schedule();
            }

            return true;
        }
        catch (Exception ex)
        {
            LocalFileModeReady = false;
            Watcher?.Dispose();
            Watcher = null;
            ReloadDebouncer?.Dispose();
            ReloadDebouncer = null;
            DataForgePlugin.Log.LogError($"Failed to initialize server localization files: {ex}");
            return false;
        }
    }

    private static void SetupFileWatcher()
    {
        Watcher?.Dispose();
        Watcher = null;
        ReloadDebouncer?.Dispose();
        ReloadDebouncer = null;
        if (!HasLocalAuthority)
        {
            return;
        }

        EnsureConfigDirectoryAndDefaultOverride();
        ReloadDebouncer = DataForgeFileWatcher.CreateDebouncedAction(ReloadDelayTicks, ReloadYamlValues);
        Watcher = DataForgeFileWatcher.Create(
            LocalizationDirectory,
            "*.*",
            includeSubdirectories: false,
            ReadYamlValues,
            OnWatcherError);
    }

    private static bool ReloadFromDiskAndSync()
    {
        if (!HasLocalAuthority)
        {
            return false;
        }

        EnsureConfigDirectoryAndDefaultOverride();
        if (!TryLoadPayloadFromDisk(out LocalizationPayload payload))
        {
            return false;
        }
        if (!TrySerializeAndVerifyPayload(payload, out string serializedPayload, out LocalizationPayload verifiedPayload))
        {
            return false;
        }

        lock (StateLock)
        {
            ActivePayload = verifiedPayload;
            LastParsedPayload = serializedPayload;
        }

        PublishPayload(serializedPayload);
        ApplyCurrentLocalization();
        NotifyLocalizationChanged();
        return true;
    }

    internal static void ApplyCurrentLocalization()
    {
        Localization? localization = Localization.m_instance;
        if (localization == null)
        {
            return;
        }

        ApplyCurrentLocalization(localization, localization.GetSelectedLanguage());
    }

    internal static void ApplyCurrentLocalization(Localization localization, string? language)
    {
        if (!IsLiveLocalization(localization))
        {
            return;
        }

        string languageKey = NormalizeLanguage(language);
        if (!ReferenceEquals(AppliedLocalization, localization) ||
            !AppliedLanguage.Equals(languageKey, StringComparison.OrdinalIgnoreCase))
        {
            RestoreAppliedTranslations();
            AppliedLocalization = localization;
            AppliedLanguage = languageKey;
        }

        Dictionary<string, string> translations;
        lock (StateLock)
        {
            translations = BuildTranslationsForLanguage(ActivePayload, languageKey);
        }

        bool changed = RestoreRemovedTranslations(localization, translations.Keys);
        foreach (KeyValuePair<string, string> translation in translations)
        {
            changed |= ApplyTranslation(localization, translation.Key, translation.Value);
        }

        if (changed || translations.Count > 0)
        {
            localization.m_cache.EvictAll();
        }
    }

    internal static void BeforeLanguageSetup(Localization localization)
    {
        if (IsLiveLocalization(localization))
        {
            RestoreAppliedTranslations(localization);
        }
    }

    internal static void OnWorldShutdown()
    {
        bool remoteClient = ConfigSyncInstance?.IsSourceOfTruth == false;
        if (remoteClient)
        {
            LocalFileModeReady = false;
            ClearActivePayloadAndRestore();
        }
        else
        {
            RestoreAppliedTranslations();
        }
    }

    private static bool IsLiveLocalization(Localization localization)
    {
        return localization != null &&
               Localization.m_instance != null &&
               ReferenceEquals(localization, Localization.m_instance);
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
            if (ReloadFromDiskAndSync())
            {
                DataForgePlugin.Log.LogInfo("Localization YAML reload complete.");
            }
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Error reloading localization YAML files: {ex}");
        }
    }

    private static void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (!HasLocalAuthority)
        {
            return;
        }

        DataForgePlugin.Log.LogWarning(
            $"Localization file watcher lost events; scheduling a full reload: {e.GetException().Message}");
        if (!DataForgeFileWatcher.TryRecreate(
                "localization",
                () =>
                {
                    SetupFileWatcher();
                    LocalFileModeReady = true;
                    ReloadDebouncer?.Schedule();
                }))
        {
            LocalFileModeReady = false;
            ReloadYamlValues();
        }
    }

    private static bool ShouldReloadForFileEvent(FileSystemEventArgs e)
    {
        if (!HasLocalAuthority)
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
        if (HasLocalAuthority)
        {
            return;
        }

        string payload = SyncedPayload?.Value ?? "";
        ApplySyncedPayload(payload);
    }

    private static void OnSourceOfTruthChanged(bool isSourceOfTruth)
    {
        LocalFileModeReady = false;
        if (isSourceOfTruth)
        {
            ClearActivePayloadAndRestore();
            if (!HasLocalAuthority)
            {
                NotifyLocalizationChanged();
            }
            return;
        }

        Watcher?.Dispose();
        Watcher = null;
        ReloadDebouncer?.Dispose();
        ReloadDebouncer = null;
        ClearActivePayloadAndRestore();
        NotifyLocalizationChanged();
    }

    private static void ApplySyncedPayload(string payload)
    {
        if (!string.Equals(LastParsedPayload, payload, StringComparison.Ordinal))
        {
            if (!TryDeserializePayload(payload, "synced localization payload", out LocalizationPayload localizationPayload))
            {
                return;
            }

            lock (StateLock)
            {
                ActivePayload = localizationPayload;
                LastParsedPayload = payload;
            }
        }

        ApplyCurrentLocalization();
        NotifyLocalizationChanged();
    }

    private static void PublishPayload(string payload)
    {
        DataForgeSync.PublishPayload(SyncedPayload, DomainName, payload);
    }

    private static LocalizationPayload LoadPayloadFromDisk()
    {
        LocalizationPayload payload = CreateEmptyPayload();

        if (!Directory.Exists(LocalizationDirectory))
        {
            return payload;
        }

        string[] files = Directory.GetFiles(LocalizationDirectory, "*.yml")
            .Concat(Directory.GetFiles(LocalizationDirectory, "*.yaml"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length > MaxLanguageCount)
        {
            throw new InvalidDataException(
                $"Localization contains {files.Length} language files; the limit is {MaxLanguageCount}.");
        }

        long totalBytes = 0;
        HashSet<string> languages = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            FileInfo fileInfo = new(file);
            totalBytes += fileInfo.Length;
            if (fileInfo.Length > MaxPayloadBytes || totalBytes > MaxPayloadBytes)
            {
                throw new InvalidDataException(
                    $"Localization files exceed the {MaxPayloadBytes}-byte safety limit.");
            }

            string language = Path.GetFileNameWithoutExtension(file).Trim();
            if (language.Length == 0 || language.Length > MaxLanguageNameLength)
            {
                throw new InvalidDataException(
                    $"Localization language names must contain from 1 to {MaxLanguageNameLength} characters.");
            }
            if (!languages.Add(language))
            {
                throw new InvalidDataException(
                    $"Localization has more than one file for language '{language}' when compared case-insensitively.");
            }

            Dictionary<string, string> translations = LoadTranslationMap(file, $"{language} localization");
            payload.Languages![language] = translations;
        }

        return payload;
    }

    private static bool TryLoadPayloadFromDisk(out LocalizationPayload payload)
    {
        try
        {
            payload = LoadPayloadFromDisk();
            return true;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Localization reload failed; keeping the last-known-good configuration. {ex.Message}");
            payload = CreateEmptyPayload();
            return false;
        }
    }

    private static Dictionary<string, string> LoadTranslationMap(string path, string source)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        if (new FileInfo(path).Length > MaxPayloadBytes)
        {
            throw new InvalidDataException(
                $"{source} exceeds the {MaxPayloadBytes}-byte safety limit.");
        }

        string yaml = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            YamlStream stream = new();
            using StringReader reader = new(yaml);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            if (stream.Documents.Count != 1)
            {
                throw new FormatException($"{source} must contain exactly one YAML document.");
            }
            if (stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw new FormatException($"{source} must be a flat token-to-text mapping.");
            }
            if (root.Children.Count > MaxTokensPerLanguage)
            {
                throw new FormatException(
                    $"{source} contains {root.Children.Count} tokens; the limit is {MaxTokensPerLanguage}.");
            }

            Dictionary<string, string> normalized = new(StringComparer.Ordinal);
            HashSet<string> normalizedKeys = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
            {
                if (entry.Key is not YamlScalarNode keyNode)
                {
                    throw new FormatException($"{source} has an invalid token: token keys must be scalar strings.");
                }
                if (!TryNormalizeToken(keyNode.Value, out string token, out string tokenError))
                {
                    throw new FormatException($"{source} has an invalid token: {tokenError}");
                }
                if (!normalizedKeys.Add(token))
                {
                    throw new FormatException(
                        $"{source} has duplicate normalized token '{token}' when compared case-insensitively.");
                }
                if (entry.Value is not YamlScalarNode valueNode || string.IsNullOrEmpty(valueNode.Value))
                {
                    throw new FormatException(
                        $"{source} token '{token}' must have a non-empty scalar string value.");
                }

                string text = valueNode.Value!;
                if (text.Length > MaxTextLength)
                {
                    throw new FormatException(
                        $"{source} token '{token}' exceeds the {MaxTextLength}-character text limit.");
                }
                normalized[token] = text;
            }

            return normalized;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse {source} from '{path}': {ex.Message}", ex);
        }
    }

    private static bool TrySerializeAndVerifyPayload(
        LocalizationPayload payload,
        out string serialized,
        out LocalizationPayload verified)
    {
        serialized = "";
        verified = CreateEmptyPayload();
        try
        {
            serialized = Serializer.Serialize(payload);
            if (Encoding.UTF8.GetByteCount(serialized) > MaxPayloadBytes)
            {
                DataForgePlugin.Log.LogError(
                    $"Localization payload exceeds the {MaxPayloadBytes}-byte safety limit; keeping the last-known-good configuration.");
                return false;
            }
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Failed to serialize localization payload: {ex.Message}");
            return false;
        }

        return TryDeserializePayload(serialized, "local localization payload round-trip", out verified);
    }

    private static bool TryDeserializePayload(string payload, string source, out LocalizationPayload localizationPayload)
    {
        localizationPayload = CreateEmptyPayload();
        if (string.IsNullOrWhiteSpace(payload))
        {
            DataForgePlugin.Log.LogError($"{source} was rejected because the payload is empty.");
            return false;
        }
        if (Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            DataForgePlugin.Log.LogError(
                $"{source} was rejected because it exceeds the {MaxPayloadBytes}-byte safety limit.");
            return false;
        }

        try
        {
            LocalizationPayload? parsed = Deserializer.Deserialize<LocalizationPayload>(payload);
            localizationPayload = NormalizePayload(parsed, source);
            return true;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError(
                $"{source} was rejected; keeping the last-known-good configuration. {ex.Message}");
            return false;
        }
    }

    private static LocalizationPayload NormalizePayload(LocalizationPayload? payload, string source)
    {
        if (payload == null || payload.Version != PayloadVersion || payload.Languages == null)
        {
            string actualVersion = payload == null ? "missing" : payload.Version.ToString();
            throw new InvalidDataException(
                $"{source} must have version {PayloadVersion} and a languages mapping; received version {actualVersion}.");
        }
        if (payload.Languages.Count > MaxLanguageCount)
        {
            throw new InvalidDataException(
                $"{source} contains {payload.Languages.Count} languages; the limit is {MaxLanguageCount}.");
        }

        LocalizationPayload normalized = CreateEmptyPayload();
        HashSet<string> languageNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Dictionary<string, string>> languageEntry in payload.Languages)
        {
            string language = languageEntry.Key?.Trim() ?? "";
            if (language.Length == 0 || language.Length > MaxLanguageNameLength)
            {
                throw new InvalidDataException(
                    $"{source} language names must contain from 1 to {MaxLanguageNameLength} characters.");
            }
            if (!languageNames.Add(language))
            {
                throw new InvalidDataException(
                    $"{source} has duplicate language '{language}' when compared case-insensitively.");
            }
            if (languageEntry.Value == null || languageEntry.Value.Count > MaxTokensPerLanguage)
            {
                string count = languageEntry.Value == null ? "null" : languageEntry.Value.Count.ToString();
                throw new InvalidDataException(
                    $"{source} language '{language}' has {count} tokens; the limit is {MaxTokensPerLanguage}.");
            }

            Dictionary<string, string> translations = new(StringComparer.Ordinal);
            HashSet<string> normalizedKeys = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> translation in languageEntry.Value)
            {
                if (!TryNormalizeToken(translation.Key, out string token, out string tokenError))
                {
                    throw new InvalidDataException(
                        $"{source} language '{language}' has an invalid token: {tokenError}");
                }
                if (!normalizedKeys.Add(token))
                {
                    throw new InvalidDataException(
                        $"{source} language '{language}' has duplicate normalized token '{token}' when compared case-insensitively.");
                }
                if (string.IsNullOrEmpty(translation.Value) || translation.Value.Length > MaxTextLength)
                {
                    throw new InvalidDataException(
                        $"{source} language '{language}' token '{token}' must be non-empty and no longer than {MaxTextLength} characters.");
                }
                translations[token] = translation.Value;
            }

            normalized.Languages![language] = translations;
        }

        return normalized;
    }

    private static Dictionary<string, string> BuildTranslationsForLanguage(LocalizationPayload payload, string language)
    {
        Dictionary<string, string> translations = new(StringComparer.Ordinal);
        if (payload.Languages != null &&
            payload.Languages.TryGetValue("English", out Dictionary<string, string>? englishTranslations))
        {
            MergeTranslations(translations, englishTranslations);
        }

        if (!language.Equals("English", StringComparison.OrdinalIgnoreCase) &&
            payload.Languages != null &&
            payload.Languages.TryGetValue(language, out Dictionary<string, string>? languageTranslations))
        {
            MergeTranslations(translations, languageTranslations);
        }

        return translations;
    }

    private static void MergeTranslations(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        foreach (KeyValuePair<string, string> pair in source)
        {
            target[pair.Key] = pair.Value;
        }
    }

    private static bool ApplyTranslation(Localization localization, string token, string text)
    {
        bool currentExists = localization.m_translations.TryGetValue(token, out string? current);
        if (!AppliedTranslations.TryGetValue(token, out TranslationLease? lease) ||
            !currentExists ||
            !string.Equals(current, lease.LastAppliedValue, StringComparison.Ordinal))
        {
            lease = new TranslationLease(currentExists, current);
            AppliedTranslations[token] = lease;
        }

        bool changed = !currentExists || !string.Equals(current, text, StringComparison.Ordinal);
        localization.m_translations[token] = text;
        lease.LastAppliedValue = text;
        return changed;
    }

    private static bool RestoreRemovedTranslations(
        Localization localization,
        IEnumerable<string> currentTokens)
    {
        HashSet<string> current = new(currentTokens, StringComparer.Ordinal);
        bool changed = false;
        foreach (string token in AppliedTranslations.Keys.Where(token => !current.Contains(token)).ToArray())
        {
            changed |= RestoreTranslationIfOwned(localization, token);
            AppliedTranslations.Remove(token);
        }

        return changed;
    }

    private static void RestoreAppliedTranslations()
    {
        RestoreAppliedTranslations(AppliedLocalization);
    }

    private static void RestoreAppliedTranslations(Localization? localization)
    {
        if (localization == null)
        {
            ClearAppliedTranslationState();
            return;
        }

        bool changed = false;
        foreach (string token in AppliedTranslations.Keys.ToArray())
        {
            changed |= RestoreTranslationIfOwned(localization, token);
        }
        if (changed)
        {
            localization.m_cache.EvictAll();
        }

        ClearAppliedTranslationState();
    }

    private static bool RestoreTranslationIfOwned(Localization localization, string token)
    {
        if (!AppliedTranslations.TryGetValue(token, out TranslationLease? lease) ||
            !localization.m_translations.TryGetValue(token, out string? current) ||
            !string.Equals(current, lease.LastAppliedValue, StringComparison.Ordinal))
        {
            return false;
        }

        if (lease.OriginalExisted)
        {
            localization.m_translations[token] = lease.OriginalValue ?? "";
        }
        else
        {
            localization.m_translations.Remove(token);
        }

        return true;
    }

    private static void ClearAppliedTranslationState()
    {
        AppliedLocalization = null;
        AppliedLanguage = "";
        AppliedTranslations.Clear();
    }

    private static void ClearActivePayloadAndRestore()
    {
        lock (StateLock)
        {
            ActivePayload = CreateEmptyPayload();
            LastParsedPayload = null;
        }

        RestoreAppliedTranslations();
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

        EnsureBuiltInTranslations(path, BuiltInEnglishTranslations, "# Built-in DataForge tooltip tokens. You can edit these texts.");

        string koreanPath = Path.Combine(LocalizationDirectory, KoreanLanguageFileName);
        if (!File.Exists(koreanPath))
        {
            File.WriteAllText(koreanPath, DefaultKoreanLocalizationTemplate());
        }

        EnsureBuiltInTranslations(koreanPath, BuiltInKoreanTranslations, "# DataForge 기본 툴팁 토큰입니다. 원하는 문구로 수정할 수 있습니다.");
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
            "",
            "# Built-in DataForge tooltip tokens. You can edit these texts.",
            FormatBuiltInTranslationLines(BuiltInEnglishTranslations),
            "#",
            "# Example item override:",
            "# - item: MeadHealthtest",
            "#   cloneFrom: MeadHealthMajor",
            "#   name: $df_item_meadhealthtest",
            "#   description: Direct text override without localization",
            ""
        });
    }

    private static string FormatBuiltInTranslationLines(
        IEnumerable<(string Token, string Text)> translations)
    {
        return string.Join(
            Environment.NewLine,
            translations.Select(entry => $"{entry.Token}: {QuoteYaml(entry.Text)}"));
    }

    private static string DefaultKoreanLocalizationTemplate()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# DataForge 서버 동기화 localization 파일입니다.",
            "#",
            "# 이 폴더에는 Valheim 언어 이름을 파일명으로 사용합니다:",
            "# English.yml, Korean.yml, Turkish.yml, German.yml 등.",
            "#",
            "# English.yml은 기본 fallback 파일입니다. 클라이언트 언어가 한국어이면",
            "# DataForge는 English.yml을 먼저 적용한 뒤 Korean.yml을 덮어씁니다.",
            "#",
            "# override 필드에 $df_item_meadhealthtest 같은 토큰을 넣으면 이 파일의 번역을 사용합니다.",
            "# $ 토큰을 쓰지 않고 필드에 직접 텍스트를 넣어도 그대로 표시됩니다.",
            "#",
            "# 예시 localization 항목:",
            "# $df_item_meadhealthtest: \"테스트 아이템\"",
            "# $df_item_meadhealthtest_description: \"대형 체력 벌꿀주를 복제한 테스트 아이템입니다.\"",
            "",
            "# DataForge 기본 툴팁 토큰입니다. 원하는 문구로 수정할 수 있습니다.",
            FormatBuiltInTranslationLines(BuiltInKoreanTranslations),
            "#",
            "# 예시 item override:",
            "# - item: MeadHealthtest",
            "#   cloneFrom: MeadHealthMajor",
            "#   name: $df_item_meadhealthtest",
            "#   description: localization 토큰 없이 직접 입력한 설명",
            ""
        });
    }

    private static void EnsureBuiltInTranslations(
        string path,
        IReadOnlyCollection<(string Token, string Text)> translations,
        string header)
    {
        string yaml;
        Dictionary<string, string> existing;
        try
        {
            existing = LoadTranslationMap(path, $"built-in localization file '{Path.GetFileName(path)}'");
            yaml = File.ReadAllText(path);
        }
        catch
        {
            return;
        }

        HashSet<string> existingTokens = new(existing.Keys, StringComparer.OrdinalIgnoreCase);
        List<(string Token, string Text)> missing = translations
            .Where(entry => TryNormalizeToken(entry.Token, out string token, out _) &&
                            !existingTokens.Contains(token))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        using StreamWriter writer = File.AppendText(path);
        if (yaml.Length > 0 && !yaml.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            writer.WriteLine();
        }

        writer.WriteLine();
        writer.WriteLine(header);
        foreach ((string token, string text) in missing)
        {
            writer.WriteLine($"{token}: {QuoteYaml(text)}");
        }
    }

    private static string QuoteYaml(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
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
        return string.Equals(
            Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            localizationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeToken(string? value, out string token, out string error)
    {
        token = value?.Trim() ?? "";
        error = "";
        if (token.StartsWith("$", StringComparison.Ordinal))
        {
            token = token.Substring(1);
        }

        if (token.Length == 0 || token.Length > MaxTokenLength)
        {
            error = $"token names must contain from 1 to {MaxTokenLength} characters after an optional leading '$'.";
            return false;
        }

        const string tokenTerminators = " (){}[]+-!?/\\&%,.:-=<>\r\n\t";
        if (token.IndexOf('$') >= 0 ||
            token.Any(character => char.IsControl(character) || tokenTerminators.IndexOf(character) >= 0))
        {
            error = $"token '{token}' contains a character that terminates Valheim localization tokens.";
            return false;
        }

        return true;
    }

    private static string NormalizeLanguage(string? language)
    {
        string normalized = language?.Trim() ?? "";
        return normalized.Length == 0 ? "English" : normalized;
    }

    private static void NotifyLocalizationChanged()
    {
        if (Localization.m_instance == null || Localization.OnLanguageChange == null)
        {
            return;
        }

        foreach (Delegate subscriber in Localization.OnLanguageChange.GetInvocationList())
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception ex)
            {
                DataForgePlugin.Log.LogWarning(
                    $"A localized UI subscriber failed after a DataForge localization update: {ex.Message}");
            }
        }
    }

    private static LocalizationPayload CreateEmptyPayload()
    {
        return new LocalizationPayload
        {
            Version = PayloadVersion,
            Languages = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private sealed class TranslationLease
    {
        internal TranslationLease(bool originalExisted, string? originalValue)
        {
            OriginalExisted = originalExisted;
            OriginalValue = originalValue;
        }

        internal bool OriginalExisted { get; }
        internal string? OriginalValue { get; }
        internal string LastAppliedValue { get; set; } = "";
    }

    internal sealed class LocalizationPayload
    {
        public int Version { get; set; }
        public Dictionary<string, Dictionary<string, string>>? Languages { get; set; }
    }
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.SetupGui))]
internal static class DataForgeLocalizationFejdStartupPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        try
        {
            LocalizationOverrideManager.ApplyCurrentLocalization();
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning(
                $"Failed to apply DataForge localization to the startup UI: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
internal static class DataForgeLocalizationSetupLanguagePatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Localization __instance)
    {
        try
        {
            LocalizationOverrideManager.BeforeLanguageSetup(__instance);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning(
                $"Failed to prepare DataForge localization before a language change: {ex.Message}");
        }
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Localization __instance, string language)
    {
        try
        {
            LocalizationOverrideManager.ApplyCurrentLocalization(__instance, language);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning(
                $"Failed to apply DataForge localization for '{language}': {ex.Message}");
        }
    }
}
