using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DataForge;

internal static class DataForgeOverrideFiles
{
    internal static bool IsDomainOverrideFile(
        string path,
        string baseFileName,
        string fileNamePrefix)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = Path.GetFileName(path);
        if (fileName.Equals(baseFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string prefixedStem = fileNamePrefix + "_";
        return stem.Length > prefixedStem.Length &&
               stem.StartsWith(prefixedStem, StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<string> GetOverrideFiles(string directory, Func<string, bool> isOverrideFile)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(directory, "*.yml")
            .Concat(Directory.GetFiles(directory, "*.yaml"))
            .Where(isOverrideFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool TryLoadEntries<TEntry>(
        IEnumerable<string> files,
        Func<string, string, IEnumerable<TEntry>> deserializeEntries,
        out List<TEntry> entries)
    {
        List<TEntry> loadedEntries = new();
        try
        {
            foreach (string file in files)
            {
                string yaml = File.ReadAllText(file);
                loadedEntries.AddRange(deserializeEntries(yaml, file));
            }

            entries = loadedEntries;
            return true;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Override reload failed; keeping the last-known-good configuration. {ex.Message}");
            entries = new List<TEntry>();
            return false;
        }
    }

    internal static bool TryDeserializeEntries<TEntry>(
        string yaml,
        string source,
        Func<string, string, IEnumerable<TEntry>> deserializeEntries,
        out List<TEntry> entries)
    {
        try
        {
            entries = deserializeEntries(yaml, source).ToList();
            return true;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogError($"Synced override payload was rejected; keeping the last-known-good configuration. {ex.Message}");
            entries = new List<TEntry>();
            return false;
        }
    }

    internal static void EnsureDefaultOverride(
        string directory,
        string overrideFileName,
        Func<IEnumerable<string>> getOverrideFiles,
        Func<string> buildDefaultTemplate)
    {
        Directory.CreateDirectory(directory);
        if (getOverrideFiles().Any())
        {
            return;
        }

        File.WriteAllText(Path.Combine(directory, overrideFileName), buildDefaultTemplate());
    }
}
