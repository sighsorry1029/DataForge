using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DataForge;

internal static class Program
{
    private static int Passed;
    private static int ReflectionEvents;

    private static void Main(string[] args)
    {
        InitialAndFailureState();
        MetadataIsReadOnly();
        CoalescingAndLocalRevisions();
        SessionAndSubscriberSafety();
        if (args.Length > 0) CheckMergedAssembly(args[0]);
        Console.WriteLine($"DataForge public API checks passed: {Passed}");
    }

    private static void Assert(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
        Passed++;
    }

    private static List<DataForgeChange> Fresh(out Action<DataForgeChange> listener)
    {
        DataForgeApi.Shutdown();
        List<DataForgeChange> changes = new();
        listener = changes.Add;
        DataForgeApi.Changed += listener;
        return changes;
    }

    private static void InitialAndFailureState()
    {
        List<DataForgeChange> changes = Fresh(out _);
        foreach (DataForgeDomain domain in Enum.GetValues<DataForgeDomain>())
        {
            DataForgeDomainState state = DataForgeApi.GetState(domain);
            Assert(!state.IsReady && state.Revision == 0 && state.LastChangeKind == DataForgeChangeKind.Reset, "Initial state");
            Assert(DataForgeApi.GetConfiguredOverrideKeys(domain).Count == 0, "Empty initial overrides");
            Assert(!DataForgeApi.TryGetCloneSource(domain, "Missing", out _), "No initial clone");
        }
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, true)) scope.Complete();
        Assert(DataForgeApi.GetState(DataForgeDomain.Items).IsReady, "Empty/disabled baseline pass can be ready");
        Assert(changes.Count == 0, "No synchronous event from apply");
        DataForgeApi.DispatchPending();
        Assert(changes.Count == 1 && changes[0].FullRefresh && changes[0].Kind == DataForgeChangeKind.Applied, "Null keys means full refresh");
        using (DataForgeApi.BeginApply(DataForgeDomain.Items, true, new[] { "Amber" })) { }
        Assert(!DataForgeApi.GetState(DataForgeDomain.Items).IsReady, "Uncompleted pass is failed, not applied");
        DataForgeApi.DispatchPending();
        Assert(changes.Last().Kind == DataForgeChangeKind.Failed && changes.Last().FullRefresh, "Failed pass invalidates runtime caches");
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, false, Array.Empty<string>())) scope.Complete();
        DataForgeApi.DispatchPending();
        Assert(changes.Last().IsReady && !changes.Last().IsAuthority && !changes.Last().FullRefresh, "Recovery/client state");
        Assert(changes.Last().Revision == 3, "Revision describes local attempts, not peer version");
        Assert(!DataForgeApi.GetState(DataForgeDomain.Pieces).IsReady, "Other domain readiness independent");
        bool threw = false;
        try { DataForgeApi.GetState((DataForgeDomain)999); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert(threw, "Invalid domain rejected");
    }

    private static void MetadataIsReadOnly()
    {
        List<DataForgeChange> changes = Fresh(out _);
        string[] affected = { "Amber", "TrophyWraith" };
        string[] overrides = { "Amber", "amber", "TrophyWraith" };
        Dictionary<string, string> clones = new() { ["AmberClone"] = "Amber" };
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, true, affected))
        {
            affected[0] = "Corrupted";
            scope.Complete(overrides, clones);
            overrides[0] = "Corrupted";
            clones["AmberClone"] = "Corrupted";
        }
        DataForgeApi.DispatchPending();
        Assert(changes[0].AffectedKeys.Contains("Amber") && !changes[0].AffectedKeys.Contains("Corrupted"), "Affected keys copied at scope entry");
        Assert(DataForgeApi.HasConfiguredOverride(DataForgeDomain.Items, "AMBER"), "Configured names use domain comparer");
        Assert(DataForgeApi.GetConfiguredOverrideKeys(DataForgeDomain.Items).Count == 2, "Override metadata deduplicated");
        Assert(DataForgeApi.TryGetCloneSource(DataForgeDomain.Items, "AmberClone", out string source) && source == "Amber", "Clone source snapshot copied");
        Assert(!DataForgeApi.TryGetCloneSource(DataForgeDomain.Recipes, "AmberClone", out _), "Clone identity is domain scoped");
        bool threw = false;
        try { ((IList<string>)changes[0].AffectedKeys)[0] = "bad"; } catch (NotSupportedException) { threw = true; }
        Assert(threw, "Public affected keys immutable");
        threw = false;
        try { ((IDictionary<string, string>)DataForgeApi.GetCloneSources(DataForgeDomain.Items))["AmberClone"] = "bad"; }
        catch (NotSupportedException) { threw = true; }
        Assert(threw, "Public clone map immutable");
        IReadOnlyDictionary<string, string> retained = DataForgeApi.GetCloneSources(DataForgeDomain.Items);
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, true)) scope.Complete();
        Assert(!DataForgeApi.HasConfiguredOverride(DataForgeDomain.Items, "Amber"), "Removed/disabled config disappears");
        Assert(!DataForgeApi.TryGetCloneSource(DataForgeDomain.Items, "AmberClone", out _), "Removed clone disappears");
        Assert(retained["AmberClone"] == "Amber", "Old immutable snapshot remains stable");
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Localization, false, new[] { "$Key", "$key" }, language: "English"))
            scope.Complete(new[] { "$Key", "$key" });
        Assert(DataForgeApi.GetState(DataForgeDomain.Localization).Language == "English", "Current language captured");
        Assert(DataForgeApi.GetConfiguredOverrideKeys(DataForgeDomain.Localization).Count == 2, "Localization key case preserved");
        using (DataForgeApi.BeginApply(DataForgeDomain.Localization, false, language: "German")) { }
        Assert(DataForgeApi.GetConfiguredOverrideKeys(DataForgeDomain.Localization).Count == 0, "Failed pass must not expose stale ownership");
    }

    private static void CoalescingAndLocalRevisions()
    {
        List<DataForgeChange> changes = Fresh(out _);
        for (int i = 0; i < 100; i++)
        {
            using DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, true, new[] { "Item" + i });
            scope.Complete();
        }
        Assert(changes.Count == 0, "Repeated nested applies deferred");
        DataForgeApi.DispatchPending();
        Assert(changes.Count == 1 && changes[0].AffectedKeys.Count == 100 && changes[0].Revision == 100, "Repeated applies coalesced once/domain");
        DataForgeApi.DispatchPending();
        Assert(changes.Count == 1, "Idle update emits nothing");
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, true, new[] { "B" })) scope.Complete();
        using (DataForgeApi.BeginApply(DataForgeDomain.Items, true, new[] { "C" })) { }
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, true, new[] { "D" })) scope.Complete();
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Recipes, true)) scope.Complete(new[] { "SwordIron;1", "SwordIron;2" });
        DataForgeApi.DispatchPending();
        Assert(changes.Count == 3, "Separate domains emitted independently");
        DataForgeChange lastItems = changes.Last(change => change.Domain == DataForgeDomain.Items);
        Assert(lastItems.IsReady && lastItems.FullRefresh && lastItems.AffectedKeys.Count == 3, "Recovery preserves invalidation requirement");
        Assert(DataForgeApi.GetConfiguredOverrideKeys(DataForgeDomain.Recipes).Count == 2, "Recipe slots remain distinct");
    }

    private static void SessionAndSubscriberSafety()
    {
        List<DataForgeChange> changes = Fresh(out Action<DataForgeChange> listener);
        long oldSession = DataForgeApi.GetState(DataForgeDomain.Items).SessionId;
        using (DataForgeApplyScope stale = DataForgeApi.BeginApply(DataForgeDomain.Items, true))
        {
            DataForgeApi.ResetSession(false);
            stale.Complete(new[] { "Stale" });
        }
        Assert(!DataForgeApi.GetState(DataForgeDomain.Items).IsReady, "Stale scope cannot revive new session");
        Assert(DataForgeApi.GetState(DataForgeDomain.Items).SessionId != oldSession, "New world has new generation");
        DataForgeApi.DispatchPending();
        Assert(changes.Count == 5 && changes.All(change => change.Kind == DataForgeChangeKind.Reset && change.FullRefresh), "World reset invalidates all domains");
        DataForgeApi.Changed -= listener;
        Action<DataForgeChange> broken = _ => throw new InvalidOperationException("consumer");
        DataForgeApi.Changed += broken;
        int afterBroken = 0;
        DataForgeApi.Changed += _ => afterBroken++;
        DataForgeApi.SetWarningSink(_ => throw new Exception("broken logger"));
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, true)) scope.Complete();
        DataForgeApi.DispatchPending();
        Assert(afterBroken == 1, "One consumer/logger cannot block other subscribers");
        Assert(changes.Count == 5, "Unsubscribe stops delivery");

        Fresh(out _);
        int nestedCount = 0;
        DataForgeApi.Changed += change =>
        {
            nestedCount++;
            if (change.Domain != DataForgeDomain.Items) return;
            using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Pieces, true)) scope.Complete();
            DataForgeApi.DispatchPending();
        };
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, true)) scope.Complete();
        DataForgeApi.DispatchPending();
        Assert(nestedCount == 1, "Subscriber-triggered apply cannot recursively dispatch");
        DataForgeApi.DispatchPending();
        Assert(nestedCount == 2, "New callback work arrives at next dispatch");
        Fresh(out _);
        int staleDeliveries = 0;
        DataForgeApi.Changed += _ => DataForgeApi.ResetSession(false);
        DataForgeApi.Changed += _ => staleDeliveries++;
        using (DataForgeApplyScope scope = DataForgeApi.BeginApply(DataForgeDomain.Items, true)) scope.Complete();
        DataForgeApi.DispatchPending();
        Assert(staleDeliveries == 0, "Reset during callback blocks stale session deliveries");
        DataForgeApi.Shutdown();
    }

    private static void CheckMergedAssembly(string path)
    {
        Assembly assembly = Assembly.LoadFrom(System.IO.Path.GetFullPath(path));
        HashSet<string> assemblyReferences = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(!assemblyReferences.Contains("ServerSync"), "ServerSync is merged into the final DLL");
        Assert(!assemblyReferences.Contains("YamlDotNet"), "YamlDotNet is merged into the final DLL");
        Assert(!assembly.GetType("ServerSync.ConfigSync", true)!.IsVisible, "Merged ServerSync types are internalized");
        Assert(!assembly.GetType("YamlDotNet.Serialization.DeserializerBuilder", true)!.IsVisible, "Merged YamlDotNet types are internalized");
        Type api = assembly.GetType("DataForge.DataForgeApi", true)!;
        Type domain = assembly.GetType("DataForge.DataForgeDomain", true)!;
        Type change = assembly.GetType("DataForge.DataForgeChange", true)!;
        Assert(api.IsPublic && change.IsPublic && domain.IsPublic, "Merged public API types remain exported");
        Assert((int)api.GetProperty("ApiVersion")!.GetValue(null)! == 1, "Merged contract version");
        object items = Enum.Parse(domain, "Items");
        object state = api.GetMethod("GetState")!.Invoke(null, new[] { items })!;
        Assert((bool)state.GetType().GetProperty("IsReady")!.GetValue(state)! == false, "Merged API can be queried without game initialization");
        EventInfo changed = api.GetEvent("Changed")!;
        Delegate handler = Delegate.CreateDelegate(changed.EventHandlerType!, typeof(Program).GetMethod(nameof(OnReflectedChange), BindingFlags.Static | BindingFlags.NonPublic)!);
        changed.AddEventHandler(null, handler);
        object scope = api.GetMethod("BeginApply", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object?[] { items, true, null, true, null })!;
        scope.GetType().GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(scope, new object?[] { null, null });
        ((IDisposable)scope).Dispose();
        api.GetMethod("DispatchPending", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        Assert(ReflectionEvents == 1, "Optional reflection subscriber signature works against merged DLL");
        changed.RemoveEventHandler(null, handler);
        foreach (PropertyInfo property in change.GetProperties()) Assert(property.SetMethod == null, "Read-only merged payload: " + property.Name);
        Assert(api.GetMethod("BeginApply") == null && api.GetMethod("ResetSession") == null, "Mutation helpers are not public");
    }

    private static void OnReflectedChange(object change)
    {
        Assert(change.GetType().GetProperty("Domain")!.GetValue(change)!.ToString() == "Items", "Reflected event domain");
        ReflectionEvents++;
    }
}
