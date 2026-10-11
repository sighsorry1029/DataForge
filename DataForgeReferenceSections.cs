using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using YamlDotNet.Serialization;
using ModAssetOwnership;

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
        DataForgeAssetOwnerCatalog.PrepareForReferenceGeneration();
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
            .ThenBy(entry => entry.OwnerName, StringComparer.Ordinal)
            .ThenBy(entry => entry.SortKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SortKey, StringComparer.Ordinal)
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
        yield return normalizedName;

        int aliasSeparatorIndex = normalizedName.IndexOf(':');
        if (aliasSeparatorIndex > 0)
        {
            string alias = NormalizeName(normalizedName.Substring(0, aliasSeparatorIndex));
            if (alias.Length > 0 &&
                !alias.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                yield return alias;
            }
        }
    }

    private static string NormalizeName(string? name)
    {
        return (name ?? "").Replace("(Clone)", "").Trim();
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
    private static readonly object Sync = new();
    private static readonly Dictionary<string, AssetOwner> AssetOwners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AmbiguousAssetNames = new(StringComparer.OrdinalIgnoreCase);
    private static string _loadedSignature = "";
    private static bool _mappingsInitialized;

    internal static void PrepareForReferenceGeneration()
    {
        EnsureMappingsLoaded();
    }

    internal static string GetOwnerName(string assetName)
    {
        if (!_mappingsInitialized)
        {
            EnsureMappingsLoaded();
        }

        foreach (string candidate in EnumerateLookupCandidates(assetName))
        {
            if (AssetOwners.TryGetValue(candidate, out AssetOwner owner) &&
                !string.IsNullOrWhiteSpace(owner.Guid))
            {
                return owner.Name;
            }
        }

        return DataForgeReferenceSections.UnknownOwnerName;
    }

    private static void EnsureMappingsLoaded()
    {
        List<AssetOwner> plugins = GetPluginResources();
        AssetBundle[] bundles = AssetBundle.GetAllLoadedAssetBundles().ToArray();
        string signature = BuildSignature(bundles, plugins);
        if (_mappingsInitialized &&
            string.Equals(signature, _loadedSignature, StringComparison.Ordinal))
        {
            return;
        }

        lock (Sync)
        {
            if (_mappingsInitialized &&
                string.Equals(signature, _loadedSignature, StringComparison.Ordinal))
            {
                return;
            }

            AssetOwners.Clear();
            AmbiguousAssetNames.Clear();
            foreach (AssetBundle assetBundle in bundles
                         .OrderBy(bundle => bundle.name ?? "", StringComparer.OrdinalIgnoreCase)
                         .ThenBy(bundle => bundle.name ?? "", StringComparer.Ordinal))
            {
                string bundleName = assetBundle.name ?? "";
                if (bundleName.Length == 0)
                {
                    continue;
                }

                AssetOwner? owner = AssetOwnerMatching.Resolve(bundleName, plugins);
                if (owner == null)
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
                    if (string.IsNullOrWhiteSpace(assetName) || AmbiguousAssetNames.Contains(assetName))
                    {
                        continue;
                    }

                    AssetOwnerMatching.Add(AssetOwners, AmbiguousAssetNames, assetName, owner);
                }
            }

            _loadedSignature = signature;
            _mappingsInitialized = true;
            DataForgePlugin.Log.LogDebug(
                $"Tracked {AssetOwners.Count} mod asset owner mapping(s) for reference sections; " +
                $"{AmbiguousAssetNames.Count} ambiguous name(s) remain untracked.");
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

    private static List<AssetOwner> GetPluginResources()
    {
        List<AssetOwner> plugins = new();
        foreach (var info in Chainloader.PluginInfos.Values)
        {
            try
            {
                var assembly = info.Instance?.GetType().Assembly;
                if (assembly == null) continue;
                plugins.Add(new AssetOwner(info.Metadata.GUID, info.Metadata.Name,
                    assembly.GetName().Name ?? "", assembly.GetManifestResourceNames()));
            }
            catch { /* Retry at the next reference generation after plugin initialization. */ }
        }
        return plugins;
    }

    private static string BuildSignature(AssetBundle[] bundles, List<AssetOwner> plugins)
    {
        // Same-name reloads and late initialization must not reuse an incomplete catalog.
        return string.Join("|", bundles.Select(bundle => $"{bundle.GetInstanceID()}:{bundle.name}").OrderBy(x => x, StringComparer.Ordinal))
            + "||" + string.Join("|", plugins.Select(plugin => $"{plugin.Guid}:{plugin.Name}:{plugin.AssemblyName}:"
                + string.Join(",", plugin.Resources.OrderBy(x => x, StringComparer.Ordinal))).OrderBy(x => x, StringComparer.Ordinal));
    }
}
