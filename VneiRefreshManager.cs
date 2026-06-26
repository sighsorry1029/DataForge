using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DataForge;

internal static class VneiRefreshManager
{
    private const long RefreshDelayTicks = TimeSpan.TicksPerSecond;
    private static DataForgeFileWatcher.DebouncedAction? RefreshDebouncer;
    private static bool Initialized;
    private static string PendingReason = "";

    internal static void Initialize()
    {
        if (Initialized)
        {
            return;
        }

        Initialized = true;
        RefreshDebouncer = DataForgeFileWatcher.CreateDebouncedAction(RefreshDelayTicks, RefreshNow);
    }

    internal static void Dispose()
    {
        RefreshDebouncer?.Dispose();
        RefreshDebouncer = null;
        Initialized = false;
        PendingReason = "";
    }

    internal static void RequestRefresh(string reason)
    {
        if (IsDedicatedServer())
        {
            return;
        }

        PendingReason = string.IsNullOrWhiteSpace(reason) ? "DataForge" : reason;
        RefreshDebouncer?.Schedule();
    }

    private static void RefreshNow()
    {
        if (IsDedicatedServer())
        {
            return;
        }

        IndexBackup? backup = null;
        try
        {
            Type? indexingType = AccessTools.TypeByName("VNEI.Logic.Indexing");
            Type? recipeInfoType = AccessTools.TypeByName("VNEI.Logic.RecipeInfo");
            if (indexingType == null || recipeInfoType == null)
            {
                return;
            }

            MethodInfo? hasIndexedMethod = AccessTools.Method(indexingType, "HasIndexed", Type.EmptyTypes);
            MethodInfo? indexAllMethod = AccessTools.Method(indexingType, "IndexAll", Type.EmptyTypes);
            MethodInfo? updateKnownMethod = AccessTools.Method(indexingType, "UpdateKnown", Type.EmptyTypes);
            if (hasIndexedMethod == null || indexAllMethod == null)
            {
                return;
            }

            bool hasIndexed = (bool)(hasIndexedMethod.Invoke(null, Array.Empty<object>()) ?? false);
            if (!hasIndexed)
            {
                return;
            }

            backup = ClearIndex(indexingType, recipeInfoType);
            VneiPrefabCleanupGuard.RemoveInvalidEntriesBeforeVnei();
            indexAllMethod.Invoke(null, Array.Empty<object>());
            updateKnownMethod?.Invoke(null, Array.Empty<object>());
            DataForgePlugin.Log.LogInfo($"Refreshed VNEI index after DataForge {PendingReason} changes.");
        }
        catch (Exception ex)
        {
            backup?.Restore();
            DataForgePlugin.Log.LogWarning($"Could not refresh VNEI index after DataForge changes: {ex.Message}");
        }
        finally
        {
            PendingReason = "";
        }
    }

    private static IndexBackup ClearIndex(Type indexingType, Type recipeInfoType)
    {
        IndexBackup backup = new();
        ClearDictionaryProperty(indexingType, "Items", backup);
        ClearDictionaryProperty(indexingType, "ItemsByPreLocalizedName", backup);
        ClearDictionaryProperty(indexingType, "ItemsByLocalizedName", backup);
        ClearListProperty(recipeInfoType, "Recipes", backup);
        AccessTools.Field(indexingType, "currentKnownCount")?.SetValue(null, -1);
        AccessTools.Field(indexingType, "currentShowOnlyKnown")?.SetValue(null, false);
        return backup;
    }

    private static void ClearDictionaryProperty(Type type, string name, IndexBackup backup)
    {
        if (AccessTools.Property(type, name)?.GetValue(null, null) is IDictionary dictionary)
        {
            backup.Track(dictionary);
            dictionary.Clear();
        }
    }

    private static void ClearListProperty(Type type, string name, IndexBackup backup)
    {
        if (AccessTools.Property(type, name)?.GetValue(null, null) is IList list)
        {
            backup.Track(list);
            list.Clear();
        }
    }

    private static bool IsDedicatedServer()
    {
        try
        {
            return ZNet.instance != null && ZNet.instance.IsDedicated();
        }
        catch
        {
            return false;
        }
    }

    private sealed class IndexBackup
    {
        private readonly List<DictionaryBackup> Dictionaries = new();
        private readonly List<ListBackup> Lists = new();

        internal void Track(IDictionary dictionary)
        {
            List<DictionaryEntry> entries = new();
            foreach (DictionaryEntry entry in dictionary)
            {
                entries.Add(entry);
            }

            Dictionaries.Add(new DictionaryBackup(dictionary, entries));
        }

        internal void Track(IList list)
        {
            List<object?> entries = new();
            foreach (object? entry in list)
            {
                entries.Add(entry);
            }

            Lists.Add(new ListBackup(list, entries));
        }

        internal void Restore()
        {
            foreach (DictionaryBackup backup in Dictionaries)
            {
                backup.Dictionary.Clear();
                foreach (DictionaryEntry entry in backup.Entries)
                {
                    backup.Dictionary[entry.Key] = entry.Value;
                }
            }

            foreach (ListBackup backup in Lists)
            {
                backup.List.Clear();
                foreach (object? entry in backup.Entries)
                {
                    backup.List.Add(entry);
                }
            }
        }
    }

    private sealed class DictionaryBackup
    {
        internal DictionaryBackup(IDictionary dictionary, List<DictionaryEntry> entries)
        {
            Dictionary = dictionary;
            Entries = entries;
        }

        internal IDictionary Dictionary { get; }
        internal List<DictionaryEntry> Entries { get; }
    }

    private sealed class ListBackup
    {
        internal ListBackup(IList list, List<object?> entries)
        {
            List = list;
            Entries = entries;
        }

        internal IList List { get; }
        internal List<object?> Entries { get; }
    }
}
