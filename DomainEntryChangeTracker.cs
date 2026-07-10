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
