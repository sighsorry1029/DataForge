using System;
using System.Collections.Generic;
using System.Linq;
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
    private static Exception? Finalizer(
        Pickable __instance,
        PickableAmountState __state,
        Exception? __exception)
    {
        if (__state.Changed)
        {
            __instance.m_amount = __state.Amount;
        }

        return __exception;
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
        int replacements = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (SpawnItemMethod != null && instruction.Calls(SpawnItemMethod))
            {
                CodeInstruction replacement = new(instruction)
                {
                    opcode = OpCodes.Call,
                    operand = SpawnItemWithMultiplierMethod
                };
                replacements++;
                yield return replacement;
                continue;
            }

            yield return instruction;
        }

        if (replacements != 1)
        {
            DataForgePlugin.Log.LogWarning(
                $"CookingStation output multiplier patch expected one SpawnItem call but replaced {replacements}; cooking output multipliers may be unavailable.");
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
    private readonly struct CraftAmountReservation
    {
        internal CraftAmountReservation(Inventory inventory, string prefabName, int originalStack, int adjustedStack)
        {
            Inventory = inventory;
            PrefabName = prefabName;
            OriginalStack = originalStack;
            AdjustedStack = adjustedStack;
        }

        internal Inventory Inventory { get; }
        internal string PrefabName { get; }
        internal int OriginalStack { get; }
        internal int AdjustedStack { get; }
    }

    [ThreadStatic]
    private static CraftAmountReservation? PendingCraftAmount;
    private static bool MissingCraftAmountReservationReported;

    private static readonly MethodInfo? CanAddItemMethod = AccessTools.DeclaredMethod(
        typeof(Inventory),
        nameof(Inventory.CanAddItem),
        new[] { typeof(GameObject), typeof(int) });
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
    private static readonly MethodInfo CanAddCraftedItemWithMultiplierMethod =
        AccessTools.DeclaredMethod(typeof(DataForgeInventoryGuiDoCraftingAmountMultiplierPatch), nameof(CanAddCraftedItemWithMultiplier));

    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        PendingCraftAmount = null;
    }

    [HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(Exception? __exception)
    {
        PendingCraftAmount = null;
        return __exception;
    }

    [HarmonyPriority(Priority.Last)]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> patched = instructions.ToList();
        List<int> capacityChecks = new();
        List<int> addItemCalls = new();
        for (int index = 0; index < patched.Count; index++)
        {
            CodeInstruction instruction = patched[index];
            if (CanAddItemMethod != null && instruction.Calls(CanAddItemMethod))
            {
                capacityChecks.Add(index);
                continue;
            }

            if (AddItemMethod != null && instruction.Calls(AddItemMethod))
            {
                addItemCalls.Add(index);
            }
        }

        if (capacityChecks.Count != 1 || addItemCalls.Count != 1)
        {
            DataForgePlugin.Log.LogWarning(
                $"Crafting output multiplier patch expected one capacity check and one Inventory.AddItem call but found " +
                $"{capacityChecks.Count} and {addItemCalls.Count}; the crafting multiplier patch was disabled safely.");
            return patched;
        }

        patched[capacityChecks[0]] = new CodeInstruction(patched[capacityChecks[0]])
        {
            opcode = OpCodes.Call,
            operand = CanAddCraftedItemWithMultiplierMethod
        };
        patched[addItemCalls[0]] = new CodeInstruction(patched[addItemCalls[0]])
        {
            opcode = OpCodes.Call,
            operand = AddItemWithMultiplierMethod
        };
        return patched;
    }

    private static bool CanAddCraftedItemWithMultiplier(Inventory inventory, GameObject prefab, int stack)
    {
        int originalStack = stack;
        stack = ItemOverrideManager.ApplyAcquisitionAmountMultiplier(prefab, originalStack);
        if (!HasExactCraftCapacity(inventory, prefab, stack, quality: 1))
        {
            return false;
        }

        PendingCraftAmount = new CraftAmountReservation(
            inventory,
            prefab != null ? prefab.name : "",
            originalStack,
            stack);
        return true;
    }

    private static bool HasExactCraftCapacity(
        Inventory inventory,
        GameObject? prefab,
        int stack,
        int quality)
    {
        if (prefab == null || stack <= 0 || !inventory.CanAddItem(prefab, stack))
        {
            return false;
        }

        ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
        if (itemDrop?.m_itemData?.m_shared == null)
        {
            return false;
        }

        ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
        byte outputWorldLevel = (byte)Game.m_worldLevel;
        List<ItemDrop.ItemData> items = inventory.GetAllItems();
        int freeSlots = Math.Max(0, inventory.GetWidth() * inventory.GetHeight() - items.Count);
        long capacity = (long)freeSlots *
                        Math.Max(1, shared.m_maxStackSize);
        foreach (ItemDrop.ItemData item in items)
        {
            if (item != null &&
                item.m_shared.m_name == shared.m_name &&
                item.m_quality == quality &&
                item.m_worldLevel == outputWorldLevel)
            {
                capacity += Math.Max(0, item.m_shared.m_maxStackSize - item.m_stack);
            }
        }

        return capacity >= stack;
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
        bool isUpgrade = position.x >= 0 &&
                         position.y >= 0 &&
                         position.x < inventory.GetWidth() &&
                         position.y < inventory.GetHeight();
        if (!isUpgrade)
        {
            CraftAmountReservation? reservation = PendingCraftAmount;
            PendingCraftAmount = null;
            bool reservationMatches = reservation.HasValue &&
                                      ReferenceEquals(reservation.Value.Inventory, inventory) &&
                                      string.Equals(reservation.Value.PrefabName, name, StringComparison.Ordinal) &&
                                      reservation.Value.OriginalStack == stack &&
                                      quality == 1;
            if (reservationMatches)
            {
                stack = reservation!.Value.AdjustedStack;
            }
            else
            {
                if (!MissingCraftAmountReservationReported)
                {
                    MissingCraftAmountReservationReported = true;
                    DataForgePlugin.Log.LogWarning(
                        "Crafting output multiplier reservation was unavailable; the vanilla amount will only be added when it fits completely.");
                }

                if (!HasExactCraftCapacity(
                        inventory,
                        ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name) : null,
                        stack,
                        quality))
                {
                    return null!;
                }
            }
        }

        return inventory.AddItem(name, stack, quality, variant, crafterID, crafterName, position, pickedUp);
    }
}
