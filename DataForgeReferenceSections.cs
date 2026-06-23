using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using YamlDotNet.Serialization;

namespace DataForge;

internal static class DataForgeReferenceSections
{
    internal const string VanillaOwnerName = "Valheim";
    internal const string UnknownOwnerName = "Unknown / Untracked";

    private sealed class GroupedEntry<TSource>
    {
        public TSource Entry { get; set; } = default!;
        public string SortKey { get; set; } = "";
        public string OwnerName { get; set; } = UnknownOwnerName;
    }

    internal static string SerializeReferenceSections<TSource, TOutput>(
        IEnumerable<TSource> entries,
        Func<TSource, string> getSortKey,
        Func<TSource, string> getOwnerName,
        Func<TSource, TOutput> getOutput,
        ISerializer serializer)
    {
        List<IGrouping<string, GroupedEntry<TSource>>> sections = entries
            .Select(entry =>
            {
                string ownerName = (getOwnerName(entry) ?? "").Trim();
                return new GroupedEntry<TSource>
                {
                    Entry = entry,
                    SortKey = (getSortKey(entry) ?? "").Trim(),
                    OwnerName = ownerName.Length > 0 ? ownerName : UnknownOwnerName
                };
            })
            .OrderBy(entry => GetOwnerSortBucket(entry.OwnerName))
            .ThenBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SortKey, StringComparer.OrdinalIgnoreCase)
            .GroupBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        StringBuilder builder = new();
        bool wroteSection = false;
        foreach (IGrouping<string, GroupedEntry<TSource>> section in sections)
        {
            if (wroteSection)
            {
                builder.AppendLine();
            }

            AppendSectionHeaderComment(builder, section.Key);
            foreach (GroupedEntry<TSource> entry in section)
            {
                string serializedEntry = CollapseScalarBlockListsToInlineLists(
                    serializer.Serialize(new[] { getOutput(entry.Entry) }).TrimEnd('\r', '\n'));
                builder.AppendLine(serializedEntry);
            }

            wroteSection = true;
        }

        return wroteSection ? builder.ToString() : "[]" + Environment.NewLine;
    }

    private static void AppendSectionHeaderComment(StringBuilder builder, string ownerName)
    {
        builder.Append("# ===== ");
        builder.Append(string.IsNullOrWhiteSpace(ownerName) ? UnknownOwnerName : ownerName.Trim());
        builder.AppendLine(" =====");
    }

    private static int GetOwnerSortBucket(string ownerName)
    {
        if (string.Equals(ownerName, VanillaOwnerName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return string.Equals(ownerName, UnknownOwnerName, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    private static string CollapseScalarBlockListsToInlineLists(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.IndexOf("- ", StringComparison.Ordinal) < 0)
        {
            return yaml;
        }

        string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
        StringBuilder builder = new();

        for (int index = 0; index < lines.Length; index++)
        {
            if (TryCollapseScalarBlockList(lines, ref index, out string collapsedLine))
            {
                builder.AppendLine(collapsedLine);
                continue;
            }

            builder.AppendLine(lines[index]);
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static bool TryCollapseScalarBlockList(string[] lines, ref int index, out string collapsedLine)
    {
        collapsedLine = "";
        string line = lines[index];
        int colonIndex = line.IndexOf(':');
        if (colonIndex < 0 || colonIndex != line.Length - 1)
        {
            return false;
        }

        int childIndex = index + 1;
        if (childIndex >= lines.Length)
        {
            return false;
        }

        int parentIndent = GetFirstNonWhitespaceIndex(line);
        int childIndent = GetFirstNonWhitespaceIndex(lines[childIndex]);
        if (parentIndent < 0 || childIndent <= parentIndent || !lines[childIndex].TrimStart().StartsWith("- ", StringComparison.Ordinal))
        {
            return false;
        }

        List<string> values = new();
        int scanIndex = childIndex;
        while (scanIndex < lines.Length)
        {
            string childLine = lines[scanIndex];
            int currentIndent = GetFirstNonWhitespaceIndex(childLine);
            if (currentIndent != childIndent || !childLine.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            string value = childLine.TrimStart().Substring(2).Trim();
            if (value.Length == 0 || value.Contains(':') || value.Contains(','))
            {
                return false;
            }

            values.Add(value);
            scanIndex++;
        }

        if (values.Count == 0)
        {
            return false;
        }

        collapsedLine = line + " [" + string.Join(", ", values) + "]";
        index = scanIndex - 1;
        return true;
    }

    private static int GetFirstNonWhitespaceIndex(string line)
    {
        for (int index = 0; index < line.Length; index++)
        {
            if (!char.IsWhiteSpace(line[index]))
            {
                return index;
            }
        }

        return -1;
    }
}

internal static class DataForgeOwnerResolver
{
    internal static string GetPrefabOwnerName(string? prefabName)
    {
        string normalizedName = NormalizeName(prefabName);
        if (normalizedName.Length == 0)
        {
            return DataForgeReferenceSections.UnknownOwnerName;
        }

        foreach (string candidate in EnumerateLookupCandidates(normalizedName))
        {
            if (DataForgeVanillaAssetCatalog.IsVanillaPrefab(candidate))
            {
                return DataForgeReferenceSections.VanillaOwnerName;
            }
        }

        return DataForgeAssetOwnerCatalog.GetOwnerName(normalizedName);
    }

    internal static string GetAssetOwnerName(string? assetName)
    {
        string normalizedName = NormalizeName(assetName);
        if (normalizedName.Length == 0)
        {
            return DataForgeReferenceSections.UnknownOwnerName;
        }

        foreach (string candidate in EnumerateLookupCandidates(normalizedName))
        {
            if (DataForgeVanillaAssetCatalog.IsVanillaAsset(candidate))
            {
                return DataForgeReferenceSections.VanillaOwnerName;
            }
        }

        return DataForgeAssetOwnerCatalog.GetOwnerName(normalizedName);
    }

    private static IEnumerable<string> EnumerateLookupCandidates(string normalizedName)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        AddIfNew(normalizedName);
        AddIfNew(TrimCloneSuffix(normalizedName));

        int aliasSeparatorIndex = normalizedName.IndexOf(':');
        if (aliasSeparatorIndex > 0)
        {
            AddIfNew(normalizedName.Substring(0, aliasSeparatorIndex));
        }

        foreach (string candidate in seen)
        {
            yield return candidate;
        }

        void AddIfNew(string candidate)
        {
            string normalizedCandidate = NormalizeName(candidate);
            if (normalizedCandidate.Length > 0)
            {
                seen.Add(normalizedCandidate);
            }
        }
    }

    private static string NormalizeName(string? name)
    {
        return (name ?? "").Replace("(Clone)", "").Trim();
    }

    private static string TrimCloneSuffix(string name)
    {
        const string cloneSuffix = "(Clone)";
        return name.EndsWith(cloneSuffix, StringComparison.Ordinal)
            ? name.Substring(0, name.Length - cloneSuffix.Length).TrimEnd()
            : name;
    }
}

internal static class DataForgeVanillaAssetCatalog
{
    private enum CatalogState
    {
        Uninitialized,
        Loaded,
        Unavailable
    }

    private static readonly object Sync = new();
    private static readonly HashSet<string> PrefabNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AssetNames = new(StringComparer.OrdinalIgnoreCase);
    private static CatalogState _state;

    internal static bool IsVanillaPrefab(string prefabName)
    {
        EnsureLoaded();
        return _state == CatalogState.Loaded &&
               !string.IsNullOrWhiteSpace(prefabName) &&
               PrefabNames.Contains(prefabName);
    }

    internal static bool IsVanillaAsset(string assetName)
    {
        EnsureLoaded();
        return _state == CatalogState.Loaded &&
               !string.IsNullOrWhiteSpace(assetName) &&
               AssetNames.Contains(assetName);
    }

    private static void EnsureLoaded()
    {
        if (_state != CatalogState.Uninitialized)
        {
            return;
        }

        lock (Sync)
        {
            if (_state != CatalogState.Uninitialized)
            {
                return;
            }

            string manifestPath = Path.Combine(Application.dataPath, "StreamingAssets", "SoftRef", "manifest_extended");
            if (!File.Exists(manifestPath))
            {
                _state = CatalogState.Unavailable;
                DataForgePlugin.Log.LogWarning($"Vanilla asset manifest was not found at '{manifestPath}'. Reference owner sections may place unmapped entries under '{DataForgeReferenceSections.UnknownOwnerName}'.");
                return;
            }

            const string marker = "path in bundle:";
            foreach (string rawLine in File.ReadLines(manifestPath))
            {
                int markerIndex = rawLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                string assetPath = rawLine.Substring(markerIndex + marker.Length).Trim();
                string assetName = Path.GetFileNameWithoutExtension(assetPath);
                if (string.IsNullOrWhiteSpace(assetName))
                {
                    continue;
                }

                if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    PrefabNames.Add(assetName);
                }
                else if (assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    AssetNames.Add(assetName);
                }
            }

            _state = CatalogState.Loaded;
            DataForgePlugin.Log.LogDebug($"Loaded {PrefabNames.Count} vanilla prefab names and {AssetNames.Count} vanilla asset names from '{manifestPath}'.");
        }
    }
}

internal static class DataForgeAssetOwnerCatalog
{
    private sealed class PluginResourceSnapshot
    {
        public string OwnerName { get; set; } = "";
        public string PluginName { get; set; } = "";
        public string PluginGuid { get; set; } = "";
        public string AssemblyName { get; set; } = "";
        public string[] ResourceNames { get; set; } = Array.Empty<string>();
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> AssetOwners = new(StringComparer.OrdinalIgnoreCase);
    private static string _loadedSignature = "";

    internal static string GetOwnerName(string assetName)
    {
        EnsureMappingsLoaded();
        foreach (string candidate in EnumerateLookupCandidates(assetName))
        {
            if (AssetOwners.TryGetValue(candidate, out string ownerName) &&
                !string.IsNullOrWhiteSpace(ownerName))
            {
                return ownerName;
            }
        }

        return DataForgeReferenceSections.UnknownOwnerName;
    }

    private static void EnsureMappingsLoaded()
    {
        string signature = BuildSignature();
        if (string.Equals(signature, _loadedSignature, StringComparison.Ordinal))
        {
            return;
        }

        lock (Sync)
        {
            if (string.Equals(signature, _loadedSignature, StringComparison.Ordinal))
            {
                return;
            }

            AssetOwners.Clear();
            List<PluginResourceSnapshot> plugins = GetPluginResources();
            foreach (AssetBundle assetBundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                string bundleName = assetBundle.name ?? "";
                if (bundleName.Length == 0)
                {
                    continue;
                }

                string ownerName = ResolveOwnerName(bundleName, plugins);
                if (string.IsNullOrWhiteSpace(ownerName))
                {
                    continue;
                }

                foreach (string assetPath in assetBundle.GetAllAssetNames())
                {
                    if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                        !assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string assetName = Path.GetFileNameWithoutExtension(assetPath);
                    if (!string.IsNullOrWhiteSpace(assetName))
                    {
                        AssetOwners[assetName] = ownerName;
                    }
                }
            }

            _loadedSignature = signature;
            DataForgePlugin.Log.LogDebug($"Tracked {AssetOwners.Count} mod asset owner mapping(s) for reference sections.");
        }
    }

    private static IEnumerable<string> EnumerateLookupCandidates(string assetName)
    {
        string normalizedName = (assetName ?? "").Replace("(Clone)", "").Trim();
        if (normalizedName.Length == 0)
        {
            yield break;
        }

        yield return normalizedName;
        int aliasSeparatorIndex = normalizedName.IndexOf(':');
        if (aliasSeparatorIndex > 0)
        {
            yield return normalizedName.Substring(0, aliasSeparatorIndex);
        }
    }

    private static List<PluginResourceSnapshot> GetPluginResources()
    {
        return Chainloader.PluginInfos.Values
            .Select(pluginInfo =>
            {
                string pluginName = (pluginInfo.Metadata.Name ?? "").Trim();
                string pluginGuid = (pluginInfo.Metadata.GUID ?? "").Trim();
                string assemblyName = "";
                string[] resourceNames = Array.Empty<string>();
                try
                {
                    assemblyName = pluginInfo.Instance?.GetType().Assembly.GetName().Name ?? "";
                    resourceNames = pluginInfo.Instance?.GetType().Assembly.GetManifestResourceNames() ?? Array.Empty<string>();
                }
                catch
                {
                    // Some plugin assemblies can be in a partially initialized state while ObjectDB is being copied.
                }

                return new PluginResourceSnapshot
                {
                    OwnerName = pluginName.Length > 0 ? pluginName : pluginGuid,
                    PluginName = pluginName,
                    PluginGuid = pluginGuid,
                    AssemblyName = assemblyName,
                    ResourceNames = resourceNames
                };
            })
            .Where(plugin => plugin.OwnerName.Length > 0)
            .ToList();
    }

    private static string ResolveOwnerName(string bundleName, List<PluginResourceSnapshot> plugins)
    {
        PluginResourceSnapshot? embeddedOwner = plugins.FirstOrDefault(plugin =>
            plugin.ResourceNames.Any(resourceName =>
                resourceName.EndsWith(bundleName, StringComparison.OrdinalIgnoreCase)));
        if (embeddedOwner != null)
        {
            return embeddedOwner.OwnerName;
        }

        string normalizedBundleName = NormalizeToken(Path.GetFileNameWithoutExtension(bundleName));
        if (normalizedBundleName.Length == 0)
        {
            return "";
        }

        PluginResourceSnapshot? tokenOwner = plugins.FirstOrDefault(plugin =>
        {
            string normalizedPluginName = NormalizeToken(plugin.PluginName);
            string normalizedPluginGuid = NormalizeToken(plugin.PluginGuid);
            string normalizedAssemblyName = NormalizeToken(plugin.AssemblyName);
            return IsTokenMatch(normalizedBundleName, normalizedPluginName) ||
                   IsTokenMatch(normalizedBundleName, normalizedPluginGuid) ||
                   IsTokenMatch(normalizedBundleName, normalizedAssemblyName);
        });

        return tokenOwner?.OwnerName ?? "";
    }

    private static bool IsTokenMatch(string bundleName, string pluginToken)
    {
        return pluginToken.Length > 0 &&
               (bundleName.IndexOf(pluginToken, StringComparison.OrdinalIgnoreCase) >= 0 ||
                pluginToken.IndexOf(bundleName, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        StringBuilder builder = new();
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string BuildSignature()
    {
        IEnumerable<string> bundleTokens = AssetBundle.GetAllLoadedAssetBundles()
            .Select(bundle => bundle.name ?? "")
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> pluginTokens = Chainloader.PluginInfos.Values
            .Select(pluginInfo =>
            {
                string pluginName = pluginInfo.Metadata.Name ?? "";
                string pluginGuid = pluginInfo.Metadata.GUID ?? "";
                string assemblyName = "";
                try
                {
                    assemblyName = pluginInfo.Instance?.GetType().Assembly.GetName().Name ?? "";
                }
                catch
                {
                    // The signature only needs to notice stable ownership inputs; ignore transient reflection failures.
                }

                return $"{pluginGuid}:{pluginName}:{assemblyName}";
            })
            .OrderBy(token => token, StringComparer.OrdinalIgnoreCase);

        return string.Join("|", bundleTokens) + "||" + string.Join("|", pluginTokens);
    }
}
