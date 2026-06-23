using HarmonyLib;
using UnityEngine;

namespace DataForge;

internal static class FireplaceFuelOverflow
{
    internal static bool IsEnabledFor(Fireplace fireplace, out int maxStoredFuel)
    {
        maxStoredFuel = DataForgePlugin.MaxStoredFireplaceFuel;
        return maxStoredFuel > 0 &&
               fireplace != null &&
               fireplace.m_canRefill &&
               !fireplace.m_infiniteFuel &&
               fireplace.m_fuelItem != null &&
               maxStoredFuel > fireplace.m_maxFuel;
    }

    internal static bool TryGetFuel(Fireplace fireplace, out float fuel)
    {
        fuel = 0f;
        if (fireplace.m_nview == null || !fireplace.m_nview.IsValid() || fireplace.m_nview.GetZDO() == null)
        {
            return false;
        }

        fuel = fireplace.m_nview.GetZDO().GetFloat(ZDOVars.s_fuel);
        return true;
    }

    internal static bool CanAcceptMore(float fuel, int maxStoredFuel)
    {
        return Mathf.CeilToInt(fuel) < maxStoredFuel;
    }

    internal static bool IsVanillaFull(Fireplace fireplace, float fuel)
    {
        return Mathf.CeilToInt(fuel) >= fireplace.m_maxFuel;
    }

    internal static void InvokeSetFuel(Fireplace fireplace, float fuel, int maxStoredFuel)
    {
        fireplace.m_nview.InvokeRPC("RPC_SetFuelAmount", Mathf.Clamp(fuel, 0f, maxStoredFuel));
    }

    internal static void SetOwnedFuel(Fireplace fireplace, float fuel, int maxStoredFuel)
    {
        fireplace.m_nview.GetZDO().Set(ZDOVars.s_fuel, Mathf.Clamp(fuel, 0f, maxStoredFuel));
        fireplace.m_fuelAddedEffects.Create(fireplace.transform.position, fireplace.transform.rotation);
        fireplace.UpdateState();
    }

    internal static void ShowCannotAddMore(Humanoid user, string fuelName)
    {
        user.Message(MessageHud.MessageType.Center, Localization.instance.Localize("$msg_cantaddmore", new[] { fuelName }));
    }

    internal static void ShowOutOfFuel(Humanoid user, string fuelName)
    {
        user.Message(MessageHud.MessageType.Center, "$msg_outof " + fuelName);
    }

    internal static void ShowAddingFuel(Humanoid user, string fuelName)
    {
        user.Message(MessageHud.MessageType.Center, Localization.instance.Localize("$msg_fireadding", new[] { fuelName }));
    }

    internal static void RefundFuel(Fireplace fireplace)
    {
        if (!IsEnabledFor(fireplace, out int maxStoredFuel) ||
            !TryGetFuel(fireplace, out float fuel) ||
            !fireplace.m_nview.IsOwner())
        {
            return;
        }

        float refundableFuel = Mathf.Min(Mathf.Max(0f, fuel), maxStoredFuel) - Mathf.Max(0f, fireplace.m_startFuel);
        int remainingFuel = Mathf.FloorToInt(Mathf.Max(0f, refundableFuel));
        if (remainingFuel <= 0)
        {
            return;
        }

        GameObject itemPrefab = ResolveFuelPrefab(fireplace);
        ItemDrop itemDrop = itemPrefab.GetComponent<ItemDrop>();
        if (itemDrop == null)
        {
            return;
        }

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

    private static GameObject ResolveFuelPrefab(Fireplace fireplace)
    {
        string prefabName = NormalizePrefabName(fireplace.m_fuelItem.gameObject.name);
        GameObject? itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
        return itemPrefab != null ? itemPrefab : fireplace.m_fuelItem.gameObject;
    }

    private static string NormalizePrefabName(string prefabName)
    {
        return prefabName.Replace("(Clone)", "").Trim();
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
internal static class DataForgeFireplaceOverflowInteractPatch
{
    private static bool Prefix(Fireplace __instance, Humanoid user, bool hold, bool alt, ref bool __result)
    {
        if (!FireplaceFuelOverflow.IsEnabledFor(__instance, out int maxStoredFuel) ||
            !FireplaceFuelOverflow.TryGetFuel(__instance, out float fuel) ||
            !FireplaceFuelOverflow.IsVanillaFull(__instance, fuel))
        {
            return true;
        }

        if (hold)
        {
            if (__instance.m_holdRepeatInterval <= 0f)
            {
                __result = false;
                return false;
            }

            if (Time.time - __instance.m_lastUseTime < __instance.m_holdRepeatInterval)
            {
                __result = false;
                return false;
            }
        }

        if (!__instance.m_nview.HasOwner())
        {
            __instance.m_nview.ClaimOwnership();
        }

        if (__instance.m_canTurnOff && !hold && !alt && fuel > 0f)
        {
            return true;
        }

        Inventory inventory = user.GetInventory();
        string fuelName = __instance.m_fuelItem.m_itemData.m_shared.m_name;
        if (inventory == null)
        {
            __result = false;
            return false;
        }

        if (!inventory.HaveItem(fuelName))
        {
            FireplaceFuelOverflow.ShowOutOfFuel(user, fuelName);
            __result = false;
            return false;
        }

        if (!FireplaceFuelOverflow.CanAcceptMore(fuel, maxStoredFuel))
        {
            FireplaceFuelOverflow.ShowCannotAddMore(user, fuelName);
            __result = false;
            return false;
        }

        FireplaceFuelOverflow.ShowAddingFuel(user, fuelName);
        inventory.RemoveItem(fuelName, 1);
        FireplaceFuelOverflow.InvokeSetFuel(__instance, fuel + 1f, maxStoredFuel);
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.UseItem))]
internal static class DataForgeFireplaceOverflowUseItemPatch
{
    private static bool Prefix(Fireplace __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
    {
        if (!FireplaceFuelOverflow.IsEnabledFor(__instance, out int maxStoredFuel) ||
            !FireplaceFuelOverflow.TryGetFuel(__instance, out float fuel) ||
            !FireplaceFuelOverflow.IsVanillaFull(__instance, fuel))
        {
            return true;
        }

        string fuelName = __instance.m_fuelItem.m_itemData.m_shared.m_name;
        if (item.m_shared.m_name != fuelName)
        {
            return true;
        }

        if (!FireplaceFuelOverflow.CanAcceptMore(fuel, maxStoredFuel))
        {
            FireplaceFuelOverflow.ShowCannotAddMore(user, item.m_shared.m_name);
            __result = true;
            return false;
        }

        Inventory inventory = user.GetInventory();
        FireplaceFuelOverflow.ShowAddingFuel(user, item.m_shared.m_name);
        inventory.RemoveItem(item, 1);
        FireplaceFuelOverflow.InvokeSetFuel(__instance, fuel + 1f, maxStoredFuel);
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.CanUseItems))]
internal static class DataForgeFireplaceOverflowCanUseItemsPatch
{
    private static bool Prefix(Fireplace __instance, Player player, bool sendErrorMessage, ref bool __result)
    {
        if (!FireplaceFuelOverflow.IsEnabledFor(__instance, out int maxStoredFuel) ||
            !FireplaceFuelOverflow.TryGetFuel(__instance, out float fuel) ||
            !FireplaceFuelOverflow.IsVanillaFull(__instance, fuel))
        {
            return true;
        }

        string fuelName = __instance.m_fuelItem.m_itemData.m_shared.m_name;
        if (!player.GetInventory().HaveItem(fuelName))
        {
            if (sendErrorMessage)
            {
                FireplaceFuelOverflow.ShowOutOfFuel(player, fuelName);
            }

            __result = false;
            return false;
        }

        if (FireplaceFuelOverflow.CanAcceptMore(fuel, maxStoredFuel))
        {
            __result = true;
            return false;
        }

        if (sendErrorMessage)
        {
            FireplaceFuelOverflow.ShowCannotAddMore(player, fuelName);
        }

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.AddFuel))]
internal static class DataForgeFireplaceOverflowAddFuelPatch
{
    private static bool Prefix(Fireplace __instance, float fuel)
    {
        if (!FireplaceFuelOverflow.IsEnabledFor(__instance, out int maxStoredFuel) ||
            !FireplaceFuelOverflow.TryGetFuel(__instance, out float currentFuel))
        {
            return true;
        }

        if ((fuel < 0f && currentFuel > 0f) || (fuel > 0f && currentFuel < maxStoredFuel))
        {
            __instance.m_nview.InvokeRPC("RPC_AddFuelAmount", fuel);
        }

        return false;
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.SetFuel))]
internal static class DataForgeFireplaceOverflowSetFuelPatch
{
    private static bool Prefix(Fireplace __instance, float fuel)
    {
        if (!FireplaceFuelOverflow.IsEnabledFor(__instance, out int maxStoredFuel) ||
            !FireplaceFuelOverflow.TryGetFuel(__instance, out float currentFuel))
        {
            return true;
        }

        float clampedFuel = Mathf.Clamp(fuel, 0f, maxStoredFuel);
        if (!Mathf.Approximately(clampedFuel, currentFuel))
        {
            __instance.m_nview.InvokeRPC("RPC_SetFuelAmount", clampedFuel);
        }

        return false;
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.RPC_AddFuel))]
internal static class DataForgeFireplaceOverflowRpcAddFuelPatch
{
    private static bool Prefix(Fireplace __instance)
    {
        if (!FireplaceFuelOverflow.IsEnabledFor(__instance, out int maxStoredFuel) ||
            !FireplaceFuelOverflow.TryGetFuel(__instance, out float fuel))
        {
            return true;
        }

        if (__instance.m_nview.IsOwner() && FireplaceFuelOverflow.CanAcceptMore(fuel, maxStoredFuel))
        {
            FireplaceFuelOverflow.SetOwnedFuel(__instance, fuel + 1f, maxStoredFuel);
        }

        return false;
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.RPC_AddFuelAmount))]
internal static class DataForgeFireplaceOverflowRpcAddFuelAmountPatch
{
    private static bool Prefix(Fireplace __instance, float amount)
    {
        if (!FireplaceFuelOverflow.IsEnabledFor(__instance, out int maxStoredFuel) ||
            !FireplaceFuelOverflow.TryGetFuel(__instance, out float fuel))
        {
            return true;
        }

        if (__instance.m_nview.IsOwner())
        {
            FireplaceFuelOverflow.SetOwnedFuel(__instance, fuel + amount, maxStoredFuel);
        }

        return false;
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.RPC_SetFuelAmount))]
internal static class DataForgeFireplaceOverflowRpcSetFuelAmountPatch
{
    private static bool Prefix(Fireplace __instance, float fuel)
    {
        if (!FireplaceFuelOverflow.IsEnabledFor(__instance, out int maxStoredFuel))
        {
            return true;
        }

        if (__instance.m_nview.IsOwner())
        {
            FireplaceFuelOverflow.SetOwnedFuel(__instance, fuel, maxStoredFuel);
        }

        return false;
    }
}

[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
internal static class DataForgeFireplaceOverflowWearNTearDestroyPatch
{
    private static void Prefix(WearNTear __instance, bool blockDrop)
    {
        if (blockDrop)
        {
            return;
        }

        Fireplace fireplace = __instance.GetComponent<Fireplace>();
        if (fireplace != null)
        {
            FireplaceFuelOverflow.RefundFuel(fireplace);
        }
    }
}
