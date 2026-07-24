using System;
using System.Collections.Generic;
using System.Linq;

namespace DataForge;

internal static class Program
{
    private static int Passed;

    private static void Main()
    {
        Run(nameof(ChangeTrackerScopesChangedKeys), ChangeTrackerScopesChangedKeys);
        Run(nameof(ChangeTrackerHandlesLayeredEntries), ChangeTrackerHandlesLayeredEntries);
        Run(nameof(CloneOrderPlacesSourcesBeforeDependents), CloneOrderPlacesSourcesBeforeDependents);
        Run(nameof(CloneOrderBlocksCyclesAndTheirDependents), CloneOrderBlocksCyclesAndTheirDependents);
        Run(nameof(CloneOrderBlocksSelfCycles), CloneOrderBlocksSelfCycles);
        Console.WriteLine($"DataForge logic checks passed: {Passed}");
    }

    private static void ChangeTrackerScopesChangedKeys()
    {
        DomainEntryChangeTracker<TestEntry> tracker = CreateTracker();
        tracker.SetEntries(new[] { Entry("A", "one") });
        Assert(tracker.ConsumeChangedKeys() == null, "The first apply must be full.");

        tracker.SetEntries(new[] { Entry("A", "one") });
        AssertKeys(tracker.ConsumeChangedKeys());

        tracker.SetEntries(new[] { Entry("A", "two") });
        AssertKeys(tracker.ConsumeChangedKeys(), "A");

        tracker.SetEntries(new[] { Entry("a", "two"), Entry("B", "one") });
        AssertKeys(tracker.ConsumeChangedKeys(), "B");

        tracker.SetEntries(new[] { Entry("B", "one") });
        AssertKeys(tracker.ConsumeChangedKeys(), "A");

        tracker.RequireFullApply();
        Assert(tracker.ConsumeChangedKeys() == null, "RequireFullApply must discard scoped changes.");
    }

    private static void ChangeTrackerHandlesLayeredEntries()
    {
        DomainEntryChangeTracker<TestEntry> tracker = CreateTracker();
        tracker.SetEntries(new[] { Entry("A", "one"), Entry("A", "two") });
        Assert(tracker.ConsumeChangedKeys() == null, "The first layered apply must be full.");

        tracker.SetEntries(new[] { Entry("A", "one"), Entry("A", "three") });
        AssertKeys(tracker.ConsumeChangedKeys(), "A");

        tracker.SetEntries(new[] { Entry("A", "three"), Entry("A", "one") });
        AssertKeys(tracker.ConsumeChangedKeys(), "A");
    }

    private static void CloneOrderPlacesSourcesBeforeDependents()
    {
        Dictionary<string, TestEntry> entries = new(StringComparer.OrdinalIgnoreCase)
        {
            ["C"] = Entry("C", "", "B"),
            ["B"] = Entry("B", "", "a"),
            ["A"] = Entry("A", "", "Vanilla")
        };
        List<TestEntry> blocked = new();

        List<TestEntry> ordered = DataForgeCloneDependencyOrder.GetAcyclicOrder(
            entries,
            entry => entry.Source,
            (entry, _) => blocked.Add(entry));

        AssertSequence(ordered.Select(entry => entry.Key), "A", "B", "C");
        Assert(blocked.Count == 0, "A valid clone chain must not be blocked.");
    }

    private static void CloneOrderBlocksCyclesAndTheirDependents()
    {
        Dictionary<string, TestEntry> entries = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Entry("A", "", "B"),
            ["B"] = Entry("B", "", "A"),
            ["C"] = Entry("C", "", "A"),
            ["D"] = Entry("D", "", "Vanilla")
        };
        Dictionary<string, string> blocked = new(StringComparer.OrdinalIgnoreCase);

        List<TestEntry> ordered = DataForgeCloneDependencyOrder.GetAcyclicOrder(
            entries,
            entry => entry.Source,
            (entry, reason) => blocked[entry.Key] = reason);

        AssertSequence(ordered.Select(entry => entry.Key), "D");
        AssertKeys(new HashSet<string>(blocked.Keys, StringComparer.OrdinalIgnoreCase), "A", "B", "C");
        Assert(blocked["A"].Contains("cycle", StringComparison.OrdinalIgnoreCase), "Cycle members need a cycle reason.");
        Assert(blocked["C"].Contains("reaches a cycle", StringComparison.OrdinalIgnoreCase), "Cycle dependents need a dependency reason.");
    }

    private static void CloneOrderBlocksSelfCycles()
    {
        Dictionary<string, TestEntry> entries = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Entry("A", "", "A"),
            ["B"] = Entry("B", "", "A")
        };
        HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);

        List<TestEntry> ordered = DataForgeCloneDependencyOrder.GetAcyclicOrder(
            entries,
            entry => entry.Source,
            (entry, _) => blocked.Add(entry.Key));

        Assert(ordered.Count == 0, "A self-cycle and its dependent must not be ordered.");
        AssertKeys(blocked, "A", "B");
    }

    private static DomainEntryChangeTracker<TestEntry> CreateTracker() =>
        new(
            entry => entry.Key,
            entries => string.Join("|", entries.Select(entry => entry.Value)));

    private static TestEntry Entry(string key, string value, string source = "") =>
        new(key, value, source);

    private static void Run(string name, Action check)
    {
        try
        {
            check();
            Passed++;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{name} failed: {exception.Message}", exception);
        }
    }

    private static void AssertKeys(HashSet<string>? actual, params string[] expected)
    {
        Assert(actual != null, "Expected a scoped key set, but received a full-apply marker.");
        HashSet<string> expectedSet = new(expected, StringComparer.OrdinalIgnoreCase);
        Assert(actual!.SetEquals(expectedSet),
            $"Expected keys [{string.Join(", ", expectedSet)}], got [{string.Join(", ", actual)}].");
    }

    private static void AssertSequence(IEnumerable<string> actual, params string[] expected)
    {
        string[] actualArray = actual.ToArray();
        Assert(actualArray.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase),
            $"Expected order [{string.Join(", ", expected)}], got [{string.Join(", ", actualArray)}].");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TestEntry
    {
        internal TestEntry(string key, string value, string source)
        {
            Key = key;
            Value = value;
            Source = source;
        }

        internal string Key { get; }
        internal string Value { get; }
        internal string Source { get; }
    }
}
