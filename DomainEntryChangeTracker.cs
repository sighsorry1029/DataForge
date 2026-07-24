using System;
using System.Collections.Generic;

namespace DataForge;

internal sealed class DomainEntryChangeTracker<TEntry>
{
    private readonly Func<TEntry, string?> _keySelector;
    private readonly Func<IReadOnlyList<TEntry>, string> _signatureBuilder;
    private Dictionary<string, string> _signatures = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _pendingChangedKeys;
    private bool _hasPendingScopedApply;
    private bool _forceNextFullApply = true;

    internal DomainEntryChangeTracker(
        Func<TEntry, string?> keySelector,
        Func<IReadOnlyList<TEntry>, string> signatureBuilder)
    {
        _keySelector = keySelector;
        _signatureBuilder = signatureBuilder;
    }

    internal void SetEntries(IReadOnlyList<TEntry> entries)
    {
        Dictionary<string, string> signatures = BuildSignatures(entries);
        if (!_forceNextFullApply)
        {
            _pendingChangedKeys = GetChangedKeys(_signatures, signatures);
            _hasPendingScopedApply = true;
        }

        _signatures = signatures;
    }

    internal HashSet<string>? ConsumeChangedKeys()
    {
        if (_forceNextFullApply)
        {
            _forceNextFullApply = false;
            _pendingChangedKeys = null;
            _hasPendingScopedApply = false;
            return null;
        }

        if (!_hasPendingScopedApply)
        {
            return null;
        }

        HashSet<string> changedKeys = _pendingChangedKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _pendingChangedKeys = null;
        _hasPendingScopedApply = false;
        return changedKeys;
    }

    internal void RequireFullApply()
    {
        _forceNextFullApply = true;
        _pendingChangedKeys = null;
        _hasPendingScopedApply = false;
    }

    private Dictionary<string, string> BuildSignatures(IReadOnlyList<TEntry> entries)
    {
        Dictionary<string, List<TEntry>> entriesByKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (TEntry entry in entries)
        {
            string? key = _keySelector(entry);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!entriesByKey.TryGetValue(key!, out List<TEntry>? keyedEntries))
            {
                keyedEntries = new List<TEntry>();
                entriesByKey[key!] = keyedEntries;
            }

            keyedEntries.Add(entry);
        }

        Dictionary<string, string> signatures = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<TEntry>> pair in entriesByKey)
        {
            signatures[pair.Key] = _signatureBuilder(pair.Value);
        }

        return signatures;
    }

    private static HashSet<string> GetChangedKeys(
        Dictionary<string, string> oldSignatures,
        Dictionary<string, string> newSignatures)
    {
        HashSet<string> changedKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in oldSignatures)
        {
            if (!newSignatures.TryGetValue(pair.Key, out string? newSignature) ||
                !string.Equals(pair.Value, newSignature, StringComparison.Ordinal))
            {
                changedKeys.Add(pair.Key);
            }
        }

        foreach (KeyValuePair<string, string> pair in newSignatures)
        {
            if (!oldSignatures.TryGetValue(pair.Key, out string? oldSignature) ||
                !string.Equals(oldSignature, pair.Value, StringComparison.Ordinal))
            {
                changedKeys.Add(pair.Key);
            }
        }

        return changedKeys;
    }
}

internal static class DataForgeCloneDependencyOrder
{
    internal static List<TEntry> GetAcyclicOrder<TEntry>(
        IReadOnlyDictionary<string, TEntry> entriesByTarget,
        Func<TEntry, string> sourceKeySelector,
        Action<TEntry, string> reportBlocked)
    {
        Dictionary<string, int> visitStates = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> blockedReasons = new(StringComparer.OrdinalIgnoreCase);
        List<string> visitPath = new();
        List<TEntry> ordered = new();

        foreach (string targetKey in entriesByTarget.Keys)
        {
            Visit(targetKey);
        }

        foreach (KeyValuePair<string, string> blocked in blockedReasons)
        {
            reportBlocked(entriesByTarget[blocked.Key], blocked.Value);
        }

        return ordered;

        bool Visit(string targetKey)
        {
            if (visitStates.TryGetValue(targetKey, out int state))
            {
                if (state == 2)
                {
                    return !blockedReasons.ContainsKey(targetKey);
                }

                int cycleStart = visitPath.FindIndex(
                    key => key.Equals(targetKey, StringComparison.OrdinalIgnoreCase));
                if (cycleStart < 0)
                {
                    cycleStart = visitPath.Count - 1;
                }

                string cycle = BuildCycleDescription(visitPath, cycleStart, targetKey);
                for (int index = Math.Max(0, cycleStart); index < visitPath.Count; index++)
                {
                    blockedReasons[visitPath[index]] = $"cloneFrom cycle detected: {cycle}.";
                }

                blockedReasons[targetKey] = $"cloneFrom cycle detected: {cycle}.";
                return false;
            }

            visitStates[targetKey] = 1;
            visitPath.Add(targetKey);
            TEntry entry = entriesByTarget[targetKey];
            string sourceKey = sourceKeySelector(entry);
            if (entriesByTarget.ContainsKey(sourceKey) && !Visit(sourceKey))
            {
                if (!blockedReasons.ContainsKey(targetKey))
                {
                    blockedReasons[targetKey] =
                        $"cloneFrom dependency '{sourceKey}' reaches a cycle.";
                }
            }

            visitPath.RemoveAt(visitPath.Count - 1);
            visitStates[targetKey] = 2;
            if (blockedReasons.ContainsKey(targetKey))
            {
                return false;
            }

            ordered.Add(entry);
            return true;
        }
    }

    private static string BuildCycleDescription(
        IReadOnlyList<string> visitPath,
        int cycleStart,
        string targetKey)
    {
        List<string> cycle = new();
        for (int index = Math.Max(0, cycleStart); index < visitPath.Count; index++)
        {
            cycle.Add(visitPath[index]);
        }

        cycle.Add(targetKey);
        return string.Join(" -> ", cycle);
    }
}
