using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DataForge;

internal static class DataForgeOverrideFiles
{
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

    internal static List<TEntry> LoadEntries<TEntry>(
        IEnumerable<string> files,
        Func<string, string, IEnumerable<TEntry>> deserializeEntries)
    {
        List<TEntry> entries = new();
        foreach (string file in files)
        {
            string yaml = File.ReadAllText(file);
            entries.AddRange(deserializeEntries(yaml, file));
        }

        return entries;
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
