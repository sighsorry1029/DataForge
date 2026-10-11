using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;

namespace DataForge;

internal static class VneiRefreshManager
{
    private const long RefreshDelayTicks = TimeSpan.TicksPerSecond;
    private static DataForgeFileWatcher.DebouncedAction? RefreshDebouncer;
    private static bool Initialized;
    private static string PendingReason = "";
    private static DataForgeFileWatcher.DebouncedAction? UiRetryDebouncer;
    private static List<VneiUiCompatibility.Snapshot> PendingUi = new();
    private static ConversionIndex? ConversionReader;
    private static MethodInfo? PendingKnownUpdate;
    private static long WorldGeneration;
    private static long RequestGeneration;

    internal static void Initialize()
    {
        if (Initialized)
        {
            return;
        }

        Initialized = true;
        RefreshDebouncer = DataForgeFileWatcher.CreateDebouncedAction(RefreshDelayTicks, RefreshNow);
        UiRetryDebouncer = DataForgeFileWatcher.CreateDebouncedAction(RefreshDelayTicks, RetryUi);
    }

    internal static void Dispose()
    {
        WorldGeneration++;
        RequestGeneration++;
        RefreshDebouncer?.Dispose();
        RefreshDebouncer = null;
        Initialized = false;
        PendingReason = "";
        PendingUi.Clear();
        PendingKnownUpdate = null;
        UiRetryDebouncer?.Dispose();
        UiRetryDebouncer = null;
    }

    internal static void OnWorldShutdown()
    {
        WorldGeneration++;
        RequestGeneration++;
        PendingReason = "";
        PendingUi.Clear();
        PendingKnownUpdate = null;
        UiRetryDebouncer?.Dispose();
        UiRetryDebouncer = null;
        if (!Initialized)
        {
            return;
        }

        RefreshDebouncer?.Dispose();
        RefreshDebouncer = DataForgeFileWatcher.CreateDebouncedAction(RefreshDelayTicks, RefreshNow);
        UiRetryDebouncer = DataForgeFileWatcher.CreateDebouncedAction(RefreshDelayTicks, RetryUi);
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
        RequestGeneration++;
        RefreshDebouncer?.Schedule();
    }

    private static void RefreshNow()
    {
        if (DataForgeWorldLifecycle.IsShuttingDown || IsDedicatedServer())
        {
            return;
        }

        IndexBackup? backup = null;
        long world = WorldGeneration;
        long request = RequestGeneration;
        List<VneiUiCompatibility.Snapshot>? ui = null;
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
            if (hasIndexedMethod == null || indexAllMethod == null || updateKnownMethod == null)
            {
                throw new MissingMethodException("VNEI index/known-state refresh contract is unavailable.");
            }

            bool hasIndexed = (bool)(hasIndexedMethod.Invoke(null, Array.Empty<object>()) ?? false);
            if (!hasIndexed)
            {
                return;
            }

            // Resolve optional private contracts and capture views before mutating the index.
            ui = VneiUiCompatibility.Capture();
            ConversionReader ??= new ConversionIndex(indexingType.Assembly);
            List<ConversionExpectation> expected = PieceOverrideManager.GetVneiConversionExpectations();
            HashSet<string> buildablePieces = VneiPrefabCleanupGuard.GetBuildablePieceNames();
            EventInfo finishedEvent = indexingType.GetEvent("IndexFinished") ??
                throw new MissingMemberException(indexingType.FullName, "IndexFinished");
            if (finishedEvent.EventHandlerType != typeof(Action))
                throw new InvalidOperationException("VNEI IndexFinished signature changed.");
            bool finished = false;
            Action onFinished = () => finished = true;
            PendingUi.Clear();
            PendingKnownUpdate = null;
            backup = new IndexBackup();
            ClearIndex(indexingType, recipeInfoType, backup);
            VneiPrefabCleanupGuard.RemoveInvalidEntriesBeforeRefresh(indexAllMethod);
            finishedEvent.AddEventHandler(null, onFinished);
            try { indexAllMethod.Invoke(null, Array.Empty<object>()); }
            finally { finishedEvent.RemoveEventHandler(null, onFinished); }
            if (world != WorldGeneration || DataForgeWorldLifecycle.IsShuttingDown) return;
            bool indexedAfterRefresh = (bool)(hasIndexedMethod.Invoke(null, Array.Empty<object>()) ?? false);
            if (!finished || !indexedAfterRefresh)
            {
                throw new InvalidOperationException("VNEI IndexAll completed without producing an indexed state.");
            }

            int verifiedConversions = ConversionReader.Validate(expected, buildablePieces);
            // IndexFinished has already called external subscribers. This backup is
            // a logical index rollback, not a transaction over arbitrary mod events.
            backup = null;
            try { updateKnownMethod.Invoke(null, Array.Empty<object>()); }
            catch (Exception ex)
            {
                if (world != WorldGeneration || DataForgeWorldLifecycle.IsShuttingDown) return;
                AccessTools.Field(indexingType, "currentKnownCount")?.SetValue(null, -1);
                PendingKnownUpdate = updateKnownMethod;
                DataForgePlugin.Log.LogWarning($"VNEI index validated, but known-state update failed: {ex.GetBaseException().Message}");
            }
            if (world != WorldGeneration || DataForgeWorldLifecycle.IsShuttingDown) return;
            RebindUi(ui);
            if (world != WorldGeneration || DataForgeWorldLifecycle.IsShuttingDown) return;
            ui = null;
            DataForgePlugin.Log.LogInfo($"Refreshed VNEI index after DataForge {PendingReason} changes; verified {verifiedConversions}/{expected.Count} conversions (unindexed facilities excluded); UI failures: {PendingUi.Count}; known-state retry pending: {PendingKnownUpdate != null}.");
        }
        catch (Exception ex)
        {
            Exception refreshError = ex.GetBaseException();
            string rollbackError = "";
            if (backup != null && world == WorldGeneration && !DataForgeWorldLifecycle.IsShuttingDown)
            {
                try
                {
                    backup.Restore();
                    if (ui != null) RebindUi(ui);
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
            if (request == RequestGeneration) PendingReason = "";
        }
    }

    private static void RebindUi(List<VneiUiCompatibility.Snapshot> snapshots)
    {
        long world = WorldGeneration;
        List<VneiUiCompatibility.Snapshot> failed = VneiUiCompatibility.Restore(snapshots);
        if (world != WorldGeneration || DataForgeWorldLifecycle.IsShuttingDown) return;
        PendingUi = failed;
        if (PendingUi.Count > 0 || PendingKnownUpdate != null) UiRetryDebouncer?.Schedule();
    }

    private static void RetryUi()
    {
        long world = WorldGeneration;
        List<VneiUiCompatibility.Snapshot> snapshots = PendingUi;
        PendingUi = new();
        MethodInfo? known = PendingKnownUpdate;
        PendingKnownUpdate = null;
        if (DataForgeWorldLifecycle.IsShuttingDown || IsDedicatedServer()) return;
        if (known != null)
        {
            // UpdateKnown itself can switch an open detail view back to search.
            // Capture current successful views; failed views retain their earlier snapshot.
            try
            {
                foreach (var current in VneiUiCompatibility.Capture())
                    if (!snapshots.Any(previous => ReferenceEquals(previous.Target, current.Target))) snapshots.Add(current);
            }
            catch (Exception ex)
            {
                DataForgePlugin.Log.LogWarning($"Could not capture VNEI views for known-state retry: {ex.GetBaseException().Message}");
                return;
            }
            try { known.Invoke(null, Array.Empty<object>()); }
            catch (Exception ex)
            {
                if (world != WorldGeneration || DataForgeWorldLifecycle.IsShuttingDown) return;
                AccessTools.Field(known.DeclaringType, "currentKnownCount")?.SetValue(null, -1);
                DataForgePlugin.Log.LogWarning($"VNEI known-state retry failed; the validated index was retained: {ex.GetBaseException().Message}");
            }
        }
        if (world != WorldGeneration || DataForgeWorldLifecycle.IsShuttingDown) return;
        int failures = VneiUiCompatibility.Restore(snapshots).Count;
        if (failures > 0) DataForgePlugin.Log.LogWarning($"VNEI UI retry failed for {failures} views; the validated index was retained.");
    }

    internal readonly struct ConversionExpectation
    {
        internal readonly string Station, Input, Output;
        internal readonly int Amount;
        internal ConversionExpectation(string station, string input, string output, int amount)
        { Station = station; Input = input; Output = output; Amount = amount; }
    }

    internal sealed class ConversionIndex
    {
        private readonly PropertyInfo recipes, ingredients, results, stations;
        private readonly MethodInfo getItem;
        private readonly FieldInfo partItem, partAmount, min, max, chance, name, itemResults, itemIngredients;

        internal ConversionIndex(Assembly assembly)
        {
            Type recipe = assembly.GetType("VNEI.Logic.RecipeInfo", true)!;
            Type part = assembly.GetType("VNEI.Logic.Part", true)!;
            Type amount = assembly.GetType("VNEI.Logic.Amount", true)!;
            Type item = assembly.GetType("VNEI.Logic.Item", true)!;
            PropertyInfo Property(Type type, string key) => type.GetProperty(key,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? throw new MissingMemberException(type.FullName, key);
            recipes = Property(recipe, "Recipes");
            ingredients = Property(recipe, "Ingredients");
            results = Property(recipe, "Results");
            stations = Property(recipe, "Stations");
            getItem = VneiUiCompatibility.Binding.Method(assembly.GetType("VNEI.Logic.Indexing", true)!, "GetItem", item, typeof(string));
            partItem = VneiUiCompatibility.Binding.Field(part, "item", item);
            partAmount = VneiUiCompatibility.Binding.Field(part, "amount", amount);
            min = VneiUiCompatibility.Binding.Field(amount, "min", typeof(int));
            max = VneiUiCompatibility.Binding.Field(amount, "max", typeof(int));
            chance = VneiUiCompatibility.Binding.Field(amount, "chance", typeof(float));
            name = VneiUiCompatibility.Binding.Field(item, "internalName", typeof(string));
            itemResults = VneiUiCompatibility.Binding.Field(item, "result", typeof(HashSet<>).MakeGenericType(recipe));
            itemIngredients = VneiUiCompatibility.Binding.Field(item, "ingredient", typeof(HashSet<>).MakeGenericType(recipe));
        }

        internal int Validate(List<ConversionExpectation> expected, ISet<string> buildablePieces)
        {
            if (expected.Count == 0) return 0;
            var wantedStations = new HashSet<string>(expected.Select(e => e.Station), StringComparer.Ordinal);
            var found = new HashSet<(string, string, string, int)>();
            foreach (object recipe in (IEnumerable)recipes.GetValue(null, null))
            {
                foreach (object station in (IEnumerable)stations.GetValue(recipe, null))
                {
                    string? stationName = Name(station);
                    if (stationName == null || !wantedStations.Contains(stationName)) continue;
                    foreach (object input in Parts(ingredients.GetValue(recipe, null)))
                    foreach (object output in Parts(results.GetValue(recipe, null)))
                    {
                        string? inputName = Name(input), outputName = Name(output);
                        if (inputName == null || outputName == null) continue;
                        object outputAmount = partAmount.GetValue(output);
                        int count = (int)min.GetValue(outputAmount);
                        if (count != (int)max.GetValue(outputAmount) || (float)chance.GetValue(outputAmount) != 1f) continue;
                        // A RecipeInfo constructor adds to Recipes before indexing can fail.
                        // Require the input/output backlinks too, not just a partial recipe.
                        if (!ContainsRecipe(itemIngredients, input, recipe) || !ContainsRecipe(itemResults, output, recipe)) continue;
                        found.Add((stationName, inputName, outputName, count));
                    }
                }
            }
            int verified = 0;
            foreach (var e in expected)
            {
                // VNEI omits unbuildable facilities from its Item index. Preserve that
                // policy; visible facilities must have complete conversion recipes.
                if (getItem.Invoke(null, new object[] { e.Station }) == null)
                {
                    if (buildablePieces.Contains(e.Station))
                        throw new InvalidOperationException($"VNEI omitted buildable conversion facility {e.Station}.");
                    continue;
                }
                if (!found.Contains((e.Station, e.Input, e.Output, e.Amount)))
                    throw new InvalidOperationException($"VNEI omitted conversion {e.Station}: {e.Input} -> {e.Output} x{e.Amount}.");
                verified++;
            }
            return verified;
        }

        private string? Name(object part)
        {
            object? item = partItem.GetValue(part);
            return item == null ? null : (string?)name.GetValue(item);
        }
        private bool ContainsRecipe(FieldInfo field, object part, object recipe) =>
            ((IEnumerable)field.GetValue(partItem.GetValue(part))).Cast<object>().Any(value => ReferenceEquals(value, recipe));
        private static IEnumerable<object> Parts(object value) =>
            ((IDictionary)value).Values.Cast<IEnumerable>().SelectMany(parts => parts.Cast<object>());
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
