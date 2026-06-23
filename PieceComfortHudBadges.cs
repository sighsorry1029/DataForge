using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DataForge;

internal static class PieceComfortHudBadges
{
    private const string BadgeObjectName = "DataForgeComfortBadge";
    private const string GroupHighlightObjectName = "DataForgeComfortGroupHighlight";
    private static readonly Color BadgeColor = new(1f, 0.42f, 0f, 1f);
    private static readonly Color GroupHighlightColor = new(1f, 0.42f, 0f, 0.3f);
    private static bool AnyBadgeVisible;
    private static bool AnyGroupHighlightVisible;

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

        if (!DataForgePlugin.ShowPieceComfortInHammer)
        {
            HideVisibleBadges(hud);
            HideVisibleGroupHighlights(hud);
            return;
        }

        List<Piece> buildPieces = player.GetBuildPieces();
        bool anyVisible = false;

        for (int index = 0; index < hud.m_pieceIcons.Count; index++)
        {
            Hud.PieceIconData iconData = hud.m_pieceIcons[index];
            int comfort = index < buildPieces.Count && buildPieces[index] != null
                ? buildPieces[index].m_comfort
                : 0;

            if (comfort <= 0)
            {
                HideBadge(iconData);
                continue;
            }

            TMP_Text badge = GetOrCreateBadge(hud, iconData);
            if (badge == null)
            {
                continue;
            }

            string text = comfort.ToString(CultureInfo.InvariantCulture);
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
        RefreshGroupHighlights(hud, buildPieces);
    }

    private static void RefreshGroupHighlights(Hud hud, List<Piece> buildPieces)
    {
        Piece hoveredPiece = hud.m_hoveredPiece;
        if (hoveredPiece == null ||
            hoveredPiece.m_comfort <= 0 ||
            hoveredPiece.m_comfortGroup == Piece.ComfortGroup.None)
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
                piece.m_comfortGroup == hoveredGroup;

            if (!shouldHighlight)
            {
                HideGroupHighlight(iconData);
                continue;
            }

            Image highlight = GetOrCreateGroupHighlight(iconData);
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
            HideGroupHighlight(iconData);
        }

        AnyGroupHighlightVisible = false;
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

    private static Image GetOrCreateGroupHighlight(Hud.PieceIconData iconData)
    {
        if (iconData?.m_go == null)
        {
            return null!;
        }

        Transform existing = iconData.m_go.transform.Find(GroupHighlightObjectName);
        if (existing != null && existing.TryGetComponent(out Image existingImage))
        {
            existingImage.color = GroupHighlightColor;
            existingImage.raycastTarget = false;
            return existingImage;
        }

        GameObject highlightObject = new(GroupHighlightObjectName);
        highlightObject.SetActive(false);
        highlightObject.transform.SetParent(iconData.m_go.transform, worldPositionStays: false);

        RectTransform rect = highlightObject.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        Image image = highlightObject.AddComponent<Image>();
        image.color = GroupHighlightColor;
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

    private static void HideGroupHighlight(Hud.PieceIconData iconData)
    {
        if (iconData?.m_go == null)
        {
            return;
        }

        Transform existing = iconData.m_go.transform.Find(GroupHighlightObjectName);
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

[HarmonyPatch(typeof(Hud), nameof(Hud.UpdatePieceList))]
internal static class DataForgeHudUpdatePieceListComfortBadgePatch
{
    private static void Postfix(Hud __instance, Player player)
    {
        PieceComfortHudBadges.Refresh(__instance, player);
    }
}
