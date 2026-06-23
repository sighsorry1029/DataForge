using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DataForge;

internal static class ArcheryTargetGuard
{
    private static readonly HashSet<int> LoggedInstances = new();

    internal static bool HasRequiredNetworkView(ArcheryTarget target)
    {
        if (target == null)
        {
            return true;
        }

        return target.GetComponentInParent<ZNetView>() != null;
    }

    internal static void LogMissingNetworkViewOnce(ArcheryTarget target)
    {
        if (target == null || !LoggedInstances.Add(target.GetInstanceID()))
        {
            return;
        }

        GameObject gameObject = target.gameObject;
        DataForgePlugin.Log.LogWarning(
            "ArcheryTarget.Start skipped because the target has no parent ZNetView. " +
            "This usually means a prefab/preview/clone object was activated outside the normal ZNetScene placement path. " +
            DescribeObject(gameObject) + " " +
            DescribePrefabRegistration(gameObject));
    }

    private static string DescribeObject(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return "object=<null>.";
        }

        Transform root = gameObject.transform.root;
        return
            $"object='{BuildPath(gameObject.transform)}', " +
            $"root='{BuildPath(root)}', " +
            $"scene='{(gameObject.scene.IsValid() ? gameObject.scene.name : "<no scene>")}', " +
            $"activeSelf={gameObject.activeSelf}, activeInHierarchy={gameObject.activeInHierarchy}, " +
            $"rootHasZNetView={root.GetComponent<ZNetView>() != null}, " +
            $"rootHasPiece={root.GetComponent<Piece>() != null}, " +
            $"rootHasWearNTear={root.GetComponent<WearNTear>() != null}.";
    }

    private static string DescribePrefabRegistration(GameObject gameObject)
    {
        ZNetScene scene = ZNetScene.instance;
        if (scene == null || gameObject == null)
        {
            return "ZNetScene=<not ready>.";
        }

        string prefabName = NormalizePrefabName(gameObject.name);
        int hash = prefabName.GetStableHashCode();
        GameObject namedPrefab = scene.GetPrefab(hash);
        string namedDescription = namedPrefab != null
            ? DescribeRegisteredPrefab(namedPrefab)
            : "<missing>";

        string prefabMatches = DescribeListMatches(scene.m_prefabs, prefabName);
        string nonNetViewMatches = DescribeListMatches(scene.m_nonNetViewPrefabs, prefabName);
        return
            $"prefabName='{prefabName}', namedPrefab={namedDescription}, " +
            $"m_prefabs={prefabMatches}, m_nonNetViewPrefabs={nonNetViewMatches}.";
    }

    private static string DescribeListMatches(List<GameObject> prefabs, string prefabName)
    {
        List<string> matches = new();
        for (int index = 0; index < prefabs.Count; index++)
        {
            GameObject prefab = prefabs[index];
            if (prefab == null || !NormalizePrefabName(prefab.name).Equals(prefabName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add($"[{index}] {DescribeRegisteredPrefab(prefab)}");
        }

        return matches.Count > 0 ? string.Join("; ", matches) : "<none>";
    }

    private static string DescribeRegisteredPrefab(GameObject prefab)
    {
        if (prefab == null)
        {
            return "<null>";
        }

        return
            $"'{prefab.name}'" +
            $"(hasZNetView={prefab.GetComponent<ZNetView>() != null}, " +
            $"hasPiece={prefab.GetComponent<Piece>() != null}, " +
            $"hasWearNTear={prefab.GetComponent<WearNTear>() != null})";
    }

    private static string BuildPath(Transform transform)
    {
        List<string> parts = new();
        Transform current = transform;
        while (current != null)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string NormalizePrefabName(string name)
    {
        return (name ?? "")
            .Replace("(Clone)", "")
            .Trim();
    }
}

[HarmonyPatch(typeof(ArcheryTarget), nameof(ArcheryTarget.Start))]
internal static class DataForgeArcheryTargetStartGuardPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ArcheryTarget __instance)
    {
        if (ArcheryTargetGuard.HasRequiredNetworkView(__instance))
        {
            return true;
        }

        ArcheryTargetGuard.LogMissingNetworkViewOnce(__instance);
        __instance.enabled = false;
        return false;
    }
}
