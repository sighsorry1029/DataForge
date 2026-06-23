using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace DataForge;

[HarmonyPatch(typeof(Pickable), nameof(Pickable.RPC_Pick))]
internal static class DataForgePickableRpcPickAmountMultiplierPatch
{
    private readonly struct PickableAmountState
    {
        public PickableAmountState(int amount, bool changed)
        {
            Amount = amount;
            Changed = changed;
        }

        public int Amount { get; }
        public bool Changed { get; }
    }

    [HarmonyPriority(Priority.First)]
    private static void Prefix(Pickable __instance, out PickableAmountState __state)
    {
        __state = new PickableAmountState(__instance.m_amount, changed: false);
        float multiplier = ItemOverrideManager.GetAcquisitionAmountMultiplier(__instance.m_itemPrefab);
        if (Math.Abs(multiplier - 1f) <= 0.0001f)
        {
            return;
        }

        __instance.m_amount = ItemOverrideManager.MultiplyAmount(__instance.m_amount, multiplier);
        __state = new PickableAmountState(__state.Amount, changed: true);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Pickable __instance, PickableAmountState __state)
    {
        if (__state.Changed)
        {
            __instance.m_amount = __state.Amount;
        }
    }
}

[HarmonyPatch(typeof(DropTable), nameof(DropTable.GetDropListItems))]
internal static class DataForgeDropTableGetDropListItemsAmountMultiplierPatch
{
    private static void Postfix(ref List<ItemDrop.ItemData> __result)
    {
        if (__result == null || __result.Count == 0 || !DataForgePlugin.ItemOverridesEnabled)
        {
            return;
        }

        List<ItemDrop.ItemData> multiplied = new();
        bool changed = false;
        foreach (ItemDrop.ItemData item in __result)
        {
            if (item == null)
            {
                changed = true;
                continue;
            }

            int stack = ItemOverrideManager.ApplyAcquisitionAmountMultiplier(item, item.m_stack);
            if (stack <= 0)
            {
                changed = true;
                continue;
            }

            if (stack != item.m_stack)
            {
                ItemDrop.ItemData clone = item.Clone();
                clone.m_stack = stack;
                multiplied.Add(clone);
                changed = true;
            }
            else
            {
                multiplied.Add(item);
            }
        }

        if (changed)
        {
            __result = multiplied;
        }
    }
}

[HarmonyPatch(typeof(DropTable), nameof(DropTable.GetDropList), typeof(int))]
internal static class DataForgeDropTableGetDropListAmountMultiplierPatch
{
    private static void Postfix(ref List<GameObject> __result)
    {
        if (__result == null || __result.Count == 0 || !DataForgePlugin.ItemOverridesEnabled)
        {
            return;
        }

        List<GameObject> multiplied = new();
        bool changed = false;
        foreach (GameObject itemPrefab in __result)
        {
            int count = ItemOverrideManager.ApplyAcquisitionAmountMultiplier(itemPrefab, 1);
            if (count <= 0)
            {
                changed = true;
                continue;
            }

            if (count != 1)
            {
                changed = true;
            }

            for (int index = 0; index < count; index++)
            {
                multiplied.Add(itemPrefab);
            }
        }

        if (changed)
        {
            __result = multiplied;
        }
    }
}

[HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
internal static class DataForgeCharacterDropGenerateDropListAmountMultiplierPatch
{
    private static void Postfix(ref List<KeyValuePair<GameObject, int>> __result)
    {
        if (__result == null || __result.Count == 0 || !DataForgePlugin.ItemOverridesEnabled)
        {
            return;
        }

        List<KeyValuePair<GameObject, int>> multiplied = new();
        bool changed = false;
        foreach (KeyValuePair<GameObject, int> drop in __result)
        {
            int amount = ItemOverrideManager.ApplyAcquisitionAmountMultiplier(drop.Key, drop.Value);
            if (amount <= 0)
            {
                changed = true;
                continue;
            }

            if (amount != drop.Value)
            {
                changed = true;
            }

            multiplied.Add(new KeyValuePair<GameObject, int>(drop.Key, amount));
        }

        if (changed)
        {
            __result = multiplied;
        }
    }
}

[HarmonyPatch(typeof(Smelter), nameof(Smelter.Spawn))]
internal static class DataForgeSmelterSpawnAmountMultiplierPatch
{
    private static void Prefix(Smelter __instance, string ore, ref int stack)
    {
        Smelter.ItemConversion conversion = __instance.GetItemConversion(ore);
        if (conversion?.m_to == null)
        {
            return;
        }

        stack = ItemOverrideManager.ApplyAcquisitionAmountMultiplier(conversion.m_to.gameObject, stack);
    }
}

[HarmonyPatch(typeof(CookingStation), nameof(CookingStation.RPC_RemoveDoneItem))]
internal static class DataForgeCookingStationRemoveDoneItemAmountMultiplierPatch
{
    private static readonly MethodInfo? SpawnItemMethod =
        AccessTools.DeclaredMethod(typeof(CookingStation), nameof(CookingStation.SpawnItem));

    private static readonly MethodInfo SpawnItemWithMultiplierMethod =
        AccessTools.DeclaredMethod(typeof(DataForgeCookingStationRemoveDoneItemAmountMultiplierPatch), nameof(SpawnItemWithMultiplier));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (SpawnItemMethod != null && instruction.Calls(SpawnItemMethod))
            {
                yield return new CodeInstruction(OpCodes.Call, SpawnItemWithMultiplierMethod);
                continue;
            }

            yield return instruction;
        }
    }

    private static void SpawnItemWithMultiplier(CookingStation station, string prefabName, int slot, Vector3 userPoint)
    {
        GameObject? prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
        int amount = ItemOverrideManager.ApplyAcquisitionAmountMultiplier(prefab, 1);
        for (int index = 0; index < amount; index++)
        {
            station.SpawnItem(prefabName, slot, userPoint);
        }
    }
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
internal static class DataForgeInventoryGuiDoCraftingAmountMultiplierPatch
{
    private static readonly MethodInfo? AddItemMethod = AccessTools.DeclaredMethod(
        typeof(Inventory),
        nameof(Inventory.AddItem),
        new[]
        {
            typeof(string),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(long),
            typeof(string),
            typeof(Vector2i),
            typeof(bool)
        });

    private static readonly MethodInfo AddItemWithMultiplierMethod =
        AccessTools.DeclaredMethod(typeof(DataForgeInventoryGuiDoCraftingAmountMultiplierPatch), nameof(AddItemWithMultiplier));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (AddItemMethod != null && instruction.Calls(AddItemMethod))
            {
                yield return new CodeInstruction(OpCodes.Call, AddItemWithMultiplierMethod);
                continue;
            }

            yield return instruction;
        }
    }

    private static ItemDrop.ItemData AddItemWithMultiplier(
        Inventory inventory,
        string name,
        int stack,
        int quality,
        int variant,
        long crafterID,
        string crafterName,
        Vector2i position,
        bool pickedUp)
    {
        GameObject? prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name) : null;
        stack = ItemOverrideManager.ApplyAcquisitionAmountMultiplier(prefab, stack);
        return inventory.AddItem(name, stack, quality, variant, crafterID, crafterName, position, pickedUp);
    }
}
