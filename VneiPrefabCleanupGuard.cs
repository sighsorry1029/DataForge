using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DataForge;

internal static class VneiPrefabCleanupGuard
{
    private static bool VneiIndexAllPatchInstalled;
    private static bool VneiIndexAllPatchFailed;
    private static DateTime NextPatchAttemptUtc = DateTime.MinValue;

    internal static void TryPatchVneiIndexAll(Harmony harmony)
    {
        if (VneiIndexAllPatchInstalled || VneiIndexAllPatchFailed)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now < NextPatchAttemptUtc)
        {
            return;
        }

        NextPatchAttemptUtc = now.AddSeconds(5);
        Type? indexingType = FindLoadedType("VNEI.Logic.Indexing");
        if (indexingType == null)
        {
            return;
        }

        MethodInfo? indexAllMethod = AccessTools.DeclaredMethod(indexingType, "IndexAll");
        MethodInfo? prefixMethod = AccessTools.DeclaredMethod(typeof(VneiPrefabCleanupGuard), nameof(RemoveInvalidEntriesBeforeVnei));
        if (indexAllMethod == null || prefixMethod == null)
        {
            VneiIndexAllPatchFailed = true;
            DataForgePlugin.Log.LogWarning("Could not install VNEI invalid prefab cleanup patch.");
            return;
        }

        harmony.Patch(indexAllMethod, prefix: new HarmonyMethod(prefixMethod));
        VneiIndexAllPatchInstalled = true;
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static void RemoveInvalidEntriesBeforeVnei()
    {
        ZNetScene scene = ZNetScene.instance;
        if (!scene)
        {
            return;
        }

        RemoveInvalidPrefabListEntries(scene.m_prefabs);
        RemoveInvalidNamedPrefabEntries(scene.m_namedPrefabs);
        RemoveInvalidPieceTableEntries(scene);
    }

    private static void RemoveInvalidPrefabListEntries(List<GameObject> prefabs)
    {
        for (int index = prefabs.Count - 1; index >= 0; index--)
        {
            if (!IsAlive(prefabs[index]))
            {
                prefabs.RemoveAt(index);
            }
        }
    }

    private static void RemoveInvalidNamedPrefabEntries(Dictionary<int, GameObject> namedPrefabs)
    {
        foreach (int key in namedPrefabs
                     .Where(pair => !IsAlive(pair.Value))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            namedPrefabs.Remove(key);
        }
    }

    private static void RemoveInvalidPieceTableEntries(ZNetScene scene)
    {
        foreach (PieceTable pieceTable in CollectBuildPieceTables(scene))
        {
            if (!pieceTable)
            {
                continue;
            }

            List<GameObject> pieces = pieceTable.m_pieces;
            for (int index = pieces.Count - 1; index >= 0; index--)
            {
                if (!IsAlive(pieces[index]))
                {
                    pieces.RemoveAt(index);
                }
            }
        }
    }

    private static IEnumerable<PieceTable> CollectBuildPieceTables(ZNetScene scene)
    {
        List<PieceTable> pieceTables = new();
        foreach (GameObject prefab in GetVneiPrefabCandidates(scene))
        {
            if (!prefab.TryGetComponent(out ItemDrop itemDrop))
            {
                continue;
            }

            PieceTable? pieceTable = itemDrop.m_itemData?.m_shared?.m_buildPieces;
            if (!pieceTable || pieceTables.Contains(pieceTable))
            {
                continue;
            }

            pieceTables.Add(pieceTable);
        }

        return pieceTables;
    }

    private static IEnumerable<GameObject> GetVneiPrefabCandidates(ZNetScene scene)
    {
        HashSet<GameObject> prefabs = new();
        foreach (GameObject prefab in scene.m_prefabs)
        {
            if (IsAlive(prefab))
            {
                prefabs.Add(prefab);
            }
        }

        foreach (GameObject prefab in scene.m_namedPrefabs.Values)
        {
            if (IsAlive(prefab))
            {
                prefabs.Add(prefab);
            }
        }

        return prefabs;
    }

    private static bool IsAlive(UnityEngine.Object? unityObject) =>
        !ReferenceEquals(unityObject, null) && unityObject;
}
