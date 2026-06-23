using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace DataForge;

internal static class DataForgeResourceMap
{
    private const string ResourceMapFileName = "z_resourcemap.txt";
    private const int UnknownTierSortValue = 999_999;
    private static readonly object Sync = new();
    private static Dictionary<string, int> ResourceTierByToken = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ItemDrop?> ItemDropCache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, RecipeLookupEntry>? RecipeOutputLookup;
    private static readonly Dictionary<string, DataForgeItemSortGroup> ItemSortGroupCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> ItemTierSortValueCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime LoadedWriteTimeUtc = DateTime.MinValue;
    private static bool Loaded;
    private static int CachedObjectDbItemCount = -1;
    private static int CachedObjectDbRecipeCount = -1;

    private static string ConfigDirectory => Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName);
    private static string ResourceMapPath => Path.Combine(ConfigDirectory, ResourceMapFileName);

    private readonly struct RecipeLookupEntry
    {
        internal RecipeLookupEntry(Recipe recipe, int? tier)
        {
            Recipe = recipe;
            Tier = tier;
        }

        internal Recipe Recipe { get; }
        internal int? Tier { get; }
    }

    internal static string BuildSortKey(int groupRank, int tierSortValue, string name)
    {
        return string.Join("|", new[]
        {
            Math.Max(0, groupRank).ToString("D3", CultureInfo.InvariantCulture),
            Math.Max(0, tierSortValue).ToString("D6", CultureInfo.InvariantCulture),
            name ?? ""
        });
    }

    internal static string BuildTierSortKey(int tierSortValue, string name)
    {
        return BuildSortKey(0, tierSortValue, name);
    }

    internal static string BuildItemSortKey(string? itemName, int tierSortValue, string name)
    {
        DataForgeItemSortGroup group = GetItemSortGroup(itemName);
        return string.Join("|", new[]
        {
            Math.Max(0, group.BigGroupRank).ToString("D3", CultureInfo.InvariantCulture),
            Math.Max(0, group.SubGroupRank).ToString("D3", CultureInfo.InvariantCulture),
            Math.Max(0, group.DetailRank).ToString("D3", CultureInfo.InvariantCulture),
            Math.Max(0, tierSortValue).ToString("D6", CultureInfo.InvariantCulture),
            name ?? ""
        });
    }

    private static DataForgeItemSortGroup GetItemSortGroup(string? itemName)
    {
        EnsureObjectDbCacheFresh();
        string cacheKey = CleanPrefabName(itemName);
        if (cacheKey.Length > 0 && ItemSortGroupCache.TryGetValue(cacheKey, out DataForgeItemSortGroup cachedGroup))
        {
            return cachedGroup;
        }

        ItemDrop? itemDrop = ResolveItemDrop(itemName);
        DataForgeItemSortGroup group = DataForgeItemSortClassifier.Classify(itemName, itemDrop?.m_itemData?.m_shared);
        if (cacheKey.Length > 0)
        {
            ItemSortGroupCache[cacheKey] = group;
        }

        return group;
    }

    internal static int GetItemTierSortValue(string? itemName)
    {
        EnsureObjectDbCacheFresh();
        string cacheKey = CleanPrefabName(itemName);
        if (cacheKey.Length > 0 && ItemTierSortValueCache.TryGetValue(cacheKey, out int cachedSortValue))
        {
            return cachedSortValue;
        }

        int? tier = GetKnownTierForItem(itemName);
        Recipe? recipe = ResolveRecipeForItem(itemName);
        int? recipeTier = GetKnownTierForRecipe(recipe);
        int sortValue = SortValue(MaxTier(tier, recipeTier));
        if (cacheKey.Length > 0)
        {
            ItemTierSortValueCache[cacheKey] = sortValue;
        }

        return sortValue;
    }

    internal static int GetResourceTierSortValue(IEnumerable<string?> resourceNames)
    {
        return SortValue(GetKnownTierForResourceNames(resourceNames));
    }

    private static int SortValue(int? tier)
    {
        return tier ?? UnknownTierSortValue;
    }

    private static int? GetKnownTierForRecipe(Recipe? recipe)
    {
        if (recipe?.m_resources == null)
        {
            return null;
        }

        return GetKnownTierForResourceNames(recipe.m_resources
            .Where(requirement => requirement?.m_resItem != null && requirement.m_amount > 0)
            .Select(requirement => requirement.m_resItem.name));
    }

    private static int? GetKnownTierForItem(string? itemName)
    {
        ItemDrop? itemDrop = ResolveItemDrop(itemName);
        List<string?> tokens = new()
        {
            itemName,
            itemDrop != null ? itemDrop.name : null,
            itemDrop?.m_itemData?.m_shared?.m_name,
            StripLocalizationToken(itemDrop?.m_itemData?.m_shared?.m_name)
        };

        if (itemDrop?.m_itemData?.m_shared?.m_name is { Length: > 0 } sharedName &&
            Localization.instance != null)
        {
            tokens.Add(Localization.instance.Localize(sharedName));
        }

        return GetKnownTierForResourceNames(tokens);
    }

    private static int? GetKnownTierForResourceNames(IEnumerable<string?> resourceNames)
    {
        EnsureLoaded();
        int? tier = null;
        foreach (string? resourceName in resourceNames)
        {
            foreach (string token in GetResourceTokens(resourceName))
            {
                if (ResourceTierByToken.TryGetValue(token, out int mappedTier))
                {
                    tier = MaxTier(tier, mappedTier);
                }
            }
        }

        return tier;
    }

    private static int? MaxTier(int? left, int? right)
    {
        if (!left.HasValue)
        {
            return right;
        }

        if (!right.HasValue)
        {
            return left;
        }

        return Math.Max(left.Value, right.Value);
    }

    private static IEnumerable<string> GetResourceTokens(string? value)
    {
        string cleaned = CleanPrefabName(value);
        if (cleaned.Length == 0)
        {
            yield break;
        }

        yield return NormalizeResourceToken(cleaned);
        string stripped = StripLocalizationToken(cleaned);
        if (stripped.Length > 0)
        {
            yield return NormalizeResourceToken(stripped);
        }
    }

    private static void EnsureLoaded()
    {
        Directory.CreateDirectory(ConfigDirectory);
        EnsureDefaultResourceMapFile();
        DateTime writeTimeUtc = File.Exists(ResourceMapPath)
            ? File.GetLastWriteTimeUtc(ResourceMapPath)
            : DateTime.MinValue;

        lock (Sync)
        {
            if (Loaded && writeTimeUtc == LoadedWriteTimeUtc)
            {
                return;
            }

            ResourceTierByToken = LoadResourceMap(ResourceMapPath);
            LoadedWriteTimeUtc = writeTimeUtc;
            Loaded = true;
            ClearSortCaches();
        }
    }

    private static void EnsureObjectDbCacheFresh()
    {
        int itemCount = ObjectDB.instance?.m_items?.Count ?? -1;
        int recipeCount = ObjectDB.instance?.m_recipes?.Count ?? -1;
        if (itemCount == CachedObjectDbItemCount && recipeCount == CachedObjectDbRecipeCount)
        {
            return;
        }

        CachedObjectDbItemCount = itemCount;
        CachedObjectDbRecipeCount = recipeCount;
        ClearSortCaches();
    }

    private static void ClearSortCaches()
    {
        ItemDropCache.Clear();
        RecipeOutputLookup = null;
        ItemSortGroupCache.Clear();
        ItemTierSortValueCache.Clear();
    }

    private static Dictionary<string, int> LoadResourceMap(string path)
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return map;
        }

        int currentTier = -1;
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) &&
                line.EndsWith("]", StringComparison.Ordinal))
            {
                currentTier++;
                continue;
            }

            if (currentTier < 0)
            {
                currentTier = 0;
            }

            string token = NormalizeResourceToken(line);
            if (token.Length > 0 && !map.ContainsKey(token))
            {
                map[token] = currentTier;
            }
        }

        return map;
    }

    private static void EnsureDefaultResourceMapFile()
    {
        if (File.Exists(ResourceMapPath))
        {
            return;
        }

        File.WriteAllText(ResourceMapPath, DefaultResourceMapText());
    }

    private static string StripComment(string line)
    {
        int commentIndex = line.IndexOf('#');
        return commentIndex >= 0 ? line.Substring(0, commentIndex) : line;
    }

    internal static string NormalizeResourceToken(string? token)
    {
        string text = CleanPrefabName(token);
        if (text.StartsWith("$item_", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("$item_".Length);
        }
        else if (text.StartsWith("$", StringComparison.Ordinal))
        {
            text = text.Substring(1);
        }

        return new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static string CleanPrefabName(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? ""
            : name!.Replace("(Clone)", "").Trim();
    }

    private static string StripLocalizationToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        string trimmed = value!.Trim();
        return trimmed.StartsWith("$item_", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring("$item_".Length)
            : trimmed.TrimStart('$');
    }

    private static ItemDrop? ResolveItemDrop(string? itemName)
    {
        if (ObjectDB.instance == null || string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        string normalized = CleanPrefabName(itemName);
        if (ItemDropCache.TryGetValue(normalized, out ItemDrop? cachedItemDrop))
        {
            return cachedItemDrop;
        }

        GameObject? prefab = ObjectDB.instance.GetItemPrefab(normalized);
        if (prefab != null && prefab.TryGetComponent(out ItemDrop itemDrop))
        {
            ItemDropCache[normalized] = itemDrop;
            return itemDrop;
        }

        ItemDrop? resolved = ObjectDB.instance.m_items
            .Where(item => item != null)
            .Select(item => item.GetComponent<ItemDrop>())
            .FirstOrDefault(itemDrop =>
                itemDrop != null &&
                ItemNameMatches(itemDrop, normalized));
        ItemDropCache[normalized] = resolved;
        return resolved;
    }

    private static Recipe? ResolveRecipeForItem(string? itemName)
    {
        if (ObjectDB.instance?.m_recipes == null || string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        string normalized = CleanPrefabName(itemName);
        Dictionary<string, RecipeLookupEntry> lookup = GetRecipeOutputLookup();
        if (TryGetRecipeOutputLookupEntry(lookup, normalized, out RecipeLookupEntry entry))
        {
            return entry.Recipe;
        }

        return null;
    }

    private static Dictionary<string, RecipeLookupEntry> GetRecipeOutputLookup()
    {
        if (RecipeOutputLookup != null)
        {
            return RecipeOutputLookup;
        }

        EnsureLoaded();
        Dictionary<string, RecipeLookupEntry> lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (Recipe recipe in ObjectDB.instance?.m_recipes ?? Enumerable.Empty<Recipe>())
        {
            if (recipe?.m_item == null)
            {
                continue;
            }

            int? recipeTier = GetKnownTierForRecipe(recipe);
            foreach (string key in GetItemLookupKeys(recipe.m_item))
            {
                AddRecipeOutputLookupEntry(lookup, key, recipe, recipeTier);
            }
        }

        RecipeOutputLookup = lookup;
        return lookup;
    }

    private static bool TryGetRecipeOutputLookupEntry(
        Dictionary<string, RecipeLookupEntry> lookup,
        string itemName,
        out RecipeLookupEntry entry)
    {
        foreach (string key in GetLookupKeys(itemName))
        {
            if (lookup.TryGetValue(key, out entry))
            {
                return true;
            }
        }

        entry = default;
        return false;
    }

    private static void AddRecipeOutputLookupEntry(
        Dictionary<string, RecipeLookupEntry> lookup,
        string key,
        Recipe recipe,
        int? tier)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!lookup.TryGetValue(key, out RecipeLookupEntry existing) ||
            (tier ?? -1) > (existing.Tier ?? -1))
        {
            lookup[key] = new RecipeLookupEntry(recipe, tier);
        }
    }

    private static IEnumerable<string> GetItemLookupKeys(ItemDrop itemDrop)
    {
        if (itemDrop?.m_itemData?.m_shared == null)
        {
            yield break;
        }

        foreach (string? candidate in new[]
                 {
                     itemDrop.name,
                     itemDrop.gameObject.name,
                     itemDrop.m_itemData.m_shared.m_name,
                     StripLocalizationToken(itemDrop.m_itemData.m_shared.m_name)
                 })
        {
            foreach (string key in GetLookupKeys(candidate))
            {
                yield return key;
            }
        }
    }

    private static IEnumerable<string> GetLookupKeys(string? value)
    {
        string clean = CleanPrefabName(value);
        if (clean.Length > 0)
        {
            yield return clean;
        }

        string normalized = NormalizeResourceToken(clean);
        if (normalized.Length > 0 && !string.Equals(normalized, clean, StringComparison.OrdinalIgnoreCase))
        {
            yield return normalized;
        }
    }

    private static bool ItemNameMatches(ItemDrop itemDrop, string token)
    {
        if (itemDrop?.m_itemData?.m_shared == null)
        {
            return false;
        }

        string normalizedToken = NormalizeResourceToken(token);
        foreach (string candidate in new[]
                 {
                     itemDrop.name,
                     itemDrop.gameObject.name,
                     itemDrop.m_itemData.m_shared.m_name,
                     StripLocalizationToken(itemDrop.m_itemData.m_shared.m_name)
                 })
        {
            if (CleanPrefabName(candidate).Equals(token, StringComparison.OrdinalIgnoreCase) ||
                NormalizeResourceToken(candidate).Equals(normalizedToken, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string DefaultResourceMapText() =>
        string.Join(Environment.NewLine, new[]
        {
            "# DataForge resource tier map.",
            "# User-editable sorting data for generated item, recipe, and piece reference/full scaffold files.",
            "# Sections near the top are lower tier; sections near the bottom are higher tier.",
            "# Generated files use the same direction: lower tiers first, higher tiers later.",
            "# Edit this file to change item/recipe/piece generated file sorting.",
            "",
            "[Meadows]",
            "Wood",
            "Stone",
            "Resin",
            "Dandelion",
            "Flint",
            "LeatherScraps",
            "BoneFragments",
            "Honey",
            "Raspberry",
            "Blueberries",
            "DeerHide",
            "DeerMeat",
            "Feathers",
            "GreydwarfEye",
            "RawMeat",
            "",
            "[BlackForest]",
            "HardAntler",
            "Bronze",
            "BronzeNails",
            "Copper",
            "Tin",
            "Ectoplasm",
            "SurtlingCore",
            "TrollHide",
            "BjornHide",
            "FineWood",
            "AncientSeed",
            "Carrot",
            "BjornMeat",
            "BjornPaw",
            "RoundLog",
            "Thistle",
            "",
            "[Swamp]",
            "Iron",
            "Ooze",
            "Entrails",
            "Guck",
            "Bloodbag",
            "Chain",
            "ElderBark",
            "IronNails",
            "Root",
            "Turnip",
            "WitheredBone",
            "CuredSquirrelHamstring",
            "",
            "[Ocean]",
            "Chitin",
            "SerpentScale",
            "SerpentMeat",
            "",
            "[Mountain]",
            "Silver",
            "Crystal",
            "DragonEgg",
            "JuteRed",
            "Obsidian",
            "WolfClaw",
            "WolfFang",
            "WolfHairBundle",
            "WolfMeat",
            "WolfPelt",
            "",
            "[Plains]",
            "UndeadBjornRibcage",
            "BlackMetal",
            "DragonTear",
            "Barley",
            "BarleyFlour",
            "ChickenEgg",
            "ChickenMeat",
            "Flax",
            "GoblinTotem",
            "LinenThread",
            "LoxMeat",
            "LoxPelt",
            "Needle",
            "Tar",
            "",
            "[Mistlands]",
            "Eitr",
            "Bilebag",
            "BlackCore",
            "BlackMarble",
            "BugMeat",
            "Carapace",
            "DvergrKeyFragment",
            "DvergrNeedle",
            "GiantBloodSack",
            "HareMeat",
            "JuteBlue",
            "Mandible",
            "Sap",
            "ScaleHide",
            "Softtissue",
            "Wisp",
            "YagluthDrop",
            "YggdrasilWood",
            "",
            "[Ashlands]",
            "FlametalNew",
            "AskBladder",
            "AskHide",
            "AsksvinEgg",
            "AsksvinMeat",
            "Blackwood",
            "BoneMawSerpentMeat",
            "BonemawSerpentTooth",
            "CelestialFeather",
            "CeramicPlate",
            "CharcoalResin",
            "CharredBone",
            "CharredCogwheel",
            "Charredskull",
            "Grausten",
            "MoltenCore",
            "MorgenHeart",
            "MorgenSinew",
            "ProustitePowder",
            "SulfurStone",
            "VoltureEgg",
            "VoltureMeat",
            ""
        });
}
