using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DataForge;

internal static class Program
{
    private static int Passed;

    private static void Main()
    {
        Run(nameof(ChangeTrackerScopesChangedKeys), ChangeTrackerScopesChangedKeys);
        Run(nameof(ChangeTrackerHandlesLayeredEntries), ChangeTrackerHandlesLayeredEntries);
        Run(nameof(ChangeTrackerHandlesEmptyAndReaddedEntries), ChangeTrackerHandlesEmptyAndReaddedEntries);
        Run(nameof(CloneOrderPlacesSourcesBeforeDependents), CloneOrderPlacesSourcesBeforeDependents);
        Run(nameof(CloneOrderBlocksCyclesAndTheirDependents), CloneOrderBlocksCyclesAndTheirDependents);
        Run(nameof(CloneOrderBlocksSelfCycles), CloneOrderBlocksSelfCycles);
        Run(nameof(ReferencePruningOmitsScalarDefaults), ReferencePruningOmitsScalarDefaults);
        Run(nameof(ReferencePruningRetainsZeroToolTier), ReferencePruningRetainsZeroToolTier);
        Run(nameof(ReferencePruningKeepsScalarOverrides), ReferencePruningKeepsScalarOverrides);
        Run(nameof(ReferencePruningHandlesTupleDefaults), ReferencePruningHandlesTupleDefaults);
        Run(nameof(ReferencePruningRemovesEmptyChildren), ReferencePruningRemovesEmptyChildren);
        Run(nameof(IconProtocolNormalizesLogicalNames), IconProtocolNormalizesLogicalNames);
        Run(nameof(IconProtocolRejectsUnsafeLogicalNames), IconProtocolRejectsUnsafeLogicalNames);
        Run(nameof(IconProtocolComputesLowercaseHashes), IconProtocolComputesLowercaseHashes);
        Run(nameof(IconProtocolValidatesPngHeaders), IconProtocolValidatesPngHeaders);
        Run(nameof(IconManifestRoundTripsDeterministically), IconManifestRoundTripsDeterministically);
        Run(nameof(IconManifestCountsSharedContentOnce), IconManifestCountsSharedContentOnce);
        Run(nameof(IconManifestEnforcesLimitsAndIdentity), IconManifestEnforcesLimitsAndIdentity);
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

    private static void ChangeTrackerHandlesEmptyAndReaddedEntries()
    {
        DomainEntryChangeTracker<TestEntry> tracker = CreateTracker();
        tracker.SetEntries(Array.Empty<TestEntry>());
        Assert(tracker.ConsumeChangedKeys() == null, "The first empty snapshot must be a full apply.");

        tracker.SetEntries(new[] { Entry("A", "one") });
        AssertKeys(tracker.ConsumeChangedKeys(), "A");

        tracker.SetEntries(Array.Empty<TestEntry>());
        AssertKeys(tracker.ConsumeChangedKeys(), "A");

        tracker.SetEntries(new[] { Entry("A", "one") });
        AssertKeys(tracker.ConsumeChangedKeys(), "A");

        tracker.RequireFullApply();
        tracker.SetEntries(Array.Empty<TestEntry>());
        Assert(tracker.ConsumeChangedKeys() == null, "A required full apply must survive the next snapshot update.");
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

    private static void ReferencePruningOmitsScalarDefaults()
    {
        ReferenceFixture source = new()
        {
            Override = true,
            Amount = 1,
            MaxQuality = 1,
            Scalar = 0f,
            Icon = "auto"
        };

        Assert(ReferenceValue.ClonePruned(source) == null,
            "A reference object containing only established defaults must be omitted.");
    }

    private static void ReferencePruningRetainsZeroToolTier()
    {
        ReferenceFixture source = new() { ToolTier = 0 };

        ReferenceFixture? pruned = ReferenceValue.ClonePruned(source);
        Assert(pruned?.ToolTier == 0, "An explicitly emitted tool tier 0 must be retained.");
    }

    private static void ReferencePruningKeepsScalarOverrides()
    {
        ReferenceFixture source = new()
        {
            Override = false,
            Amount = 2,
            MaxQuality = 3,
            ToolTier = 1,
            Scalar = 0.5f,
            Icon = "custom.png"
        };

        ReferenceFixture? pruned = ReferenceValue.ClonePruned(source);
        Assert(pruned != null, "Non-default reference values must be retained.");
        Assert(pruned!.Override == false, "Explicit override:false must be retained.");
        Assert(pruned.Amount == 2, "A non-default amount must be retained.");
        Assert(pruned.MaxQuality == 3, "A non-default max quality must be retained.");
        Assert(pruned.ToolTier == 1, "An emitted tool tier must be retained regardless of its value.");
        Assert(Math.Abs(pruned.Scalar.GetValueOrDefault() - 0.5f) < 0.0001f,
            "A non-zero scalar must be retained.");
        Assert(pruned.Icon == "custom.png", "A custom icon must be retained.");
    }

    private static void ReferencePruningHandlesTupleDefaults()
    {
        ReferenceFixture defaults = new()
        {
            CraftingStation = "none, 1",
            Durability = "0, 0, false, 0, false, false, 0",
            RegenMultiplier = "1, 1, 1",
            DeflectionForce = "20, 5"
        };
        Assert(ReferenceValue.ClonePruned(defaults) == null,
            "Established tuple defaults must be omitted together.");

        ReferenceFixture changed = new()
        {
            CraftingStation = "Workbench, 2",
            Durability = "100, 0, true, 0, true, false, 0",
            RegenMultiplier = "1, 1.25, 1",
            DeflectionForce = "25, 5"
        };
        ReferenceFixture? pruned = ReferenceValue.ClonePruned(changed);
        Assert(pruned?.CraftingStation == "Workbench, 2", "A non-default station tuple must be retained.");
        Assert(pruned?.Durability == changed.Durability, "A non-default durability tuple must be retained.");
        Assert(pruned?.RegenMultiplier == changed.RegenMultiplier, "A non-default regen tuple must be retained.");
        Assert(pruned?.DeflectionForce == changed.DeflectionForce, "A non-default deflection tuple must be retained.");
    }

    private static void ReferencePruningRemovesEmptyChildren()
    {
        ReferenceFixture defaults = new()
        {
            Child = new ReferenceChild { Amount = 1 },
            Children = new List<ReferenceChild>
            {
                new() { Amount = 1 },
                new() { Amount = 2 }
            }
        };

        ReferenceFixture? pruned = ReferenceValue.ClonePruned(defaults);
        Assert(pruned != null, "A list with a non-default child must be retained.");
        Assert(pruned!.Child == null, "An object containing only defaults must be removed.");
        Assert(pruned.Children?.Count == 1 && pruned.Children[0].Amount == 2,
            "Default-only list elements must be removed without dropping non-default elements.");
    }

    private static void IconProtocolNormalizesLogicalNames()
    {
        Assert(DataForgeIconProtocol.TryNormalizeLogicalName(
                "Items\\Potions\\Healing", out string nested, out _) &&
            nested == "Items/Potions/Healing.png",
            "Nested icon paths must use forward slashes and gain an implicit .png extension.");
        Assert(DataForgeIconProtocol.TryNormalizeLogicalName(
                "Items/Icon.PNG", out string extension, out _) && extension == "Items/Icon.png",
            "The PNG extension must have one canonical casing.");
        Assert(DataForgeIconProtocol.TryNormalizeLogicalName(
                "  Items/Icon.png  ", out string trimmed, out _) && trimmed == "Items/Icon.png",
            "Outer whitespace must be ignored before normalization.");
    }

    private static void IconProtocolRejectsUnsafeLogicalNames()
    {
        string[] invalidNames =
        {
            "",
            "/absolute/icon.png",
            "C:\\absolute\\icon.png",
            "../icon.png",
            "folder/./icon.png",
            "folder//icon.png",
            "folder/icon.jpg",
            "folder/icon.png/",
            "folder/icon\u0001.png",
            new string('x', DataForgeIconProtocol.MaxLogicalNameUtf8Bytes + 1),
            new string('x', DataForgeIconProtocol.MaxLogicalNameUtf8Bytes - 3)
        };

        foreach (string invalidName in invalidNames)
        {
            Assert(
                !DataForgeIconProtocol.TryNormalizeLogicalName(invalidName, out _, out _),
                $"Unsafe logical icon name '{invalidName}' must be rejected.");
        }
    }

    private static void IconProtocolComputesLowercaseHashes()
    {
        const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        string actual = DataForgeIconProtocol.ComputeSha256(Encoding.UTF8.GetBytes("abc"));
        Assert(actual == expected, "SHA-256 output must use the standard lowercase encoding.");
        Assert(DataForgeIconProtocol.IsValidSha256(actual), "A lowercase SHA-256 hash must validate.");
        Assert(!DataForgeIconProtocol.IsValidSha256(actual.ToUpperInvariant()),
            "Uppercase hashes must be rejected to preserve canonical manifests.");
        Assert(!DataForgeIconProtocol.IsValidSha256(actual.Substring(1)),
            "A SHA-256 hash must contain exactly 64 hexadecimal characters.");
    }

    private static void IconProtocolValidatesPngHeaders()
    {
        byte[] png = CreatePngHeader(64, 32);
        Assert(
            DataForgeIconProtocol.TryReadPngInfo(png, out int width, out int height, out _),
            "A PNG signature followed by a valid IHDR must validate.");
        Assert(width == 64 && height == 32 && (long)width * height == 2048,
            "PNG dimensions must be read from IHDR in big-endian order.");

        byte[] badSignature = (byte[])png.Clone();
        badSignature[1] = 0;
        Assert(!DataForgeIconProtocol.TryReadPngInfo(badSignature, out _, out _, out _),
            "An invalid PNG signature must be rejected.");

        byte[] badChunk = (byte[])png.Clone();
        badChunk[12] = (byte)'X';
        Assert(!DataForgeIconProtocol.TryReadPngInfo(badChunk, out _, out _, out _),
            "The first PNG chunk must be IHDR.");

        byte[] truncated = png.Take(32).ToArray();
        Assert(!DataForgeIconProtocol.TryReadPngInfo(truncated, out _, out _, out _),
            "A PNG shorter than its complete IHDR chunk must be rejected.");

        byte[] tooWide = CreatePngHeader(DataForgeIconProtocol.MaxIconDimension + 1, 1);
        Assert(!DataForgeIconProtocol.TryReadPngInfo(tooWide, out _, out _, out _),
            "PNG dimensions above the per-axis limit must be rejected.");

        byte[] tooLarge = new byte[DataForgeIconProtocol.MaxIconBytes + 1];
        Array.Copy(png, tooLarge, png.Length);
        Assert(!DataForgeIconProtocol.TryReadPngInfo(tooLarge, out _, out _, out _),
            "PNG files above the byte limit must be rejected before decoding.");
    }

    private static void IconManifestRoundTripsDeterministically()
    {
        DataForgeIconManifestEntry first = IconEntry("items/Alpha", 1, 80, 32, 16);
        DataForgeIconManifestEntry second = IconEntry("items/한글.PNG", 2, 96, 24, 24);
        string forward = DataForgeIconProtocol.SerializeManifest(new[] { first, second });
        string reverse = DataForgeIconProtocol.SerializeManifest(new[] { second, first });
        Assert(forward == reverse, "Manifest serialization must not depend on enumeration order.");

        Assert(
            DataForgeIconProtocol.TryParseManifest(
                forward,
                out DataForgeIconManifest parsed,
                out string error),
            $"A serialized manifest must parse: {error}");
        Assert(parsed.Entries.Count == 2, "Manifest round-trip must preserve every logical icon.");
        Assert(DataForgeIconProtocol.IsValidSha256(parsed.Revision),
            "Manifest revision must be a lowercase hash of the canonical body.");
        Assert(parsed.Entries.Any(entry =>
                string.Equals(entry.LogicalName, "items/Alpha.png", StringComparison.OrdinalIgnoreCase)),
            "Manifest round-trip must preserve canonical logical names.");

        string tampered = forward.Replace("|80|32|16", "|81|32|16");
        Assert(!DataForgeIconProtocol.TryParseManifest(tampered, out _, out _),
            "Changing manifest metadata without its revision must be rejected.");
        Assert(!DataForgeIconProtocol.TryParseManifest(
                new string('x', DataForgeIconProtocol.MaxManifestCharacters + 1), out _, out _),
            "Oversized manifest text must be rejected before parsing its entries.");
    }

    private static void IconManifestCountsSharedContentOnce()
    {
        string sharedHash = DataForgeIconProtocol.ComputeSha256(new byte[] { 42 });
        List<DataForgeIconManifestEntry> aliases = new();
        for (int index = 0; index < 17; index++)
        {
            aliases.Add(new DataForgeIconManifestEntry(
                $"aliases/icon-{index}.png",
                sharedHash,
                DataForgeIconProtocol.MaxIconBytes,
                DataForgeIconProtocol.MaxIconDimension,
                DataForgeIconProtocol.MaxIconDimension));
        }

        Assert(
            DataForgeIconProtocol.TryCreateManifest(
                aliases,
                out DataForgeIconManifest manifest,
                out string error),
            $"Aliases for one content hash must count toward aggregate limits once: {error}");
        Assert(manifest.UniqueContentCount == 1 &&
               manifest.TotalBytes == DataForgeIconProtocol.MaxIconBytes &&
               manifest.TotalPixels == DataForgeIconProtocol.MaxIconPixels,
            "Aggregate byte and pixel totals must describe unique content hashes.");
    }

    private static void IconManifestEnforcesLimitsAndIdentity()
    {
        DataForgeIconManifestEntry collisionA = IconEntry("Icons/Same.png", 1, 40, 1, 1);
        DataForgeIconManifestEntry collisionB = IconEntry("icons/same.PNG", 2, 40, 1, 1);
        Assert(!DataForgeIconProtocol.TryCreateManifest(
                new[] { collisionA, collisionB }, out _, out _),
            "Logical icon names that differ only by case must collide.");

        string collisionBody = ManifestLine(collisionA) + "\n" + ManifestLine(collisionB);
        string collisionPayload = "DATAFORGE_ICONS_V1\nrevision=" +
                                  DataForgeIconProtocol.ComputeSha256(Encoding.UTF8.GetBytes(collisionBody)) +
                                  "\n" + collisionBody;
        Assert(!DataForgeIconProtocol.TryParseManifest(collisionPayload, out _, out _),
            "Manifest parsing must independently reject case-insensitive name collisions.");

        List<DataForgeIconManifestEntry> tooMany = new();
        for (int index = 0; index <= DataForgeIconProtocol.MaxIconCount; index++)
        {
            tooMany.Add(IconEntry($"many/{index}.png", index, 33, 1, 1));
        }

        Assert(!DataForgeIconProtocol.TryCreateManifest(tooMany, out _, out _),
            "The manifest icon-count limit must be enforced.");

        List<DataForgeIconManifestEntry> tooManyBytes = new();
        for (int index = 0; index < 5; index++)
        {
            tooManyBytes.Add(IconEntry(
                $"bytes/{index}.png",
                index,
                DataForgeIconProtocol.MaxIconBytes,
                1,
                1));
        }

        Assert(!DataForgeIconProtocol.TryCreateManifest(tooManyBytes, out _, out _),
            "Unique icon content above the aggregate byte limit must be rejected.");

        List<DataForgeIconManifestEntry> tooManyPixels = new();
        for (int index = 0; index < 17; index++)
        {
            tooManyPixels.Add(IconEntry(
                $"pixels/{index}.png",
                index,
                33,
                DataForgeIconProtocol.MaxIconDimension,
                DataForgeIconProtocol.MaxIconDimension));
        }

        Assert(!DataForgeIconProtocol.TryCreateManifest(tooManyPixels, out _, out _),
            "Unique icon content above the aggregate pixel limit must be rejected.");

        string sharedHash = DataForgeIconProtocol.ComputeSha256(new byte[] { 9 });
        DataForgeIconManifestEntry sharedA = new("shared/a.png", sharedHash, 40, 2, 2);
        DataForgeIconManifestEntry sharedB = new("shared/b.png", sharedHash, 41, 2, 2);
        Assert(!DataForgeIconProtocol.TryCreateManifest(new[] { sharedA, sharedB }, out _, out _),
            "Entries sharing a hash must agree on byte length and dimensions.");
    }

    private static DataForgeIconManifestEntry IconEntry(
        string logicalName,
        int identity,
        int byteLength,
        int width,
        int height) =>
        new(
            logicalName,
            DataForgeIconProtocol.ComputeSha256(BitConverter.GetBytes(identity)),
            byteLength,
            width,
            height);

    private static string ManifestLine(DataForgeIconManifestEntry entry) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.LogicalName)) + "|" +
        entry.Hash + "|" + entry.ByteLength + "|" + entry.Width + "|" + entry.Height;

    private static byte[] CreatePngHeader(int width, int height)
    {
        byte[] bytes = new byte[33];
        byte[] signature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        Array.Copy(signature, bytes, signature.Length);
        WriteUInt32BigEndian(bytes, 8, 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        WriteUInt32BigEndian(bytes, 16, width);
        WriteUInt32BigEndian(bytes, 20, height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    private static void WriteUInt32BigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)((uint)value >> 24);
        bytes[offset + 1] = (byte)((uint)value >> 16);
        bytes[offset + 2] = (byte)((uint)value >> 8);
        bytes[offset + 3] = (byte)value;
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

    private sealed class ReferenceFixture
    {
        public bool? Override { get; set; }
        public int? Amount { get; set; }
        public int? MaxQuality { get; set; }
        public int? ToolTier { get; set; }
        public float? Scalar { get; set; }
        public string? Icon { get; set; }
        public string? CraftingStation { get; set; }
        public string? Durability { get; set; }
        public string? RegenMultiplier { get; set; }
        public string? DeflectionForce { get; set; }
        public ReferenceChild? Child { get; set; }
        public List<ReferenceChild>? Children { get; set; }
    }

    private sealed class ReferenceChild
    {
        public int? Amount { get; set; }
    }
}
