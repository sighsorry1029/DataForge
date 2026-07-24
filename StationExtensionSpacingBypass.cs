using HarmonyLib;
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
