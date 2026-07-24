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
    private static readonly Dictionary<string, Piece.PieceCategory> KnownCategoriesByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Piece.PieceCategory, string> KnownCategoryNames = new();
    private static readonly Dictionary<string, Piece.PieceCategory> CustomCategoriesByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Piece.PieceCategory, string> CustomCategoryNames = new();
    private static readonly Dictionary<PieceTable, NormalizationStamp> NormalizationStamps = new(ReferenceComparer<PieceTable>.Instance);
    private static readonly Dictionary<PieceTable, PieceTableCategorySnapshot> CategorySnapshots = new(ReferenceComparer<PieceTable>.Instance);
    private static readonly Dictionary<PieceTable, List<ConfiguredCategory>> ConfiguredOrders = new(ReferenceComparer<PieceTable>.Instance);
    private static readonly Dictionary<PieceTable, List<CategorySlot>> ConfiguredOrderBaselines = new(ReferenceComparer<PieceTable>.Instance);
    private static readonly Dictionary<PieceTable, List<RemovedCategorySlot>> TemporarilyPrunedCategories = new(ReferenceComparer<PieceTable>.Instance);
    private static readonly HashSet<GameObject> CreatedHudCategoryTabs = new(ReferenceComparer<GameObject>.Instance);
    private static readonly FieldInfo? SelectedCategoryField = AccessTools.Field(typeof(PieceTable), "m_selectedCategory");
    private static readonly FieldInfo? AvailablePiecesField = AccessTools.Field(typeof(PieceTable), "m_availablePieces");
    private static readonly FieldInfo? HudCategoryClickField = AccessTools.Field(typeof(UIInputHandler), "m_onLeftDown");
    private static int CustomCategoryVersion;

    internal static void ResetWorldState()
    {
        foreach (KeyValuePair<PieceTable, PieceTableCategorySnapshot> pair in CategorySnapshots.ToArray())
        {
            try
            {
                if (pair.Key)
                {
                    pair.Value.Restore(pair.Key);
                }
            }
            catch (Exception ex)
            {
                string pieceTableName = pair.Key ? pair.Key.name : "<destroyed>";
                DataForgePlugin.Log.LogWarning($"Failed to restore piece categories for '{pieceTableName}': {ex.Message}");
            }
        }

        CategorySnapshots.Clear();
        try
        {
            RemoveCreatedHudCategoryTabs();
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"Failed to remove DataForge piece category tabs: {ex.Message}");
            CreatedHudCategoryTabs.Clear();
        }
        CategoryLabels.Clear();
        KnownCategoriesByName.Clear();
        KnownCategoryNames.Clear();
        CustomCategoriesByName.Clear();
        CustomCategoryNames.Clear();
        ConfiguredOrders.Clear();
        ConfiguredOrderBaselines.Clear();
        TemporarilyPrunedCategories.Clear();
        NormalizationStamps.Clear();
        CustomCategoryVersion++;
    }

    internal static void Normalize(PieceTable? pieceTable)
    {
        if (!pieceTable || pieceTable.m_categories == null)
        {
            return;
        }

        CaptureCategorySnapshotIfNeeded(pieceTable);

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

            if (PieceOverrideManager.IsOwnerManagedHomesteadCategory(category))
            {
                continue;
            }

            if (ShouldReplaceLabel(category, pieceTable.m_categoryLabels[index], label))
            {
                pieceTable.m_categoryLabels[index] = label;
            }
        }

        ApplyConfiguredOrder(pieceTable);
        MoveOwnerManagedCategoriesToEnd(pieceTable);
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

        CaptureCategorySnapshotIfNeeded(pieceTable);

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

    internal static bool TryResolveKnownCategory(string categoryName, out Piece.PieceCategory category)
    {
        return KnownCategoriesByName.TryGetValue(categoryName.Trim(), out category);
    }

    internal static bool TryGetKnownCategoryName(Piece.PieceCategory category, out string name)
    {
        return KnownCategoryNames.TryGetValue(category, out name);
    }

    internal static void ReplaceKnownCategories(
        IReadOnlyDictionary<string, Piece.PieceCategory> categoriesByName,
        IReadOnlyDictionary<Piece.PieceCategory, string> namesByCategory)
    {
        KnownCategoriesByName.Clear();
        foreach (KeyValuePair<string, Piece.PieceCategory> pair in categoriesByName)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                KnownCategoriesByName[pair.Key.Trim()] = pair.Value;
            }
        }

        KnownCategoryNames.Clear();
        foreach (KeyValuePair<Piece.PieceCategory, string> pair in namesByCategory)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                KnownCategoryNames[pair.Key] = pair.Value.Trim();
            }
        }

        CategoryLabels.Clear();
        CustomCategoryVersion++;
        NormalizationStamps.Clear();
    }

    internal static void ReplaceConfiguredOrders(
        IReadOnlyDictionary<PieceTable, IReadOnlyList<ConfiguredCategory>> configuredOrders)
    {
        Dictionary<PieceTable, List<ConfiguredCategory>> normalized =
            new(ReferenceComparer<PieceTable>.Instance);
        foreach (KeyValuePair<PieceTable, IReadOnlyList<ConfiguredCategory>> pair in configuredOrders)
        {
            if (!pair.Key || pair.Value == null || pair.Value.Count == 0)
            {
                continue;
            }

            normalized[pair.Key] = pair.Value.ToList();
        }

        if (ConfiguredOrdersMatch(normalized))
        {
            return;
        }

        HashSet<PieceTable> affectedTables = new(ReferenceComparer<PieceTable>.Instance);
        foreach (PieceTable pieceTable in ConfiguredOrders.Keys.ToArray())
        {
            if (!pieceTable)
            {
                continue;
            }

            affectedTables.Add(pieceTable);
            RestoreConfiguredOrderBaseline(pieceTable);
        }

        ConfiguredOrders.Clear();
        ConfiguredOrderBaselines.Clear();
        foreach (KeyValuePair<PieceTable, List<ConfiguredCategory>> pair in normalized)
        {
            PieceTable pieceTable = pair.Key;
            CaptureCategorySnapshotIfNeeded(pieceTable);
            ConfiguredOrderBaselines[pieceTable] = CaptureCategorySlots(pieceTable);
            ConfiguredOrders[pieceTable] = pair.Value;
            affectedTables.Add(pieceTable);
        }

        CustomCategoryVersion++;
        NormalizationStamps.Clear();
        foreach (PieceTable pieceTable in affectedTables)
        {
            if (pieceTable)
            {
                Normalize(pieceTable);
            }
        }
    }

    private static bool ConfiguredOrdersMatch(
        IReadOnlyDictionary<PieceTable, List<ConfiguredCategory>> configuredOrders)
    {
        if (ConfiguredOrders.Count != configuredOrders.Count)
        {
            return false;
        }

        foreach (KeyValuePair<PieceTable, List<ConfiguredCategory>> pair in ConfiguredOrders)
        {
            if (!configuredOrders.TryGetValue(pair.Key, out List<ConfiguredCategory>? replacement) ||
                pair.Value.Count != replacement.Count)
            {
                return false;
            }

            for (int index = 0; index < pair.Value.Count; index++)
            {
                if (!pair.Value[index].Equals(replacement[index]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void ApplyConfiguredOrder(PieceTable pieceTable)
    {
        if (!ConfiguredOrders.TryGetValue(pieceTable, out List<ConfiguredCategory>? configured) ||
            configured.Count == 0 ||
            pieceTable.m_categories == null)
        {
            return;
        }

        List<CategorySlot> current = CaptureCategorySlots(pieceTable);
        bool[] consumed = new bool[current.Count];
        List<CategorySlot> ordered = new(current.Count);
        foreach (ConfiguredCategory desired in configured)
        {
            for (int index = 0; index < current.Count; index++)
            {
                if (consumed[index] || current[index].Category != desired.Category)
                {
                    continue;
                }

                consumed[index] = true;
                CategorySlot slot = current[index];
                ordered.Add(new CategorySlot(slot.Category, desired.Label ?? slot.Label));
                break;
            }
        }

        for (int index = 0; index < current.Count; index++)
        {
            if (!consumed[index])
            {
                ordered.Add(current[index]);
            }
        }

        if (CategorySlotsMatch(current, ordered))
        {
            return;
        }

        pieceTable.m_categories = ordered.Select(static slot => slot.Category).ToList();
        pieceTable.m_categoryLabels = ordered.Select(static slot => slot.Label).ToList();
    }

    private static List<CategorySlot> CaptureCategorySlots(PieceTable pieceTable)
    {
        List<CategorySlot> slots = new();
        if (!pieceTable || pieceTable.m_categories == null)
        {
            return slots;
        }

        List<string>? labels = pieceTable.m_categoryLabels;
        for (int index = 0; index < pieceTable.m_categories.Count; index++)
        {
            Piece.PieceCategory category = pieceTable.m_categories[index];
            string label = labels != null && index < labels.Count && !string.IsNullOrWhiteSpace(labels[index])
                ? labels[index]
                : GetLabel(category);
            slots.Add(new CategorySlot(category, label));
        }

        return slots;
    }

    private static bool CategorySlotsMatch(IReadOnlyList<CategorySlot> left, IReadOnlyList<CategorySlot> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!left[index].Equals(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void MoveOwnerManagedCategoriesToEnd(PieceTable pieceTable)
    {
        if (pieceTable.m_categories == null || pieceTable.m_categories.Count < 2)
        {
            return;
        }

        for (int index = pieceTable.m_categories.Count - 2; index >= 0; index--)
        {
            Piece.PieceCategory category = pieceTable.m_categories[index];
            if (!PieceOverrideManager.IsOwnerManagedHomesteadCategory(category))
            {
                continue;
            }

            string label = pieceTable.m_categoryLabels != null && index < pieceTable.m_categoryLabels.Count
                ? pieceTable.m_categoryLabels[index]
                : GetLabel(category);
            pieceTable.m_categories.RemoveAt(index);
            pieceTable.m_categories.Add(category);
            if (pieceTable.m_categoryLabels != null)
            {
                if (index < pieceTable.m_categoryLabels.Count)
                {
                    pieceTable.m_categoryLabels.RemoveAt(index);
                }

                pieceTable.m_categoryLabels.Add(label);
            }
        }
    }

    private static void RestoreConfiguredOrderBaseline(PieceTable pieceTable)
    {
        if (!ConfiguredOrderBaselines.TryGetValue(pieceTable, out List<CategorySlot>? baseline) ||
            !pieceTable ||
            pieceTable.m_categories == null)
        {
            return;
        }

        List<CategorySlot> current = CaptureCategorySlots(pieceTable);
        bool[] consumed = new bool[current.Count];
        List<CategorySlot> restored = new(current.Count);
        foreach (CategorySlot original in baseline)
        {
            for (int index = 0; index < current.Count; index++)
            {
                if (consumed[index] || current[index].Category != original.Category)
                {
                    continue;
                }

                consumed[index] = true;
                restored.Add(original);
                break;
            }
        }

        for (int index = 0; index < current.Count; index++)
        {
            if (!consumed[index])
            {
                restored.Add(current[index]);
            }
        }

        pieceTable.m_categories = restored.Select(static slot => slot.Category).ToList();
        pieceTable.m_categoryLabels = restored.Select(static slot => slot.Label).ToList();
        NormalizationStamps.Remove(pieceTable);
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

    private static Piece.PieceCategory AllocateCustomCategory()
    {
        HashSet<int> used = new();
        for (int vanillaValue = 0; vanillaValue <= VanillaCategorySlots; vanillaValue++)
        {
            used.Add(vanillaValue);
        }

        used.Add((int)Piece.PieceCategory.All);
        used.UnionWith(KnownCategoriesByName.Values.Select(static category => (int)category));
        used.UnionWith(KnownCategoryNames.Keys.Select(static category => (int)category));
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
        while (used.Contains(value) || value == (int)Piece.PieceCategory.All)
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

        CaptureCategorySnapshotIfNeeded(pieceTable);
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
        NormalizationStamps.Remove(pieceTable);
        Normalize(pieceTable);
    }

    internal static void PruneUnusedCustomCategories()
    {
        if (CustomCategoryNames.Count == 0)
        {
            return;
        }

        foreach (PieceTable pieceTable in Resources.FindObjectsOfTypeAll<PieceTable>())
        {
            if (!pieceTable || pieceTable.m_categories == null)
            {
                continue;
            }

            bool changed = false;
            for (int index = pieceTable.m_categories.Count - 1; index >= 0; index--)
            {
                Piece.PieceCategory category = pieceTable.m_categories[index];
                if (!CustomCategoryNames.ContainsKey(category) || IsCategoryUsed(pieceTable, category))
                {
                    continue;
                }

                CaptureCategorySnapshotIfNeeded(pieceTable);
                pieceTable.m_categories.RemoveAt(index);
                if (pieceTable.m_categoryLabels != null && index < pieceTable.m_categoryLabels.Count)
                {
                    pieceTable.m_categoryLabels.RemoveAt(index);
                }

                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            NormalizationStamps.Remove(pieceTable);
            Normalize(pieceTable);
        }
    }

    internal static void RestoreTemporarilyPrunedCategories()
    {
        foreach (KeyValuePair<PieceTable, List<RemovedCategorySlot>> pair in TemporarilyPrunedCategories.ToArray())
        {
            PieceTable pieceTable = pair.Key;
            if (!pieceTable)
            {
                continue;
            }

            pieceTable.m_categories ??= new List<Piece.PieceCategory>();
            pieceTable.m_categoryLabels ??= new List<string>();
            foreach (RemovedCategorySlot slot in pair.Value.OrderBy(static slot => slot.Index))
            {
                if (pieceTable.m_categories.Contains(slot.Category))
                {
                    continue;
                }

                int index = Math.Max(0, Math.Min(slot.Index, pieceTable.m_categories.Count));
                pieceTable.m_categories.Insert(index, slot.Category);
                int labelIndex = Math.Max(0, Math.Min(index, pieceTable.m_categoryLabels.Count));
                pieceTable.m_categoryLabels.Insert(labelIndex, slot.Label);
            }

            NormalizationStamps.Remove(pieceTable);
        }

        TemporarilyPrunedCategories.Clear();
    }

    internal static void PruneCategoryIfUnused(PieceTable? pieceTable, Piece.PieceCategory category)
    {
        RemoveCategoryIfUnused(pieceTable, category, rememberForCurrentApply: true);
    }

    internal static bool RemoveOwnedCategoryIfUnused(PieceTable? pieceTable, Piece.PieceCategory category)
    {
        return RemoveCategoryIfUnused(pieceTable, category, rememberForCurrentApply: false);
    }

    private static bool RemoveCategoryIfUnused(
        PieceTable? pieceTable,
        Piece.PieceCategory category,
        bool rememberForCurrentApply)
    {
        if (!pieceTable || pieceTable.m_categories == null)
        {
            return true;
        }

        if (IsCategoryUsed(pieceTable, category))
        {
            return false;
        }

        if (!pieceTable.m_categories.Contains(category))
        {
            return true;
        }

        CaptureCategorySnapshotIfNeeded(pieceTable);
        pieceTable.m_categoryLabels ??= new List<string>();
        bool changed = false;
        for (int index = pieceTable.m_categories.Count - 1; index >= 0; index--)
        {
            if (pieceTable.m_categories[index] != category)
            {
                continue;
            }

            string label = index < pieceTable.m_categoryLabels.Count
                ? pieceTable.m_categoryLabels[index]
                : GetLabel(category);
            if (rememberForCurrentApply)
            {
                if (!TemporarilyPrunedCategories.TryGetValue(pieceTable, out List<RemovedCategorySlot>? slots))
                {
                    slots = new List<RemovedCategorySlot>();
                    TemporarilyPrunedCategories[pieceTable] = slots;
                }

                slots.Add(new RemovedCategorySlot(index, category, label));
            }

            pieceTable.m_categories.RemoveAt(index);
            if (index < pieceTable.m_categoryLabels.Count)
            {
                pieceTable.m_categoryLabels.RemoveAt(index);
            }

            changed = true;
        }

        if (changed)
        {
            NormalizationStamps.Remove(pieceTable);
            Normalize(pieceTable);
        }

        return true;
    }

    private static bool IsCategoryUsed(PieceTable pieceTable, Piece.PieceCategory category)
    {
        if (pieceTable.m_pieces == null)
        {
            return false;
        }

        foreach (GameObject piecePrefab in pieceTable.m_pieces)
        {
            Piece? piece = piecePrefab ? piecePrefab.GetComponent<Piece>() : null;
            if (piece != null && piece.m_category == category)
            {
                return true;
            }
        }

        return false;
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

                categoryHash = unchecked(categoryHash * 31 + StringComparer.Ordinal.GetHashCode(label.Trim()));
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
        if (CustomCategoryNames.TryGetValue(category, out string customName))
        {
            return customName;
        }

        if (KnownCategoryNames.TryGetValue(category, out string knownName))
        {
            return knownName;
        }

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

    private static bool ShouldReplaceLabel(Piece.PieceCategory category, string? currentLabel, string expectedLabel)
    {
        string current = currentLabel?.Trim() ?? "";
        if (current.Length == 0)
        {
            return true;
        }

        if (LabelsMatch(current, expectedLabel))
        {
            return false;
        }

        if (IsNumericLabel(current) && !IsNumericLabel(expectedLabel))
        {
            return true;
        }

        bool hasAuthoritativeName =
            CustomCategoryNames.ContainsKey(category) ||
            KnownCategoryNames.ContainsKey(category);
        if (!hasAuthoritativeName || (int)category < VanillaCategorySlots)
        {
            return false;
        }

        if (TryResolveLabel(current, out Piece.PieceCategory labelledCategory))
        {
            return labelledCategory != category;
        }

        return true;
    }

    private static bool TryResolveLabel(string label, out Piece.PieceCategory category)
    {
        string comparable = GetComparableLabel(label);
        if (KnownCategoriesByName.TryGetValue(comparable, out category) ||
            CustomCategoriesByName.TryGetValue(comparable, out category))
        {
            return true;
        }

        category = Piece.PieceCategory.Misc;
        return false;
    }

    private static bool LabelsMatch(string left, string right)
    {
        return GetComparableLabel(left).Equals(GetComparableLabel(right), StringComparison.Ordinal);
    }

    private static string GetComparableLabel(string label)
    {
        string trimmed = label.Trim();
        if (!trimmed.StartsWith("$", StringComparison.Ordinal) || Localization.instance == null)
        {
            return trimmed.TrimStart('$');
        }

        string localized = Localization.instance.Localize(trimmed).Trim();
        return localized.Length > 0 && !localized.Equals(trimmed, StringComparison.Ordinal)
            ? localized
            : trimmed.TrimStart('$');
    }

    private static bool IsNumericLabel(string label)
    {
        return int.TryParse(label.Trim(), out _);
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
            CreatedHudCategoryTabs.Add(tab);
            tabs.Add(tab);
        }

        hud.m_pieceCategoryTabs = tabs.ToArray();
    }

    private static void CaptureCategorySnapshotIfNeeded(PieceTable pieceTable)
    {
        if (CategorySnapshots.ContainsKey(pieceTable))
        {
            return;
        }

        CategorySnapshots[pieceTable] = PieceTableCategorySnapshot.From(pieceTable);
    }

    private static void RemoveCreatedHudCategoryTabs()
    {
        Hud hud = Hud.instance;
        if (hud && hud.m_pieceCategoryTabs != null)
        {
            hud.m_pieceCategoryTabs = hud.m_pieceCategoryTabs
                .Where(tab => tab && !CreatedHudCategoryTabs.Contains(tab))
                .ToArray();
        }

        foreach (GameObject tab in CreatedHudCategoryTabs.ToArray())
        {
            if (tab)
            {
                UnityEngine.Object.Destroy(tab);
            }
        }

        CreatedHudCategoryTabs.Clear();
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

    internal readonly struct ConfiguredCategory : IEquatable<ConfiguredCategory>
    {
        internal ConfiguredCategory(Piece.PieceCategory category, string? label)
        {
            Category = category;
            string normalizedLabel = label?.Trim() ?? "";
            Label = normalizedLabel.Length > 0 ? normalizedLabel : null;
        }

        internal Piece.PieceCategory Category { get; }
        internal string? Label { get; }

        public bool Equals(ConfiguredCategory other)
        {
            return Category == other.Category && string.Equals(Label, other.Label, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is ConfiguredCategory other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Category * 397) ^ (Label != null ? StringComparer.Ordinal.GetHashCode(Label) : 0);
            }
        }
    }

    private readonly struct CategorySlot : IEquatable<CategorySlot>
    {
        internal CategorySlot(Piece.PieceCategory category, string label)
        {
            Category = category;
            Label = label ?? "";
        }

        internal Piece.PieceCategory Category { get; }
        internal string Label { get; }

        public bool Equals(CategorySlot other)
        {
            return Category == other.Category && string.Equals(Label, other.Label, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is CategorySlot other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Category * 397) ^ StringComparer.Ordinal.GetHashCode(Label);
            }
        }
    }

    private readonly struct RemovedCategorySlot
    {
        internal RemovedCategorySlot(int index, Piece.PieceCategory category, string label)
        {
            Index = index;
            Category = category;
            Label = label ?? "";
        }

        internal int Index { get; }
        internal Piece.PieceCategory Category { get; }
        internal string Label { get; }
    }

    private sealed class PieceTableCategorySnapshot
    {
        private readonly List<Piece.PieceCategory> _categories;
        private readonly List<string> _labels;
        private readonly int _availablePieceSlots;
        private readonly int _selectedPieceSlots;
        private readonly int _lastSelectedPieceSlots;
        private readonly Piece.PieceCategory? _selectedCategory;

        private PieceTableCategorySnapshot(
            List<Piece.PieceCategory> categories,
            List<string> labels,
            int availablePieceSlots,
            int selectedPieceSlots,
            int lastSelectedPieceSlots,
            Piece.PieceCategory? selectedCategory)
        {
            _categories = categories;
            _labels = labels;
            _availablePieceSlots = availablePieceSlots;
            _selectedPieceSlots = selectedPieceSlots;
            _lastSelectedPieceSlots = lastSelectedPieceSlots;
            _selectedCategory = selectedCategory;
        }

        internal static PieceTableCategorySnapshot From(PieceTable pieceTable)
        {
            int availablePieceSlots = AvailablePiecesField?.GetValue(pieceTable) is ICollection availablePieces
                ? availablePieces.Count
                : -1;
            Piece.PieceCategory? selectedCategory = SelectedCategoryField?.GetValue(pieceTable) is Piece.PieceCategory category
                ? category
                : null;
            return new PieceTableCategorySnapshot(
                pieceTable.m_categories?.ToList() ?? new List<Piece.PieceCategory>(),
                pieceTable.m_categoryLabels?.ToList() ?? new List<string>(),
                availablePieceSlots,
                pieceTable.m_selectedPiece?.Length ?? 0,
                pieceTable.m_lastSelectedPiece?.Length ?? 0,
                selectedCategory);
        }

        internal void Restore(PieceTable pieceTable)
        {
            pieceTable.m_categories = _categories.ToList();
            pieceTable.m_categoryLabels = _labels.ToList();
            if (_availablePieceSlots >= 0 &&
                AvailablePiecesField?.GetValue(pieceTable) is IList availablePieces &&
                !availablePieces.IsFixedSize)
            {
                while (availablePieces.Count > _availablePieceSlots)
                {
                    availablePieces.RemoveAt(availablePieces.Count - 1);
                }
            }

            Array.Resize(ref pieceTable.m_selectedPiece, _selectedPieceSlots);
            Array.Resize(ref pieceTable.m_lastSelectedPiece, _lastSelectedPieceSlots);
            if (_selectedCategory.HasValue)
            {
                SelectedCategoryField?.SetValue(pieceTable, _selectedCategory.Value);
            }
        }
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

[HarmonyPatch(typeof(Hud), nameof(Hud.UpdateBuild))]
internal static class DataForgeHudUpdateBuildPieceTableCategoryGuardPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Player player)
    {
        PieceTableCategoryGuard.Normalize(player ? player.m_buildPieces : null);
    }
}

[HarmonyPatch(typeof(Hud), nameof(Hud.UpdateBuild))]
internal static class DataForgeHudUpdateBuildPieceTableCategoryRepairPatch
{
    [HarmonyPriority(Priority.Last)]
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
