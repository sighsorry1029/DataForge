using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace DataForge;

[HarmonyPatch(typeof(StationExtension), nameof(StationExtension.OtherExtensionInRange))]
internal static class DataForgeStationExtensionSpacingBypassPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!DataForgePlugin.IgnoreStationExtensionSpacing)
        {
            return true;
        }

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(StationExtension), nameof(StationExtension.StartConnectionEffect), typeof(Vector3), typeof(float))]
internal static class DataForgeStationExtensionConnectionEffectPatch
{
    private static bool Prefix(StationExtension __instance)
    {
        return __instance.m_connectionPrefab != null;
    }
}

[HarmonyPatch(typeof(StationExtension), nameof(StationExtension.FindExtensions))]
internal static class DataForgeStationExtensionFindExtensionsPatch
{
    private static bool Prefix(CraftingStation station, Vector3 pos, List<StationExtension> extensions)
    {
        if (!DataForgePlugin.IgnoreStationExtensionSpacing)
        {
            return true;
        }

        if (station == null || extensions == null)
        {
            return false;
        }

        for (int index = StationExtension.m_allExtensions.Count - 1; index >= 0; index--)
        {
            StationExtension extension = StationExtension.m_allExtensions[index];
            if (extension == null || extension.m_craftingStation == null || extension.m_piece == null)
            {
                StationExtension.m_allExtensions.RemoveAt(index);
                continue;
            }

            float maxDistance = extension.m_maxStationDistance;
            if ((extension.transform.position - pos).sqrMagnitude < maxDistance * maxDistance &&
                extension.m_craftingStation.m_name == station.m_name &&
                (extension.m_stack || !StationExtension.ExtensionInList(extensions, extension)))
            {
                extensions.Add(extension);
            }
        }

        return false;
    }
}

[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.GetExtensions))]
internal static class DataForgeCraftingStationGetExtensionsPatch
{
    private static bool Prefix(CraftingStation __instance, ref List<StationExtension> __result)
    {
        if (!DataForgePlugin.IgnoreStationExtensionSpacing)
        {
            return true;
        }

        if (__instance.m_attachedExtensions == null)
        {
            __instance.m_attachedExtensions = new List<StationExtension>();
        }

        if (__instance.m_updateExtensionTimer >= CraftingStation.m_updateExtensionInterval)
        {
            __instance.m_updateExtensionTimer = 0f;
            __instance.m_attachedExtensions.Clear();
            AddValidExtensions(__instance, __instance.transform.position, __instance.m_attachedExtensions);
            __instance.m_buildRange = __instance.m_rangeBuild + __instance.m_attachedExtensions.Count * __instance.m_extraRangePerLevel;
            UpdateAreaMarker(__instance);
        }

        __result = __instance.m_attachedExtensions;
        return false;
    }

    private static void AddValidExtensions(CraftingStation station, Vector3 position, List<StationExtension> extensions)
    {
        for (int index = StationExtension.m_allExtensions.Count - 1; index >= 0; index--)
        {
            StationExtension extension = StationExtension.m_allExtensions[index];
            if (extension == null || extension.m_craftingStation == null || extension.m_piece == null)
            {
                StationExtension.m_allExtensions.RemoveAt(index);
                continue;
            }

            float maxDistance = extension.m_maxStationDistance;
            if ((extension.transform.position - position).sqrMagnitude < maxDistance * maxDistance &&
                extension.m_craftingStation.m_name == station.m_name &&
                (extension.m_stack || !StationExtension.ExtensionInList(extensions, extension)))
            {
                extensions.Add(extension);
            }
        }
    }

    private static void UpdateAreaMarker(CraftingStation station)
    {
        if (station.m_areaMarker == null)
        {
            return;
        }

        if (station.m_areaMarkerCircle == null)
        {
            station.m_areaMarkerCircle = station.m_areaMarker.GetComponent<CircleProjector>();
        }

        if (station.m_areaMarkerCircle != null)
        {
            station.m_areaMarkerCircle.m_radius = station.m_buildRange;
        }
    }

}
