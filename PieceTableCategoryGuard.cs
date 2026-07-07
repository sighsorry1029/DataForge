using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace DataForge;

internal static class PieceTableCategoryGuard
{
    private const int VanillaCategorySlots = (int)Piece.PieceCategory.Max;

    private static readonly Dictionary<Piece.PieceCategory, string> CategoryLabels = new();
    private static readonly Dictionary<string, Piece.PieceCategory> CustomCategoriesByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Piece.PieceCategory, string> CustomCategoryNames = new();
    private static readonly Dictionary<PieceTable, NormalizationStamp> NormalizationStamps = new(ReferenceComparer<PieceTable>.Instance);
    private static readonly FieldInfo? SelectedCategoryField = AccessTools.Field(typeof(PieceTable), "m_selectedCategory");
    private static readonly FieldInfo? AvailablePiecesField = AccessTools.Field(typeof(PieceTable), "m_availablePieces");
    private static readonly FieldInfo? HudCategoryClickField = AccessTools.Field(typeof(UIInputHandler), "m_onLeftDown");
    private static int CustomCategoryVersion;

    internal static void Normalize(PieceTable? pieceTable)
    {
        if (!pieceTable || pieceTable.m_categories == null)
        {
            return;
        }

        if (IsNormalizationCurrent(pieceTable))
        {
            return;
        }

        RemoveInvalidPieces(pieceTable);
        EnsureCategoryStorage(pieceTable);
        pieceTable.m_categoryLabels ??= new List<string>();
        while (pieceTable.m_categoryLabels.Count > pieceTable.m_categories.Count)
        {
            pieceTable.m_categoryLabels.RemoveAt(pieceTable.m_categoryLabels.Count - 1);
        }

        for (int index = 0; index < pieceTable.m_categories.Count; index++)
        {
            Piece.PieceCategory category = pieceTable.m_categories[index];
            string label = GetLabel(category);
            if (index >= pieceTable.m_categoryLabels.Count)
            {
                pieceTable.m_categoryLabels.Add(label);
                continue;
            }

            if (string.IsNullOrWhiteSpace(pieceTable.m_categoryLabels[index]))
            {
                pieceTable.m_categoryLabels[index] = label;
            }
        }

        NormalizeSelectedCategory(pieceTable);
        EnsureHudCategoryTabs(pieceTable);
        RememberNormalization(pieceTable);
    }

    internal static void EnsureSelectedCategory(PieceTable? pieceTable)
    {
        if (!pieceTable || pieceTable.m_categories == null)
        {
            return;
        }

        if (IsNormalizationCurrent(pieceTable))
        {
            return;
        }

        EnsureCategoryStorage(pieceTable);
        NormalizeSelectedCategory(pieceTable);
        RememberNormalization(pieceTable);
    }

    internal static bool TryResolveCustomCategory(string categoryName, out Piece.PieceCategory category)
    {
        return CustomCategoriesByName.TryGetValue(categoryName.Trim(), out category);
    }

    internal static Piece.PieceCategory GetOrCreateCustomCategory(string categoryName)
    {
        string normalizedName = categoryName.Trim();
        if (CustomCategoriesByName.TryGetValue(normalizedName, out Piece.PieceCategory existing))
        {
            return existing;
        }

        Piece.PieceCategory category = AllocateCustomCategory();
        CustomCategoriesByName[normalizedName] = category;
        CustomCategoryNames[category] = normalizedName;
        CategoryLabels[category] = normalizedName;
        CustomCategoryVersion++;
        NormalizationStamps.Clear();
        DataForgePlugin.Log.LogInfo($"Created piece category '{normalizedName}'.");
        return category;
    }

    internal static bool TryGetCustomCategoryName(Piece.PieceCategory category, out string name)
    {
        return CustomCategoryNames.TryGetValue(category, out name);
    }

    internal static void AppendCustomCategoryValues(ref Array values)
    {
        if (CustomCategoryNames.Count == 0)
        {
            return;
        }

        List<Piece.PieceCategory> categories = values.Cast<Piece.PieceCategory>().ToList();
        foreach (Piece.PieceCategory category in CustomCategoryNames.Keys.OrderBy(static category => (int)category))
        {
            if (!categories.Contains(category))
            {
                categories.Add(category);
            }
        }

        values = categories.ToArray();
    }

    internal static void AppendCustomCategoryNames(ref string[] names)
    {
        if (CustomCategoryNames.Count == 0)
        {
            return;
        }

        List<string> categoryNames = names.ToList();
        foreach (KeyValuePair<Piece.PieceCategory, string> pair in CustomCategoryNames.OrderBy(static pair => (int)pair.Key))
        {
            if (!categoryNames.Contains(pair.Value))
            {
                categoryNames.Add(pair.Value);
            }
        }

        names = categoryNames.ToArray();
    }

    private static Piece.PieceCategory AllocateCustomCategory()
    {
        HashSet<int> used = new(Enum.GetValues(typeof(Piece.PieceCategory)).Cast<Piece.PieceCategory>().Select(static category => (int)category));
        used.UnionWith(CustomCategoryNames.Keys.Select(static category => (int)category));
        foreach (PieceTable pieceTable in Resources.FindObjectsOfTypeAll<PieceTable>())
        {
            if (!pieceTable || pieceTable.m_categories == null)
            {
                continue;
            }

            foreach (Piece.PieceCategory category in pieceTable.m_categories)
            {
                used.Add((int)category);
            }
        }

        int value = VanillaCategorySlots + 1;
        while (used.Contains(value))
        {
            value++;
        }

        return (Piece.PieceCategory)value;
    }

    private static void RemoveInvalidPieces(PieceTable pieceTable)
    {
        if (pieceTable.m_pieces == null)
        {
            return;
        }

        for (int index = pieceTable.m_pieces.Count - 1; index >= 0; index--)
        {
            GameObject piece = pieceTable.m_pieces[index];
            if (!piece || piece.GetComponent<Piece>() == null)
            {
                pieceTable.m_pieces.RemoveAt(index);
            }
        }
    }

    internal static void EnsureCategory(PieceTable? pieceTable, Piece.PieceCategory category)
    {
        if (!pieceTable)
        {
            return;
        }

        EnsureCategoryStorage(pieceTable, category);
        if (!IsSelectableCategory(pieceTable, category))
        {
            return;
        }

        pieceTable.m_categories ??= new List<Piece.PieceCategory>();
        pieceTable.m_categoryLabels ??= new List<string>();
        Normalize(pieceTable);
        if (pieceTable.m_categories.Contains(category))
        {
            return;
        }

        pieceTable.m_categories.Add(category);
        pieceTable.m_categoryLabels.Add(GetLabel(category));
        NormalizeSelectedCategory(pieceTable);
        EnsureHudCategoryTabs(pieceTable);
        RememberNormalization(pieceTable);
    }

    private static bool IsNormalizationCurrent(PieceTable pieceTable)
    {
        return NormalizationStamps.TryGetValue(pieceTable, out NormalizationStamp stamp) &&
               stamp.Equals(BuildNormalizationStamp(pieceTable));
    }

    private static void RememberNormalization(PieceTable pieceTable)
    {
        NormalizationStamps[pieceTable] = BuildNormalizationStamp(pieceTable);
    }

    private static NormalizationStamp BuildNormalizationStamp(PieceTable pieceTable)
    {
        int categoryHash = 17;
        int missingLabels = 0;
        List<Piece.PieceCategory>? categories = pieceTable.m_categories;
        List<string>? labels = pieceTable.m_categoryLabels;
        int categoryCount = categories?.Count ?? 0;
        int labelCount = labels?.Count ?? 0;

        if (categories != null)
        {
            for (int index = 0; index < categories.Count; index++)
            {
                categoryHash = unchecked(categoryHash * 31 + (int)categories[index]);
                string label = labels != null && index < labels.Count ? labels[index] : "";
                if (string.IsNullOrWhiteSpace(label))
                {
                    missingLabels++;
                }

                categoryHash = unchecked(categoryHash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(label.Trim()));
            }
        }

        int availableSlots = AvailablePiecesField?.GetValue(pieceTable) is ICollection availablePieces
            ? availablePieces.Count
            : 0;
        int selectedSlots = pieceTable.m_selectedPiece?.Length ?? 0;
        int lastSelectedSlots = pieceTable.m_lastSelectedPiece?.Length ?? 0;
        int pieceCount = pieceTable.m_pieces?.Count ?? 0;
        int hudTabs = Hud.instance && Hud.instance.m_pieceCategoryTabs != null
            ? Hud.instance.m_pieceCategoryTabs.Length
            : 0;
        int selectedCategory = SelectedCategoryField?.GetValue(pieceTable) is Piece.PieceCategory category
            ? (int)category
            : int.MinValue;

        return new NormalizationStamp(
            CustomCategoryVersion,
            categoryCount,
            labelCount,
            missingLabels,
            categoryHash,
            availableSlots,
            selectedSlots,
            lastSelectedSlots,
            pieceCount,
            hudTabs,
            selectedCategory);
    }

    private static void EnsureCategoryStorage(PieceTable pieceTable, Piece.PieceCategory? extraCategory = null)
    {
        int requiredSlots = GetRequiredCategorySlotCount(pieceTable, extraCategory);
        EnsureAvailablePieceSlots(pieceTable, requiredSlots);
        EnsureSelectedPieceSlots(pieceTable, requiredSlots);
    }

    private static int GetRequiredCategorySlotCount(PieceTable pieceTable, Piece.PieceCategory? extraCategory = null)
    {
        int requiredSlots = Math.Max(0, VanillaCategorySlots);
        if (AvailablePiecesField?.GetValue(pieceTable) is ICollection availablePieces)
        {
            requiredSlots = Math.Max(requiredSlots, availablePieces.Count);
        }

        ConsiderCategory(extraCategory);
        if (pieceTable.m_categories != null)
        {
            foreach (Piece.PieceCategory category in pieceTable.m_categories)
            {
                ConsiderCategory(category);
            }
        }

        foreach (Piece.PieceCategory category in CustomCategoryNames.Keys)
        {
            ConsiderCategory(category);
        }

        if (pieceTable.m_pieces != null)
        {
            foreach (GameObject piecePrefab in pieceTable.m_pieces)
            {
                Piece? piece = piecePrefab ? piecePrefab.GetComponent<Piece>() : null;
                ConsiderCategory(piece != null ? piece.m_category : null);
            }
        }

        return requiredSlots;

        void ConsiderCategory(Piece.PieceCategory? maybeCategory)
        {
            if (maybeCategory is null or Piece.PieceCategory.Max or Piece.PieceCategory.All)
            {
                return;
            }

            int categoryIndex = (int)maybeCategory.Value;
            if (categoryIndex >= 0)
            {
                requiredSlots = Math.Max(requiredSlots, categoryIndex + 1);
            }
        }
    }

    private static void EnsureAvailablePieceSlots(PieceTable pieceTable, int requiredSlots)
    {
        if (AvailablePiecesField?.GetValue(pieceTable) is not IList availablePieces)
        {
            return;
        }

        while (availablePieces.Count < requiredSlots)
        {
            availablePieces.Add(new List<Piece>());
        }
    }

    private static void EnsureSelectedPieceSlots(PieceTable pieceTable, int requiredSlots)
    {
        if (pieceTable.m_selectedPiece == null || pieceTable.m_selectedPiece.Length < requiredSlots)
        {
            Array.Resize(ref pieceTable.m_selectedPiece, requiredSlots);
        }

        if (pieceTable.m_lastSelectedPiece == null || pieceTable.m_lastSelectedPiece.Length < requiredSlots)
        {
            Array.Resize(ref pieceTable.m_lastSelectedPiece, requiredSlots);
        }
    }

    private static void NormalizeSelectedCategory(PieceTable pieceTable)
    {
        if (SelectedCategoryField == null)
        {
            return;
        }

        if (SelectedCategoryField.GetValue(pieceTable) is not Piece.PieceCategory selectedCategory)
        {
            return;
        }

        if (IsSelectableCategory(pieceTable, selectedCategory) &&
            pieceTable.m_categories.Count > 0 &&
            pieceTable.m_categories.Contains(selectedCategory))
        {
            return;
        }

        SelectedCategoryField.SetValue(pieceTable, FindFallbackCategory(pieceTable));
    }

    private static Piece.PieceCategory FindFallbackCategory(PieceTable pieceTable)
    {
        foreach (Piece.PieceCategory category in pieceTable.m_categories)
        {
            if (IsSelectableCategory(pieceTable, category))
            {
                return category;
            }
        }

        return Piece.PieceCategory.Misc;
    }

    private static bool IsSelectableCategory(PieceTable pieceTable, Piece.PieceCategory category)
    {
        if (category is Piece.PieceCategory.Max or Piece.PieceCategory.All)
        {
            return false;
        }

        int categoryIndex = (int)category;
        return categoryIndex >= 0 && categoryIndex < GetAvailableCategorySlotCount(pieceTable);
    }

    private static int GetAvailableCategorySlotCount(PieceTable pieceTable)
    {
        if (AvailablePiecesField?.GetValue(pieceTable) is ICollection availablePieces &&
            availablePieces.Count > 0)
        {
            return availablePieces.Count;
        }

        return Math.Max(0, VanillaCategorySlots);
    }

    internal static string GetLabel(Piece.PieceCategory category)
    {
        if (CategoryLabels.TryGetValue(category, out string label))
        {
            return label;
        }

        foreach (PieceTable pieceTable in Resources.FindObjectsOfTypeAll<PieceTable>())
        {
            if (!pieceTable || pieceTable.m_categories == null || pieceTable.m_categoryLabels == null)
            {
                continue;
            }

            int count = Math.Min(pieceTable.m_categories.Count, pieceTable.m_categoryLabels.Count);
            for (int index = 0; index < count; index++)
            {
                if (pieceTable.m_categories[index] == category && !string.IsNullOrWhiteSpace(pieceTable.m_categoryLabels[index]))
                {
                    CategoryLabels[category] = pieceTable.m_categoryLabels[index];
                    return pieceTable.m_categoryLabels[index];
                }
            }
        }

        label = category.ToString();
        CategoryLabels[category] = label;
        return label;
    }

    private static void EnsureHudCategoryTabs(PieceTable pieceTable)
    {
        Hud hud = Hud.instance;
        if (!hud || hud.m_pieceCategoryTabs == null || hud.m_pieceCategoryTabs.Length == 0 || pieceTable.m_categories == null)
        {
            return;
        }

        int requiredTabs = pieceTable.m_categories.Count;
        if (hud.m_pieceCategoryTabs.Length >= requiredTabs)
        {
            return;
        }

        GameObject template = hud.m_pieceCategoryTabs[0];
        if (!template)
        {
            return;
        }

        List<GameObject> tabs = hud.m_pieceCategoryTabs.ToList();
        Transform parent = template.transform.parent;
        for (int index = tabs.Count; index < requiredTabs; index++)
        {
            GameObject tab = UnityEngine.Object.Instantiate(template, parent);
            tab.name = $"DataForgeCategoryTab{index}";
            tab.SetActive(false);
            AddHudCategoryClickHandler(hud, tab);
            tabs.Add(tab);
        }

        hud.m_pieceCategoryTabs = tabs.ToArray();
    }

    private static void AddHudCategoryClickHandler(Hud hud, GameObject tab)
    {
        UIInputHandler inputHandler = tab.GetComponent<UIInputHandler>();
        if (inputHandler == null || HudCategoryClickField == null)
        {
            return;
        }

        Action<UIInputHandler> callback = hud.OnLeftClickCategory;
        Delegate? current = HudCategoryClickField.GetValue(inputHandler) as Delegate;
        if (current?.GetInvocationList().Any(callback.Equals) == true)
        {
            return;
        }

        HudCategoryClickField.SetValue(inputHandler, Delegate.Combine(current, callback));
    }

    private readonly struct NormalizationStamp : IEquatable<NormalizationStamp>
    {
        private readonly int _customCategoryVersion;
        private readonly int _categoryCount;
        private readonly int _labelCount;
        private readonly int _missingLabels;
        private readonly int _categoryHash;
        private readonly int _availableSlots;
        private readonly int _selectedSlots;
        private readonly int _lastSelectedSlots;
        private readonly int _pieceCount;
        private readonly int _hudTabs;
        private readonly int _selectedCategory;

        internal NormalizationStamp(
            int customCategoryVersion,
            int categoryCount,
            int labelCount,
            int missingLabels,
            int categoryHash,
            int availableSlots,
            int selectedSlots,
            int lastSelectedSlots,
            int pieceCount,
            int hudTabs,
            int selectedCategory)
        {
            _customCategoryVersion = customCategoryVersion;
            _categoryCount = categoryCount;
            _labelCount = labelCount;
            _missingLabels = missingLabels;
            _categoryHash = categoryHash;
            _availableSlots = availableSlots;
            _selectedSlots = selectedSlots;
            _lastSelectedSlots = lastSelectedSlots;
            _pieceCount = pieceCount;
            _hudTabs = hudTabs;
            _selectedCategory = selectedCategory;
        }

        public bool Equals(NormalizationStamp other)
        {
            return _customCategoryVersion == other._customCategoryVersion &&
                   _categoryCount == other._categoryCount &&
                   _labelCount == other._labelCount &&
                   _missingLabels == other._missingLabels &&
                   _categoryHash == other._categoryHash &&
                   _availableSlots == other._availableSlots &&
                   _selectedSlots == other._selectedSlots &&
                   _lastSelectedSlots == other._lastSelectedSlots &&
                   _pieceCount == other._pieceCount &&
                   _hudTabs == other._hudTabs &&
                   _selectedCategory == other._selectedCategory;
        }

        public override bool Equals(object? obj)
        {
            return obj is NormalizationStamp other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = _customCategoryVersion;
                hash = hash * 31 + _categoryCount;
                hash = hash * 31 + _labelCount;
                hash = hash * 31 + _missingLabels;
                hash = hash * 31 + _categoryHash;
                hash = hash * 31 + _availableSlots;
                hash = hash * 31 + _selectedSlots;
                hash = hash * 31 + _lastSelectedSlots;
                hash = hash * 31 + _pieceCount;
                hash = hash * 31 + _hudTabs;
                hash = hash * 31 + _selectedCategory;
                return hash;
            }
        }
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T? left, T? right)
        {
            return ReferenceEquals(left, right);
        }

        public int GetHashCode(T value)
        {
            return RuntimeHelpers.GetHashCode(value);
        }
    }
}

[HarmonyPatch(typeof(Enum), nameof(Enum.GetValues), new[] { typeof(Type) })]
internal static class DataForgeEnumGetValuesPieceCategoryPatch
{
    private static void Postfix(Type enumType, ref Array __result)
    {
        if (enumType == typeof(Piece.PieceCategory))
        {
            PieceTableCategoryGuard.AppendCustomCategoryValues(ref __result);
        }
    }
}

[HarmonyPatch(typeof(Enum), nameof(Enum.GetNames), new[] { typeof(Type) })]
internal static class DataForgeEnumGetNamesPieceCategoryPatch
{
    private static void Postfix(Type enumType, ref string[] __result)
    {
        if (enumType == typeof(Piece.PieceCategory))
        {
            PieceTableCategoryGuard.AppendCustomCategoryNames(ref __result);
        }
    }
}

[HarmonyPatch(typeof(Hud), nameof(Hud.UpdateBuild))]
internal static class DataForgeHudUpdateBuildPieceTableCategoryGuardPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Player player)
    {
        PieceTableCategoryGuard.Normalize(player ? player.m_buildPieces : null);
    }
}

[HarmonyPatch(typeof(PieceTable), nameof(PieceTable.GetSelectedCategory))]
internal static class DataForgePieceTableGetSelectedCategoryGuardPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(PieceTable __instance)
    {
        PieceTableCategoryGuard.EnsureSelectedCategory(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.SetPlaceMode))]
internal static class DataForgePlayerSetPlaceModePieceTableCategoryGuardPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(PieceTable buildPieces)
    {
        PieceTableCategoryGuard.Normalize(buildPieces);
    }
}

[HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
internal static class DataForgePieceTableUpdateAvailableCategoryGuardPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(PieceTable __instance)
    {
        PieceTableCategoryGuard.Normalize(__instance);
    }
}

[HarmonyPatch(typeof(PieceTable), nameof(PieceTable.SetCategory))]
internal static class DataForgePieceTableSetCategoryGuardPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(PieceTable __instance, int index)
    {
        PieceTableCategoryGuard.Normalize(__instance);
        return index >= 0 && __instance.m_categories != null && index < __instance.m_categories.Count;
    }
}
