using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace DataForge;

internal static class FireplaceFuelCapacity
{
    private sealed class Baseline
    {
        internal readonly float Capacity;
        internal readonly ZNetView View;

        internal Baseline(float capacity, ZNetView view)
        {
            Capacity = capacity;
            View = view;
        }
    }

    private static readonly Dictionary<Fireplace, Baseline> Fireplaces = new();
    private static volatile bool RefreshPending;

    internal static bool IsEligible(Fireplace fireplace) =>
        fireplace != null && fireplace.m_canRefill && !fireplace.m_infiniteFuel && fireplace.m_fuelItem != null;

    internal static float ResolveCapacity(float original, int multiplier) => original * multiplier;

    internal static void Register(Fireplace fireplace, ZNetView view)
    {
        // Only placed/networked instances: changing both prefabs and instances
        // would lose the original capacity and compound the multiplier.
        if (DataForgeWorldLifecycle.IsShuttingDown || !IsEligible(fireplace) || view == null || !view.IsValid()) return;
        if (!Fireplaces.TryGetValue(fireplace, out Baseline baseline))
        {
            baseline = new Baseline(fireplace.m_maxFuel, view);
            Fireplaces.Add(fireplace, baseline);
        }
        fireplace.m_maxFuel = ResolveCapacity(baseline.Capacity, DataForgePlugin.ConfiguredFireplaceFuelMultiplier);
    }

    internal static void Forget(Fireplace fireplace)
    {
        if (!ReferenceEquals(fireplace, null)) Fireplaces.Remove(fireplace);
    }

    internal static void RequestRefresh() => RefreshPending = true;

    internal static void Update()
    {
        if (!RefreshPending) return;
        RefreshPending = false;
        int multiplier = DataForgePlugin.ConfiguredFireplaceFuelMultiplier;
        foreach (var pair in Fireplaces.ToArray())
        {
            if (pair.Key == null || pair.Value.View == null)
            {
                Fireplaces.Remove(pair.Key!); // A destroyed Unity object is still a non-null dictionary key.
                continue;
            }
            pair.Key.m_maxFuel = IsEligible(pair.Key)
                ? ResolveCapacity(pair.Value.Capacity, multiplier)
                : pair.Value.Capacity;
        }
    }

    internal static void OnWorldShutdown()
    {
        foreach (var pair in Fireplaces)
        {
            if (pair.Key != null) pair.Key.m_maxFuel = pair.Value.Capacity;
        }
        Fireplaces.Clear();
        RefreshPending = false;
    }

    internal static bool TryGetFuel(ZNetView view, out float fuel)
    {
        fuel = 0f;
        if (view == null || !view.IsValid() || view.GetZDO() == null) return false;
        fuel = view.GetZDO().GetFloat(ZDOVars.s_fuel);
        return true;
    }

    // A lower capacity never deletes stored fuel, but also cannot add to it.
    internal static float ClampFuelChange(float requested, float current, float capacity) =>
        Mathf.Clamp(requested, 0f, Mathf.Max(current, capacity));

    internal static int RefundAmount(float fuel, float startFuel) =>
        Mathf.FloorToInt(Mathf.Max(0f, fuel - Mathf.Max(0f, startFuel)));

    internal static void RefundFuel(Fireplace fireplace)
    {
        if (!IsEligible(fireplace)) return;
        ZNetView view = Fireplaces.TryGetValue(fireplace, out Baseline baseline)
            ? baseline.View
            : fireplace.GetComponent<ZNetView>();
        if (!TryGetFuel(view, out float fuel) || !view.IsOwner()) return;
        // Turning the feature off must not strand fuel stored above the restored capacity.
        if (DataForgePlugin.ConfiguredFireplaceFuelMultiplier == 1 && fuel <= fireplace.m_maxFuel) return;
        int remainingFuel = RefundAmount(fuel, fireplace.m_startFuel);
        if (remainingFuel <= 0) return;

        string prefabName = fireplace.m_fuelItem.gameObject.name.Replace("(Clone)", "").Trim();
        GameObject? itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
        itemPrefab ??= fireplace.m_fuelItem.gameObject;
        ItemDrop itemDrop = itemPrefab.GetComponent<ItemDrop>();
        if (itemDrop == null) return;

        Piece piece = fireplace.GetComponent<Piece>();
        float heightOffset = piece != null ? piece.m_returnResourceHeightOffset : 1f;
        Vector3 dropPosition = fireplace.transform.position + Vector3.up * heightOffset;
        int maxStackSize = Mathf.Max(1, itemDrop.m_itemData.m_shared.m_maxStackSize);
        while (remainingFuel > 0)
        {
            ItemDrop dropped = UnityEngine.Object.Instantiate(itemPrefab, dropPosition, Quaternion.identity).GetComponent<ItemDrop>();
            int stack = Mathf.Min(remainingFuel, maxStackSize);
            dropped.SetStack(stack);
            ItemDrop.OnCreateNew(dropped);
            remainingFuel -= stack;
        }
    }
}

// Keep Azu's container selection, permission checks, consumption and RPCs intact.
// Only its two fuel-quantity calculations need protection when stored fuel is
// above a newly lowered/restored capacity. This guard also applies at multiplier 1.
internal static class AzuCraftyBoxesFuelGuard
{
    internal const string PluginGuid = "Azumatt.AzuCraftyBoxes";
    private static readonly MethodInfo Min = AccessTools.DeclaredMethod(typeof(Mathf), nameof(Mathf.Min), new[] { typeof(float), typeof(float) });
    private static readonly FieldInfo MaxFuel = AccessTools.DeclaredField(typeof(Fireplace), nameof(Fireplace.m_maxFuel));
    private static readonly MethodInfo SafeMin = AccessTools.DeclaredMethod(typeof(AzuCraftyBoxesFuelGuard), nameof(NonNegativeMinimum));

    internal static void Initialize(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return;
        MethodInfo? target = null;
        MethodInfo transpiler = AccessTools.DeclaredMethod(typeof(AzuCraftyBoxesFuelGuard), nameof(Transpiler));
        try
        {
            Type? type = plugin.Instance.GetType().Assembly.GetType("AzuCraftyBoxes.Patches.FireplaceInteractPatch");
            target = type == null ? null : AccessTools.DeclaredMethod(type, "Prefix",
                new[] { typeof(Fireplace), typeof(Humanoid), typeof(bool), typeof(bool).MakeByRefType(), typeof(ZNetView) });
            if (target == null || !target.IsStatic || target.ReturnType != typeof(bool))
                throw new InvalidOperationException("The Fireplace interaction signature is not supported.");
            harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
            DataForgePlugin.Log.LogInfo($"Enabled AzuCraftyBoxes {plugin.Metadata.Version} fireplace fuel safety guard.");
        }
        catch (Exception exception)
        {
            // Harmony records a transpiler before building the wrapper. Remove
            // just this guard if installation fails, leaving other owners intact.
            if (target != null)
            {
                try { harmony.Unpatch(target, transpiler); }
                catch (Exception cleanupException)
                {
                    DataForgePlugin.Log.LogWarning($"AzuCraftyBoxes fuel guard cleanup failed: {cleanupException.Message}");
                }
            }
            DataForgePlugin.Log.LogWarning($"AzuCraftyBoxes fuel guard could not be installed. Avoid FillAll while stored fuel exceeds capacity: {exception.Message}");
        }
    }

    internal static float NonNegativeMinimum(float remaining, float available) => Mathf.Max(0f, Mathf.Min(remaining, available));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.ToList();
        // Reviewed AzuCraftyBoxes 1.8.27: two quantity calculations and five
        // capacity reads. Refuse a partial rewrite of a different implementation.
        if (codes.Count(code => code.Calls(Min)) != 2 || codes.Count(code => code.LoadsField(MaxFuel)) != 5)
            throw new InvalidOperationException("The Fireplace fuel-quantity IL pattern is not supported.");
        foreach (CodeInstruction code in codes)
        {
            if (code.Calls(Min))
            {
                code.opcode = OpCodes.Call;
                code.operand = SafeMin;
            }
        }
        return codes;
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
internal static class DataForgeFireplaceCapacityAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Fireplace __instance, ZNetView ___m_nview) => FireplaceFuelCapacity.Register(__instance, ___m_nview);
}

[HarmonyPatch(typeof(ZNetView), "OnDestroy")]
internal static class DataForgeFireplaceCapacityDestroyPatch
{
    private static void Prefix(ZNetView __instance) => FireplaceFuelCapacity.Forget(__instance.GetComponent<Fireplace>());
}

[HarmonyPatch(typeof(Fireplace), "RPC_AddFuelAmount", typeof(long), typeof(float))]
internal static class DataForgeFireplacePreserveStoredFuelPatch
{
    private static bool Prefix(Fireplace __instance, ZNetView ___m_nview, float amount)
    {
        if (!FireplaceFuelCapacity.IsEligible(__instance) || !FireplaceFuelCapacity.TryGetFuel(___m_nview, out float current) ||
            !___m_nview.IsOwner() || current <= __instance.m_maxFuel) return true;

        // The original delta RPC clamps current+amount to the lowered capacity.
        // Reuse the owner's set RPC for a normal decrement without losing the excess.
        if (amount < 0f)
            ___m_nview.InvokeRPC("RPC_SetFuelAmount", FireplaceFuelCapacity.ClampFuelChange(current + amount, current, __instance.m_maxFuel));
        return false;
    }
}

[HarmonyPatch(typeof(Fireplace), "RPC_SetFuelAmount", typeof(long), typeof(float))]
internal static class DataForgeFireplaceSetFuelLimitPatch
{
    private static void Prefix(Fireplace __instance, ZNetView ___m_nview, ref float fuel)
    {
        if (FireplaceFuelCapacity.IsEligible(__instance) && FireplaceFuelCapacity.TryGetFuel(___m_nview, out float current) &&
            ___m_nview.IsOwner() && (DataForgePlugin.ConfiguredFireplaceFuelMultiplier > 1 || current > __instance.m_maxFuel))
            fuel = FireplaceFuelCapacity.ClampFuelChange(fuel, current, __instance.m_maxFuel);
    }
}

[HarmonyPatch(typeof(WearNTear), "Destroy", typeof(HitData), typeof(bool))]
internal static class DataForgeFireplaceFuelRefundPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(WearNTear __instance, bool blockDrop, bool __runOriginal)
    {
        if (blockDrop || !__runOriginal) return;
        Fireplace fireplace = __instance.GetComponent<Fireplace>();
        if (fireplace != null) FireplaceFuelCapacity.RefundFuel(fireplace);
    }
}
