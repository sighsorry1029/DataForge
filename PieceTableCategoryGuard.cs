using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DataForge;

internal static class PieceTableCategoryGuard
{
    private static readonly Dictionary<Piece.PieceCategory, string> CategoryLabels = new();
    private static readonly FieldInfo? SelectedCategoryField = AccessTools.Field(typeof(PieceTable), "m_selectedCategory");
    private static readonly FieldInfo? AvailablePiecesField = AccessTools.Field(typeof(PieceTable), "m_availablePieces");

    internal static void Normalize(PieceTable? pieceTable)
    {
        if (!pieceTable || pieceTable.m_categories == null)
        {
            return;
        }

        RemoveInvalidPieces(pieceTable);
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
    }

    internal static void EnsureSelectedCategory(PieceTable? pieceTable)
    {
        if (!pieceTable || pieceTable.m_categories == null)
        {
            return;
        }

        NormalizeSelectedCategory(pieceTable);
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
        if (!pieceTable || !IsSelectableCategory(pieceTable, category))
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

        return Math.Max(0, (int)Piece.PieceCategory.Max);
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
