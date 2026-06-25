using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataForge;

internal static class PieceComfortHudBadges
{
    private const string BadgeObjectName = "DataForgeComfortBadge";
    private const string GroupHighlightObjectName = "DataForgeComfortGroupHighlight";
    private const string StationExtensionHighlightObjectName = "DataForgeStationExtensionHighlight";
    private static readonly Color BadgeColor = new(1f, 0.42f, 0f, 1f);
    private static readonly Color GroupHighlightColor = new(1f, 0.42f, 0f, 0.3f);
    private static readonly Color StationExtensionHighlightColor = new(0.25f, 0.95f, 1f, 0.26f);
    private static bool AnyBadgeVisible;
    private static bool AnyGroupHighlightVisible;
    private static bool AnyStationExtensionHighlightVisible;

    internal static void RefreshVisibleHud()
    {
        Hud hud = Hud.m_instance;
        Player player = Player.m_localPlayer;
        if (hud == null || player == null || !hud.m_pieceSelectionWindow.activeSelf)
        {
            return;
        }

        Refresh(hud, player);
    }

    internal static void Refresh(Hud hud, Player player)
    {
        if (hud == null || player == null || hud.m_pieceIcons == null)
        {
            return;
        }

        if (!DataForgePlugin.ShowPieceComfortInHammer &&
            !DataForgePlugin.HighlightStationExtensionsInHammer)
        {
            HideVisibleBadges(hud);
            HideVisibleGroupHighlights(hud);
            HideVisibleStationExtensionHighlights(hud);
            return;
        }

        List<Piece> buildPieces = player.GetBuildPieces();
        if (DataForgePlugin.ShowPieceComfortInHammer)
        {
            RefreshComfortBadges(hud, player, buildPieces);
            RefreshGroupHighlights(hud, player, buildPieces);
        }
        else
        {
            HideVisibleBadges(hud);
            HideVisibleGroupHighlights(hud);
        }

        if (DataForgePlugin.HighlightStationExtensionsInHammer)
        {
            RefreshStationExtensionHighlights(hud, player, buildPieces);
        }
        else
        {
            HideVisibleStationExtensionHighlights(hud);
        }
    }

    private static void RefreshComfortBadges(Hud hud, Player player, List<Piece> buildPieces)
    {
        bool anyVisible = false;
        for (int index = 0; index < hud.m_pieceIcons.Count; index++)
        {
            Hud.PieceIconData iconData = hud.m_pieceIcons[index];
            Piece piece = index < buildPieces.Count ? buildPieces[index] : null!;

            if (piece == null || piece.m_comfort <= 0 || VeiledRecipesSoftCompat.ShouldMaskPiece(player, piece))
            {
                HideBadge(iconData);
                continue;
            }

            TMP_Text badge = GetOrCreateBadge(hud, iconData);
            if (badge == null)
            {
                continue;
            }

            string text = piece.m_comfort.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(badge.text, text, System.StringComparison.Ordinal))
            {
                badge.text = text;
            }

            if (!badge.gameObject.activeSelf)
            {
                badge.gameObject.SetActive(true);
            }

            badge.transform.SetAsLastSibling();
            anyVisible = true;
        }

        AnyBadgeVisible = anyVisible;
    }

    private static void RefreshGroupHighlights(Hud hud, Player player, List<Piece> buildPieces)
    {
        Piece hoveredPiece = hud.m_hoveredPiece;
        if (hoveredPiece == null ||
            hoveredPiece.m_comfort <= 0 ||
            hoveredPiece.m_comfortGroup == Piece.ComfortGroup.None ||
            VeiledRecipesSoftCompat.ShouldMaskPiece(player, hoveredPiece))
        {
            HideVisibleGroupHighlights(hud);
            return;
        }

        Piece.ComfortGroup hoveredGroup = hoveredPiece.m_comfortGroup;
        bool anyVisible = false;
        for (int index = 0; index < hud.m_pieceIcons.Count; index++)
        {
            Hud.PieceIconData iconData = hud.m_pieceIcons[index];
            Piece piece = index < buildPieces.Count ? buildPieces[index] : null!;
            bool shouldHighlight = piece != null &&
                piece.m_comfort > 0 &&
                piece.m_comfortGroup == hoveredGroup &&
                !VeiledRecipesSoftCompat.ShouldMaskPiece(player, piece);

            if (!shouldHighlight)
            {
                HideHighlight(iconData, GroupHighlightObjectName);
                continue;
            }

            Image highlight = GetOrCreateHighlight(iconData, GroupHighlightObjectName, GroupHighlightColor);
            if (highlight == null)
            {
                continue;
            }

            if (!highlight.gameObject.activeSelf)
            {
                highlight.gameObject.SetActive(true);
            }

            highlight.transform.SetAsFirstSibling();
            anyVisible = true;
        }

        AnyGroupHighlightVisible = anyVisible;
    }

    private static void RefreshStationExtensionHighlights(Hud hud, Player player, List<Piece> buildPieces)
    {
        Piece hoveredPiece = hud.m_hoveredPiece;
        if (hoveredPiece == null ||
            VeiledRecipesSoftCompat.ShouldMaskPiece(player, hoveredPiece) ||
            !TryGetRelatedCraftingStation(hoveredPiece, out CraftingStation targetStation))
        {
            HideVisibleStationExtensionHighlights(hud);
            return;
        }

        bool anyVisible = false;
        for (int index = 0; index < hud.m_pieceIcons.Count; index++)
        {
            Hud.PieceIconData iconData = hud.m_pieceIcons[index];
            Piece piece = index < buildPieces.Count ? buildPieces[index] : null!;
            bool shouldHighlight = piece != null &&
                !VeiledRecipesSoftCompat.ShouldMaskPiece(player, piece) &&
                IsRelatedToCraftingStation(piece, targetStation);

            if (!shouldHighlight)
            {
                HideHighlight(iconData, StationExtensionHighlightObjectName);
                continue;
            }

            Image highlight = GetOrCreateHighlight(iconData, StationExtensionHighlightObjectName, StationExtensionHighlightColor);
            if (highlight == null)
            {
                continue;
            }

            if (!highlight.gameObject.activeSelf)
            {
                highlight.gameObject.SetActive(true);
            }

            highlight.transform.SetAsFirstSibling();
            anyVisible = true;
        }

        AnyStationExtensionHighlightVisible = anyVisible;
    }

    private static bool TryGetRelatedCraftingStation(Piece piece, out CraftingStation station)
    {
        station = null!;
        if (piece == null)
        {
            return false;
        }

        CraftingStation ownStation = piece.GetComponent<CraftingStation>();
        if (ownStation != null)
        {
            station = ownStation;
            return true;
        }

        StationExtension extension = piece.GetComponent<StationExtension>();
        if (extension?.m_craftingStation != null)
        {
            station = extension.m_craftingStation;
            return true;
        }

        return false;
    }

    private static bool IsRelatedToCraftingStation(Piece piece, CraftingStation targetStation)
    {
        if (piece == null || targetStation == null)
        {
            return false;
        }

        CraftingStation ownStation = piece.GetComponent<CraftingStation>();
        if (IsSameCraftingStation(ownStation, targetStation))
        {
            return true;
        }

        StationExtension extension = piece.GetComponent<StationExtension>();
        return IsSameCraftingStation(extension?.m_craftingStation, targetStation);
    }

    private static bool IsSameCraftingStation(CraftingStation? left, CraftingStation? right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        if (ReferenceEquals(left, right))
        {
            return true;
        }

        string leftPrefab = Utils.GetPrefabName(left.gameObject);
        string rightPrefab = Utils.GetPrefabName(right.gameObject);
        if (!string.IsNullOrWhiteSpace(leftPrefab) &&
            !string.IsNullOrWhiteSpace(rightPrefab) &&
            string.Equals(leftPrefab, rightPrefab, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(left.m_name) &&
               !string.IsNullOrWhiteSpace(right.m_name) &&
               string.Equals(left.m_name, right.m_name, StringComparison.OrdinalIgnoreCase);
    }

    private static TMP_Text GetOrCreateBadge(Hud hud, Hud.PieceIconData iconData)
    {
        if (iconData?.m_go == null)
        {
            return null!;
        }

        Transform existing = iconData.m_go.transform.Find(BadgeObjectName);
        if (existing != null && existing.TryGetComponent(out TMP_Text existingText))
        {
            if (existingText.font == null)
            {
                TMP_Text? existingTemplate = FindTemplateText(hud, iconData);
                if (existingTemplate == null || existingTemplate.font == null)
                {
                    UnityEngine.Object.Destroy(existing.gameObject);
                    return null!;
                }

                ConfigureBadgeText(existingText, existingTemplate);
            }

            return existingText;
        }

        TMP_Text? template = FindTemplateText(hud, iconData);
        if (template == null || template.font == null)
        {
            return null!;
        }

        GameObject badgeObject = new(BadgeObjectName);
        badgeObject.SetActive(false);
        badgeObject.transform.SetParent(iconData.m_go.transform, worldPositionStays: false);
        RectTransform rect = badgeObject.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.one;
        rect.anchorMax = Vector2.one;
        rect.pivot = Vector2.one;
        rect.anchoredPosition = new Vector2(-8f, -2f);
        rect.sizeDelta = new Vector2(32f, 24f);

        TextMeshProUGUI text = badgeObject.AddComponent<TextMeshProUGUI>();
        ConfigureBadgeText(text, template);
        return text;
    }

    private static void HideVisibleBadges(Hud hud)
    {
        if (!AnyBadgeVisible || hud.m_pieceIcons == null)
        {
            return;
        }

        foreach (Hud.PieceIconData iconData in hud.m_pieceIcons)
        {
            HideBadge(iconData);
        }

        AnyBadgeVisible = false;
    }

    private static void HideVisibleGroupHighlights(Hud hud)
    {
        if (!AnyGroupHighlightVisible || hud.m_pieceIcons == null)
        {
            return;
        }

        foreach (Hud.PieceIconData iconData in hud.m_pieceIcons)
        {
            HideHighlight(iconData, GroupHighlightObjectName);
        }

        AnyGroupHighlightVisible = false;
    }

    private static void HideVisibleStationExtensionHighlights(Hud hud)
    {
        if (!AnyStationExtensionHighlightVisible || hud.m_pieceIcons == null)
        {
            return;
        }

        foreach (Hud.PieceIconData iconData in hud.m_pieceIcons)
        {
            HideHighlight(iconData, StationExtensionHighlightObjectName);
        }

        AnyStationExtensionHighlightVisible = false;
    }

    private static void HideBadge(Hud.PieceIconData iconData)
    {
        if (iconData?.m_go == null)
        {
            return;
        }

        Transform existing = iconData.m_go.transform.Find(BadgeObjectName);
        if (existing != null && existing.gameObject.activeSelf)
        {
            existing.gameObject.SetActive(false);
        }
    }

    private static Image GetOrCreateHighlight(Hud.PieceIconData iconData, string objectName, Color color)
    {
        if (iconData?.m_go == null)
        {
            return null!;
        }

        Transform existing = iconData.m_go.transform.Find(objectName);
        if (existing != null && existing.TryGetComponent(out Image existingImage))
        {
            existingImage.color = color;
            existingImage.raycastTarget = false;
            return existingImage;
        }

        GameObject highlightObject = new(objectName);
        highlightObject.SetActive(false);
        highlightObject.transform.SetParent(iconData.m_go.transform, worldPositionStays: false);

        RectTransform rect = highlightObject.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        Image image = highlightObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        if (iconData.m_go.TryGetComponent(out Image background) && background.sprite != null)
        {
            image.sprite = background.sprite;
            image.type = background.type;
            image.pixelsPerUnitMultiplier = background.pixelsPerUnitMultiplier;
        }

        highlightObject.transform.SetAsFirstSibling();
        return image;
    }

    private static void HideHighlight(Hud.PieceIconData iconData, string objectName)
    {
        if (iconData?.m_go == null)
        {
            return;
        }

        Transform existing = iconData.m_go.transform.Find(objectName);
        if (existing != null && existing.gameObject.activeSelf)
        {
            existing.gameObject.SetActive(false);
        }
    }

    private static void ConfigureBadgeText(TMP_Text text, TMP_Text template)
    {
        if (template == null || template.font == null)
        {
            return;
        }

        text.font = template.font;
        if (template.fontSharedMaterial != null)
        {
            text.fontSharedMaterial = template.fontSharedMaterial;
        }
        else if (template.font.material != null)
        {
            text.fontSharedMaterial = template.font.material;
        }

        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.alignment = TextAlignmentOptions.TopRight;
        text.fontSize = 20f;
        text.fontStyle = FontStyles.Bold;
        text.color = BadgeColor;
        ConfigureShadow(text);

        text.text = "";
    }

    private static void ConfigureShadow(TMP_Text text)
    {
        if (!text.TryGetComponent(out Shadow shadow))
        {
            shadow = text.gameObject.AddComponent<Shadow>();
        }

        shadow.effectColor = new Color(0f, 0f, 0f, 0.9f);
        shadow.effectDistance = new Vector2(1.5f, -1.5f);
        shadow.useGraphicAlpha = true;
    }

    private static TMP_Text FindTemplateText(Hud hud, Hud.PieceIconData iconData)
    {
        if (hud != null)
        {
            if (hud.m_buildSelection != null && hud.m_buildSelection.font != null)
            {
                return hud.m_buildSelection;
            }

            if (hud.m_pieceDescription != null && hud.m_pieceDescription.font != null)
            {
                return hud.m_pieceDescription;
            }

            TMP_Text hudText = FindFirstUsableText(hud.transform);
            if (hudText != null)
            {
                return hudText;
            }
        }

        return iconData?.m_go != null
            ? FindFirstUsableText(iconData.m_go.transform)
            : null!;
    }

    private static TMP_Text FindFirstUsableText(Transform root)
    {
        if (root == null)
        {
            return null!;
        }

        foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(includeInactive: true))
        {
            if (text != null &&
                text.font != null &&
                text.gameObject.name != BadgeObjectName)
            {
                return text;
            }
        }

        return null!;
    }
}

internal static class VeiledRecipesSoftCompat
{
    private const string CompatTypeName = "VeiledRecipes.VeiledRecipesCompat";
    private const string CompatAssemblyQualifiedName = CompatTypeName + ", VeiledRecipes";
    private static bool Initialized;
    private static bool LoggedFailure;
    private static ShouldMaskPieceDelegate? ShouldMaskPieceMethod;

    private delegate bool ShouldMaskPieceDelegate(Player player, Piece piece);

    internal static bool ShouldMaskPiece(Player player, Piece piece)
    {
        if (player == null || piece == null)
        {
            return false;
        }

        EnsureInitialized();
        if (ShouldMaskPieceMethod == null)
        {
            return false;
        }

        try
        {
            return ShouldMaskPieceMethod(player, piece);
        }
        catch (Exception ex)
        {
            if (!LoggedFailure)
            {
                LoggedFailure = true;
                DataForgePlugin.Log.LogDebug($"VeiledRecipes piece mask check failed: {ex.Message}");
            }

            return false;
        }
    }

    private static void EnsureInitialized()
    {
        if (Initialized)
        {
            return;
        }

        Initialized = true;
        Type? compatType = Type.GetType(CompatAssemblyQualifiedName, throwOnError: false);
        if (compatType == null)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                compatType = assembly.GetType(CompatTypeName, throwOnError: false);
                if (compatType != null)
                {
                    break;
                }
            }
        }

        MethodInfo? method = compatType?.GetMethod(
            "ShouldMaskPiece",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(Player), typeof(Piece) },
            modifiers: null);

        if (method != null)
        {
            ShouldMaskPieceMethod = Delegate.CreateDelegate(
                typeof(ShouldMaskPieceDelegate),
                method,
                throwOnBindFailure: false) as ShouldMaskPieceDelegate;
        }
    }
}

[HarmonyPatch(typeof(Hud), nameof(Hud.UpdatePieceList))]
internal static class DataForgeHudUpdatePieceListComfortBadgePatch
{
    private static void Postfix(Hud __instance, Player player)
    {
        PieceComfortHudBadges.Refresh(__instance, player);
    }
}
