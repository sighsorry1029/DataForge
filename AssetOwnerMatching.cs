using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ModAssetOwnership;

// Identical source is vendored in DataForge. Keep the policy and its checks in sync;
// neither independently installed mod requires the other assembly at runtime.
internal sealed class AssetOwner
{
    internal readonly string Guid;
    internal readonly string Name;
    internal readonly string AssemblyName;
    internal readonly string[] Resources;

    internal AssetOwner(string guid, string name, string assemblyName, string[] resources)
    { Guid = guid; Name = string.IsNullOrWhiteSpace(name) ? guid : name; AssemblyName = assemblyName; Resources = resources; }
}

internal static class AssetOwnerMatching
{
    internal static AssetOwner? Resolve(string bundleName, IReadOnlyList<AssetOwner> plugins)
    {
        if (string.IsNullOrWhiteSpace(bundleName)) return null;
        AssetOwner? owner = null;
        bool matchedResource = false;
        foreach (AssetOwner plugin in plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Guid)) continue;
            bool matches = false;
            foreach (string resource in plugin.Resources)
                if (resource.Equals(bundleName, StringComparison.OrdinalIgnoreCase) ||
                    resource.EndsWith("." + bundleName, StringComparison.OrdinalIgnoreCase)) { matches = true; break; }
            if (!matches) continue;
            matchedResource = true;
            if (owner != null && !owner.Guid.Equals(plugin.Guid, StringComparison.OrdinalIgnoreCase)) return null;
            owner = plugin;
        }
        if (matchedResource) return owner;

        // A substring is not provenance. Accept only a unique complete token match.
        string token = Normalize(Path.GetFileNameWithoutExtension(bundleName));
        if (token.Length == 0) return null;
        foreach (AssetOwner plugin in plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Guid)) continue;
            if (token != Normalize(plugin.Name) && token != Normalize(plugin.Guid) && token != Normalize(plugin.AssemblyName)) continue;
            if (owner != null && !owner.Guid.Equals(plugin.Guid, StringComparison.OrdinalIgnoreCase)) return null;
            owner = plugin;
        }
        return owner;
    }

    internal static void Add(Dictionary<string, AssetOwner> owners, HashSet<string> ambiguous, string name, AssetOwner owner)
    {
        if (ambiguous.Contains(name)) return;
        if (owners.TryGetValue(name, out AssetOwner? previous) && !previous.Guid.Equals(owner.Guid, StringComparison.OrdinalIgnoreCase))
        { owners.Remove(name); ambiguous.Add(name); }
        else owners[name] = owner;
    }

    private static string Normalize(string value)
    {
        StringBuilder text = new();
        foreach (char c in value ?? "") if (char.IsLetterOrDigit(c)) text.Append(char.ToLowerInvariant(c));
        return text.ToString();
    }
}
