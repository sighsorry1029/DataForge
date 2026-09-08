using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DataForge;

public enum DataForgeDomain { Items, Recipes, Pieces, StatusEffects, Localization }
public enum DataForgeChangeKind { Reset, Applied, Failed }

/// <summary>Immutable local application state, not a server/client synchronization acknowledgement.</summary>
public sealed class DataForgeDomainState
{
    public DataForgeDomain Domain { get; }
    public long SessionId { get; }
    public long Revision { get; }
    public bool IsReady { get; }
    public bool IsAuthority { get; }
    public string Language { get; }
    public DataForgeChangeKind LastChangeKind { get; }

    internal DataForgeDomainState(DataForgeDomain domain, long sessionId, long revision,
        DataForgeChangeKind kind, bool isAuthority, string language)
    {
        Domain = domain; SessionId = sessionId; Revision = revision;
        LastChangeKind = kind; IsReady = kind == DataForgeChangeKind.Applied;
        IsAuthority = isAuthority; Language = language;
    }
}

/// <summary>Re-read these runtime keys. This is not an exact field/value diff.</summary>
public sealed class DataForgeChange
{
    public DataForgeDomainState State { get; }
    public DataForgeDomain Domain => State.Domain;
    public long SessionId => State.SessionId;
    public long Revision => State.Revision;
    public bool IsReady => State.IsReady;
    public bool IsAuthority => State.IsAuthority;
    public string Language => State.Language;
    public DataForgeChangeKind Kind => State.LastChangeKind;
    public IReadOnlyList<string> AffectedKeys { get; }
    public bool FullRefresh { get; }

    internal DataForgeChange(DataForgeDomainState state, IEnumerable<string> keys, bool fullRefresh)
    {
        State = state;
        AffectedKeys = Array.AsReadOnly(keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        FullRefresh = fullRefresh;
    }
}

/// <summary>
/// Read-only integration API. Call on the Unity thread. Changed is dispatched on
/// that thread after application, coalesced per domain until the next API update.
/// Versions are local to this process/session, not comparable between peers.
/// </summary>
public static class DataForgeApi
{
    public static int ApiVersion => 1;
    public static event Action<DataForgeChange>? Changed;

    private sealed class DomainData
    {
        internal DataForgeDomainState State;
        internal IReadOnlyList<string> Keys = Array.AsReadOnly(Array.Empty<string>());
        internal HashSet<string> Overrides;
        internal IReadOnlyDictionary<string, string> Clones;
        internal DomainData(DataForgeDomain domain, long sessionId, bool authority)
        {
            State = new DataForgeDomainState(domain, sessionId, 0, DataForgeChangeKind.Reset, authority, "");
            Overrides = new HashSet<string>(Comparer(domain));
            Clones = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Comparer(domain)));
        }
    }

    private sealed class PendingChange
    {
        internal readonly HashSet<string> Keys;
        internal bool FullRefresh;
        internal PendingChange(DataForgeDomain domain) { Keys = new HashSet<string>(Comparer(domain)); }
    }

    private static readonly DataForgeDomain[] Domains = (DataForgeDomain[])Enum.GetValues(typeof(DataForgeDomain));
    private static readonly Dictionary<DataForgeDomain, DomainData> Data = new();
    private static readonly Dictionary<DataForgeDomain, PendingChange> Pending = new();
    private static long SessionId = 1;
    private static bool Dispatching;
    private static Action<string>? Warning;

    static DataForgeApi()
    {
        foreach (DataForgeDomain domain in Domains) Data.Add(domain, new DomainData(domain, SessionId, false));
    }

    public static DataForgeDomainState GetState(DataForgeDomain domain) => RequireDomain(domain).State;

    /// <summary>Enabled configured targets from the last completed pass; not proof that each target resolved.</summary>
    public static bool HasConfiguredOverride(DataForgeDomain domain, string key)
    {
        DomainData data = RequireDomain(domain);
        return data.State.IsReady && key != null && data.Overrides.Contains(key);
    }

    public static IReadOnlyList<string> GetConfiguredOverrideKeys(DataForgeDomain domain) => RequireDomain(domain).Keys;

    /// <summary>Immediate source of a clone currently managed by DataForge, not its transitive root.</summary>
    public static bool TryGetCloneSource(DataForgeDomain domain, string key, out string source)
    {
        DomainData data = RequireDomain(domain);
        source = "";
        return data.State.IsReady && key != null && data.Clones.TryGetValue(key, out source!);
    }

    public static IReadOnlyDictionary<string, string> GetCloneSources(DataForgeDomain domain) => RequireDomain(domain).Clones;

    private static DomainData RequireDomain(DataForgeDomain domain)
    {
        if (!Data.TryGetValue(domain, out DomainData? data)) throw new ArgumentOutOfRangeException(nameof(domain));
        return data;
    }

    internal static StringComparer Comparer(DataForgeDomain domain) => domain == DataForgeDomain.Localization
        ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    internal static void SetWarningSink(Action<string>? warning) => Warning = warning;

    internal static DataForgeApplyScope BeginApply(DataForgeDomain domain, bool isAuthority,
        IEnumerable<string>? affectedKeys = null, bool fullRefresh = false, string? language = null)
    {
        RequireDomain(domain);
        return new DataForgeApplyScope(domain, SessionId, isAuthority, affectedKeys,
            fullRefresh || affectedKeys == null, language ?? "");
    }

    internal static void Record(DataForgeDomain domain, long sessionId, bool authority, string language,
        IEnumerable<string> affectedKeys, bool fullRefresh, bool completed,
        IEnumerable<string>? configuredKeys, IEnumerable<KeyValuePair<string, string>>? cloneSources)
    {
        // An old re-entrant apply may unwind after world shutdown. Never revive it.
        if (sessionId != SessionId) return;
        DomainData data = RequireDomain(domain);
        HashSet<string> overrides = new(Comparer(domain));
        Dictionary<string, string> clones = new(Comparer(domain));
        if (completed)
        {
            foreach (string key in configuredKeys ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(key)) overrides.Add(key);
            foreach (KeyValuePair<string, string> pair in cloneSources ?? Array.Empty<KeyValuePair<string, string>>())
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)) clones[pair.Key] = pair.Value;
        }
        data.Overrides = overrides;
        data.Keys = Array.AsReadOnly(overrides.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        data.Clones = new ReadOnlyDictionary<string, string>(clones);
        DataForgeChangeKind kind = completed ? DataForgeChangeKind.Applied : DataForgeChangeKind.Failed;
        data.State = new DataForgeDomainState(domain, SessionId, data.State.Revision + 1, kind, authority, language);
        Queue(domain, affectedKeys, fullRefresh || !completed);
    }

    private static void Queue(DataForgeDomain domain, IEnumerable<string> keys, bool fullRefresh)
    {
        if (!Pending.TryGetValue(domain, out PendingChange? pending))
            Pending.Add(domain, pending = new PendingChange(domain));
        pending.FullRefresh |= fullRefresh;
        foreach (string key in keys)
            if (!string.IsNullOrWhiteSpace(key)) pending.Keys.Add(key);
    }

    internal static void ResetSession(bool isAuthority)
    {
        SessionId++;
        Pending.Clear();
        foreach (DataForgeDomain domain in Domains)
        {
            Data[domain] = new DomainData(domain, SessionId, isAuthority);
            Queue(domain, Array.Empty<string>(), true);
        }
    }

    internal static void DispatchPending()
    {
        if (Dispatching || Pending.Count == 0) return;
        DataForgeChange[] changes = Pending.OrderBy(pair => pair.Key)
            .Select(pair => new DataForgeChange(Data[pair.Key].State, pair.Value.Keys, pair.Value.FullRefresh)).ToArray();
        Pending.Clear();
        Dispatching = true;
        try
        {
            foreach (DataForgeChange change in changes)
            {
                Action<DataForgeChange>? handlers = Changed;
                if (handlers == null) continue;
                foreach (Action<DataForgeChange> handler in handlers.GetInvocationList())
                {
                    // A subscriber can initiate a world reset. Do not deliver the old session further.
                    if (change.SessionId != SessionId) break;
                    try { handler(change); }
                    catch (Exception ex)
                    {
                        try { Warning?.Invoke($"DataForge API subscriber failed: {ex.GetType().Name}: {ex.Message}"); }
                        catch { /* Logging must not interrupt another subscriber or game-data application. */ }
                    }
                }
            }
        }
        finally { Dispatching = false; }
    }

    internal static void Shutdown()
    {
        ResetSession(false);
        DispatchPending();
        Changed = null;
        Warning = null;
    }
}

// A scope is started only after readiness/no-change guards. Completion means the
// apply pass returned normally, not that every YAML entry or deferred visual succeeded.
internal sealed class DataForgeApplyScope : IDisposable
{
    private readonly DataForgeDomain Domain;
    private readonly long SessionId;
    private readonly bool Authority;
    private readonly string[] AffectedKeys;
    private readonly bool FullRefresh;
    private readonly string Language;
    private bool Completed;
    private bool Disposed;
    private string[] ConfiguredKeys = Array.Empty<string>();
    private KeyValuePair<string, string>[] CloneSources = Array.Empty<KeyValuePair<string, string>>();

    internal DataForgeApplyScope(DataForgeDomain domain, long sessionId, bool authority,
        IEnumerable<string>? affectedKeys, bool fullRefresh, string language)
    {
        Domain = domain; SessionId = sessionId; Authority = authority;
        AffectedKeys = (affectedKeys ?? Array.Empty<string>()).ToArray();
        FullRefresh = fullRefresh; Language = language;
    }

    internal void Complete(IEnumerable<string>? configuredOverrideKeys = null,
        IEnumerable<KeyValuePair<string, string>>? cloneSources = null)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(DataForgeApplyScope));
        ConfiguredKeys = (configuredOverrideKeys ?? Array.Empty<string>()).ToArray();
        CloneSources = (cloneSources ?? Array.Empty<KeyValuePair<string, string>>()).ToArray();
        Completed = true;
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        DataForgeApi.Record(Domain, SessionId, Authority, Language, AffectedKeys, FullRefresh,
            Completed, ConfiguredKeys, CloneSources);
    }
}
