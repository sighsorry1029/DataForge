using System;
using System.Collections.Generic;
using ModAssetOwnership;

internal static class AssetOwnerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var first = new AssetOwner("a.food", "Same Name", "FoodA", new[] { "A.Resources.foods" });
        var second = new AssetOwner("b.food", "Same Name", "FoodB", new[] { "B.Resources.foods" });
        check(AssetOwnerMatching.Resolve("foods", new[] { first }) == first, "Unique embedded resource retains owner GUID");
        check(AssetOwnerMatching.Resolve("foods", new[] { first, second }) == null, "Ambiguous resource has no first-plugin winner");
        check(AssetOwnerMatching.Resolve("foods", new[] { second, first }) == null, "Resource ambiguity independent of load order");
        check(AssetOwnerMatching.Resolve("food", new[] { first }) == null, "Partial token does not establish ownership");
        check(AssetOwnerMatching.Resolve("FoodA.bundle", new[] { first }) == first, "Unique complete token supported");
        check(AssetOwnerMatching.Resolve("SameName", new[] { first, second }) == null, "Display names do not merge GUIDs");
        check(AssetOwnerMatching.Resolve("foods", new[] { new AssetOwner("x", "x", "x", new[] { "notfoods" }) }) == null, "Resource suffix boundary");
        var owners = new Dictionary<string, AssetOwner>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AssetOwnerMatching.Add(owners, ambiguous, "Soup", first);
        AssetOwnerMatching.Add(owners, ambiguous, "SOUP", second);
        AssetOwnerMatching.Add(owners, ambiguous, "Soup", first);
        check(owners.Count == 0 && ambiguous.Contains("Soup"), "Asset collision stays ambiguous despite equal owner display names");
    }
}
