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

    internal static void OnWorldShutdown()
    {
        PendingReason = "";
        if (!Initialized)
        {
            return;
        }

        RefreshDebouncer?.Dispose();
        RefreshDebouncer = DataForgeFileWatcher.CreateDebouncedAction(RefreshDelayTicks, RefreshNow);
    }

    internal static void InvalidateForNewWorld()
    {
        if (IsDedicatedServer())
        {
            return;
        }

        try
        {
            Type? indexingType = DataForgeVneiTypes.Get("VNEI.Logic.Indexing");
            Type? recipeInfoType = DataForgeVneiTypes.Get("VNEI.Logic.RecipeInfo");
            if (indexingType == null || recipeInfoType == null)
            {
                return;
            }

            ClearDictionaryProperty(indexingType, "Items");
            ClearDictionaryProperty(indexingType, "ItemsByPreLocalizedName");
            ClearDictionaryProperty(indexingType, "ItemsByLocalizedName");
            ClearListProperty(recipeInfoType, "Recipes");
            AccessTools.Field(indexingType, "currentKnownCount")?.SetValue(null, -1);
            AccessTools.Field(indexingType, "currentShowOnlyKnown")?.SetValue(null, false);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"Could not invalidate the VNEI index for the new world: {ex.Message}");
        }
    }

    internal static void RequestRefresh(string reason)
    {
        if (DataForgeWorldLifecycle.IsShuttingDown || IsDedicatedServer())
        {
            return;
        }

        PendingReason = string.IsNullOrWhiteSpace(reason) ? "DataForge" : reason;
        RefreshDebouncer?.Schedule();
    }

    private static void RefreshNow()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown || IsDedicatedServer())
        {
            return;
        }

        IndexBackup? backup = null;
        try
        {
            Type? indexingType = DataForgeVneiTypes.Get("VNEI.Logic.Indexing");
            Type? recipeInfoType = DataForgeVneiTypes.Get("VNEI.Logic.RecipeInfo");
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

            backup = new IndexBackup();
            ClearIndex(indexingType, recipeInfoType, backup);
            VneiPrefabCleanupGuard.RemoveInvalidEntriesBeforeVnei();
            indexAllMethod.Invoke(null, Array.Empty<object>());
            bool indexedAfterRefresh = (bool)(hasIndexedMethod.Invoke(null, Array.Empty<object>()) ?? false);
            if (!indexedAfterRefresh)
            {
                throw new InvalidOperationException("VNEI IndexAll completed without producing an indexed state.");
            }

            updateKnownMethod?.Invoke(null, Array.Empty<object>());
            backup = null;
            DataForgePlugin.Log.LogInfo($"Refreshed VNEI index after DataForge {PendingReason} changes.");
        }
        catch (Exception ex)
        {
            Exception refreshError = ex.GetBaseException();
            string rollbackError = "";
            if (backup != null)
            {
                try
                {
                    backup.Restore();
                }
                catch (Exception rollbackException)
                {
                    Exception rollbackRoot = rollbackException.GetBaseException();
                    rollbackError = $" Rollback also failed: {rollbackRoot.GetType().Name}: {rollbackRoot.Message}";
                }
            }

            DataForgePlugin.Log.LogWarning(
                $"Could not refresh VNEI index after DataForge changes: " +
                $"{refreshError.GetType().Name}: {refreshError.Message}{rollbackError}");
        }
        finally
        {
            PendingReason = "";
        }
    }

    private static void ClearIndex(Type indexingType, Type recipeInfoType, IndexBackup backup)
    {
        ClearDictionaryProperty(indexingType, "Items", backup);
        ClearDictionaryProperty(indexingType, "ItemsByPreLocalizedName", backup);
        ClearDictionaryProperty(indexingType, "ItemsByLocalizedName", backup);
        ClearListProperty(recipeInfoType, "Recipes", backup);
        SetStaticField(indexingType, "currentKnownCount", -1, backup);
        SetStaticField(indexingType, "currentShowOnlyKnown", false, backup);
        TrackPluginStationFields(backup);
    }

    private static void ClearDictionaryProperty(Type type, string name, IndexBackup backup)
    {
        if (AccessTools.Property(type, name)?.GetValue(null, null) is IDictionary dictionary)
        {
            backup.Track(dictionary);
            dictionary.Clear();
        }
    }

    private static void ClearDictionaryProperty(Type type, string name)
    {
        if (AccessTools.Property(type, name)?.GetValue(null, null) is IDictionary dictionary)
        {
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

    private static void ClearListProperty(Type type, string name)
    {
        if (AccessTools.Property(type, name)?.GetValue(null, null) is IList list)
        {
            list.Clear();
        }
    }

    private static void SetStaticField(Type type, string name, object value, IndexBackup backup)
    {
        FieldInfo? field = AccessTools.Field(type, name);
        if (field == null)
        {
            return;
        }

        backup.Track(field, null);
        field.SetValue(null, value);
    }

    private static void TrackPluginStationFields(IndexBackup backup)
    {
        Type? pluginType = DataForgeVneiTypes.Get("VNEI.Plugin");
        object? plugin = pluginType == null
            ? null
            : AccessTools.Property(pluginType, "Instance")?.GetValue(null, null);
        if (pluginType == null || plugin == null)
        {
            return;
        }

        foreach (string fieldName in new[] { "allStations", "handStation", "noStation" })
        {
            FieldInfo? field = AccessTools.Field(pluginType, fieldName);
            if (field != null)
            {
                backup.Track(field, plugin);
            }
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
        private readonly List<FieldBackup> Fields = new();

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

        internal void Track(FieldInfo field, object? target)
        {
            Fields.Add(new FieldBackup(field, target, field.GetValue(target)));
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

            foreach (FieldBackup backup in Fields)
            {
                backup.Field.SetValue(backup.Target, backup.Value);
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

    private sealed class FieldBackup
    {
        internal FieldBackup(FieldInfo field, object? target, object? value)
        {
            Field = field;
            Target = target;
            Value = value;
        }

        internal FieldInfo Field { get; }
        internal object? Target { get; }
        internal object? Value { get; }
    }
}
