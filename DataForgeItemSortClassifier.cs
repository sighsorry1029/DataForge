using System;
using System.Globalization;
using ItemType = ItemDrop.ItemData.ItemType;
using AnimationState = ItemDrop.ItemData.AnimationState;

namespace DataForge;

internal readonly struct DataForgeItemSortGroup
{
    internal DataForgeItemSortGroup(int bigGroupRank, int subGroupRank, int detailRank)
    {
        BigGroupRank = bigGroupRank;
        SubGroupRank = subGroupRank;
        DetailRank = detailRank;
    }

    internal int BigGroupRank { get; }
    internal int SubGroupRank { get; }
    internal int DetailRank { get; }
}

internal static class DataForgeItemSortClassifier
{
    private const int Melee = 0;
    private const int Ranged = 1;
    private const int Magic = 2;
    private const int Equipment = 3;
    private const int Food = 4;
    private const int Consumable = 5;
    private const int Meadbase = 6;
    private const int Misc = 7;
    private const int Unknown = 8;

    internal static DataForgeItemSortGroup Classify(string? itemName, ItemDrop.ItemData.SharedData? shared)
    {
        if (shared == null)
        {
            return ClassifyFallback(itemName);
        }

        if (MatchElementalMagic(shared))
        {
            return Group(Magic, 0);
        }

        if (MatchBloodMagic(shared))
        {
            return Group(Magic, 1);
        }

        if (MatchBow(itemName, shared))
        {
            return Group(Ranged, 0);
        }

        if (MatchArrow(itemName, shared))
        {
            return Group(Ranged, 1);
        }

        if (MatchCrossbow(itemName, shared))
        {
            return Group(Ranged, 2);
        }

        if (MatchBolt(itemName, shared))
        {
            return Group(Ranged, 3);
        }

        if (MatchAmmo(shared))
        {
            return Group(Ranged, 4);
        }

        if (MatchBomb(shared))
        {
            return Group(Ranged, 5);
        }

        if (MatchSword(itemName, shared))
        {
            return Group(Melee, 0, SwordDetail(itemName, shared));
        }

        if (MatchAxe(itemName, shared))
        {
            return Group(Melee, 1, AxeDetail(itemName, shared));
        }

        if (MatchClub(itemName, shared))
        {
            return Group(Melee, 2, ClubDetail(itemName, shared));
        }

        if (MatchKnife(itemName, shared))
        {
            return Group(Melee, 3);
        }

        if (MatchSpear(itemName, shared))
        {
            return Group(Melee, 4);
        }

        if (MatchPolearm(itemName, shared))
        {
            return Group(Melee, 5);
        }

        if (MatchFists(itemName, shared))
        {
            return Group(Melee, 6);
        }

        if (MatchShield(itemName, shared))
        {
            return Group(Melee, 7, ShieldDetail(itemName));
        }

        if (MatchPickaxe(itemName, shared))
        {
            return Group(Melee, 8);
        }

        if (MatchTool(itemName, shared))
        {
            return Group(Melee, 9);
        }

        if (MatchHelmet(shared))
        {
            return Group(Equipment, 0);
        }

        if (MatchChest(shared))
        {
            return Group(Equipment, 1);
        }

        if (MatchLegs(shared))
        {
            return Group(Equipment, 2);
        }

        if (MatchCape(shared))
        {
            return Group(Equipment, 3);
        }

        if (MatchUtility(shared))
        {
            return Group(Equipment, 4);
        }

        if (MatchTrinket(shared))
        {
            return Group(Equipment, 5);
        }

        if (MatchFeast(itemName))
        {
            return Group(Food, 3);
        }

        if (MatchNativeFood(shared, out int foodSubGroupRank))
        {
            return Group(Food, foodSubGroupRank);
        }

        if (MatchMeadbase(itemName, shared))
        {
            return Group(Meadbase, 0);
        }

        if (MatchMead(itemName, shared))
        {
            return Group(Consumable, 0);
        }

        if (MatchPotion(itemName, shared))
        {
            return Group(Consumable, 1);
        }

        if (shared.m_itemType == ItemType.Trophy)
        {
            return Group(Misc, 0);
        }

        if (shared.m_value > 0)
        {
            return Group(Misc, 1);
        }

        return ClassifyFallback(itemName);
    }

    private static DataForgeItemSortGroup ClassifyFallback(string? itemName)
    {
        if (HasToken(itemName, "staff", "magic", "elemental"))
        {
            return Group(Magic, 0);
        }

        if (HasToken(itemName, "bloodmagic"))
        {
            return Group(Magic, 1);
        }

        if (HasToken(itemName, "bow") && !HasToken(itemName, "crossbow"))
        {
            return Group(Ranged, 0);
        }

        if (HasToken(itemName, "arrow"))
        {
            return Group(Ranged, 1);
        }

        if (HasToken(itemName, "crossbow", "arbalest"))
        {
            return Group(Ranged, 2);
        }

        if (HasToken(itemName, "bolt"))
        {
            return Group(Ranged, 3);
        }

        if (HasToken(itemName, "bomb"))
        {
            return Group(Ranged, 5);
        }

        if (HasToken(itemName, "sword"))
        {
            return Group(Melee, 0, SwordDetail(itemName, null));
        }

        if (HasToken(itemName, "axe", "battleaxe"))
        {
            return Group(Melee, 1, AxeDetail(itemName, null));
        }

        if (HasToken(itemName, "club", "mace", "sledge"))
        {
            return Group(Melee, 2, ClubDetail(itemName, null));
        }

        if (HasToken(itemName, "knife"))
        {
            return Group(Melee, 3);
        }

        if (HasToken(itemName, "spear"))
        {
            return Group(Melee, 4);
        }

        if (HasToken(itemName, "atgeir", "polearm"))
        {
            return Group(Melee, 5);
        }

        if (HasToken(itemName, "fist"))
        {
            return Group(Melee, 6);
        }

        if (HasToken(itemName, "shield", "buckler"))
        {
            return Group(Melee, 7, ShieldDetail(itemName));
        }

        if (HasToken(itemName, "pickaxe"))
        {
            return Group(Melee, 8);
        }

        if (HasToken(itemName, "hammer", "hoe", "cultivator", "torch", "fishingrod", "tankard"))
        {
            return Group(Melee, 9);
        }

        if (HasToken(itemName, "helmet", "hood", "helm"))
        {
            return Group(Equipment, 0);
        }

        if (HasToken(itemName, "chest", "cuirass", "tunic", "robe"))
        {
            return Group(Equipment, 1);
        }

        if (HasToken(itemName, "legs", "leg", "pants", "greaves"))
        {
            return Group(Equipment, 2);
        }

        if (HasToken(itemName, "cape", "shoulder"))
        {
            return Group(Equipment, 3);
        }

        if (HasToken(itemName, "trinket"))
        {
            return Group(Equipment, 5);
        }

        if (HasToken(itemName, "feast"))
        {
            return Group(Food, 3);
        }

        if (HasToken(itemName, "meadbase"))
        {
            return Group(Meadbase, 0);
        }

        if (HasToken(itemName, "mead"))
        {
            return Group(Consumable, 0);
        }

        if (HasToken(itemName, "potion"))
        {
            return Group(Consumable, 1);
        }

        if (HasToken(itemName, "trophy"))
        {
            return Group(Misc, 0);
        }

        return Group(Unknown, 0);
    }

    private static bool MatchSword(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        IsSkillAttack(shared, Skills.SkillType.Swords) || HasToken(itemName, shared, "sword");

    private static bool MatchAxe(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        IsSkillAttack(shared, Skills.SkillType.Axes) || HasToken(itemName, shared, "axe", "battleaxe");

    private static bool MatchClub(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        IsSkillAttack(shared, Skills.SkillType.Clubs) || HasToken(itemName, shared, "club", "mace", "sledge");

    private static bool MatchKnife(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        IsSkillAttack(shared, Skills.SkillType.Knives) || HasToken(itemName, shared, "knife");

    private static bool MatchSpear(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        IsSkillAttack(shared, Skills.SkillType.Spears) || HasToken(itemName, shared, "spear");

    private static bool MatchPolearm(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        IsSkillAttack(shared, Skills.SkillType.Polearms) ||
        IsItemTypeOrAttach(shared, ItemType.Attach_Atgeir) ||
        HasToken(itemName, shared, "atgeir", "polearm");

    private static bool MatchFists(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        IsSkillAttack(shared, Skills.SkillType.Unarmed) || HasToken(itemName, shared, "fist");

    private static bool MatchShield(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        IsItemTypeOrAttach(shared, ItemType.Shield) ||
        shared.m_skillType == Skills.SkillType.Blocking ||
        HasToken(itemName, shared, "shield", "buckler");

    private static bool MatchPickaxe(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        shared.m_skillType == Skills.SkillType.Pickaxes || HasToken(itemName, shared, "pickaxe");

    private static bool MatchTool(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        shared.m_itemType == ItemType.Tool ||
        IsItemTypeOrAttach(shared, ItemType.Torch) ||
        shared.m_skillType == Skills.SkillType.Fishing ||
        shared.m_skillType == Skills.SkillType.Farming ||
        HasToken(itemName, shared, "hammer", "hoe", "cultivator", "torch", "fishingrod", "tankard");

    private static bool MatchBow(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        shared.m_skillType != Skills.SkillType.Crossbows &&
        shared.m_skillType != Skills.SkillType.ElementalMagic &&
        shared.m_skillType != Skills.SkillType.BloodMagic &&
        (shared.m_itemType == ItemType.Bow ||
         IsSkillAttack(shared, Skills.SkillType.Bows) ||
         HasToken(itemName, shared, "bow") && !HasToken(itemName, shared, "crossbow"));

    private static bool MatchCrossbow(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        !MatchAmmo(shared) &&
        (shared.m_skillType == Skills.SkillType.Crossbows ||
         HasToken(itemName, shared, "crossbow", "arbalest")) &&
        (shared.m_itemType == ItemType.Bow || HasAttackAnimation(shared) || HasToken(itemName, shared, "crossbow", "arbalest"));

    private static bool MatchArrow(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        !HasAttackAnimation(shared) &&
        (string.Equals(shared.m_ammoType, "$ammo_arrows", StringComparison.Ordinal) ||
         HasToken(itemName, shared, "arrow")) &&
        GetTotalDamage(shared) > 0f;

    private static bool MatchBolt(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        !HasAttackAnimation(shared) &&
        (string.Equals(shared.m_ammoType, "$ammo_bolts", StringComparison.Ordinal) ||
         HasToken(itemName, shared, "bolt")) &&
        GetTotalDamage(shared) > 0f;

    private static bool MatchAmmo(ItemDrop.ItemData.SharedData shared) =>
        shared.m_itemType == ItemType.Ammo ||
        shared.m_itemType == ItemType.AmmoNonEquipable;

    private static bool MatchBomb(ItemDrop.ItemData.SharedData shared) =>
        shared.m_itemType == ItemType.OneHandedWeapon &&
        shared.m_animationState == AnimationState.Unarmed &&
        shared.m_attack?.m_attackType == Attack.AttackType.Projectile &&
        string.Equals(shared.m_attack?.m_attackAnimation, "throw_bomb", StringComparison.Ordinal);

    private static bool MatchElementalMagic(ItemDrop.ItemData.SharedData shared) =>
        shared.m_skillType == Skills.SkillType.ElementalMagic;

    private static bool MatchBloodMagic(ItemDrop.ItemData.SharedData shared) =>
        shared.m_skillType == Skills.SkillType.BloodMagic;

    private static bool MatchHelmet(ItemDrop.ItemData.SharedData shared) => IsItemTypeOrAttach(shared, ItemType.Helmet);

    private static bool MatchChest(ItemDrop.ItemData.SharedData shared) => IsItemTypeOrAttach(shared, ItemType.Chest);

    private static bool MatchLegs(ItemDrop.ItemData.SharedData shared) => IsItemTypeOrAttach(shared, ItemType.Legs);

    private static bool MatchCape(ItemDrop.ItemData.SharedData shared) => IsItemTypeOrAttach(shared, ItemType.Shoulder);

    private static bool MatchUtility(ItemDrop.ItemData.SharedData shared) => IsItemTypeOrAttach(shared, ItemType.Utility);

    private static bool MatchTrinket(ItemDrop.ItemData.SharedData shared) => IsItemTypeOrAttach(shared, ItemType.Trinket);

    private static bool MatchNativeFood(ItemDrop.ItemData.SharedData shared, out int subGroupRank)
    {
        ItemDrop.ItemData.SharedData food = GetFoodSharedData(shared);
        subGroupRank = 0;
        if (!HasFoodCarrier(shared) ||
            food.m_food <= 0f && food.m_foodStamina <= 0f && food.m_foodEitr <= 0f)
        {
            return false;
        }

        if (food.m_food >= food.m_foodStamina && food.m_food >= food.m_foodEitr)
        {
            subGroupRank = 0;
            return true;
        }

        if (food.m_foodStamina >= food.m_food && food.m_foodStamina >= food.m_foodEitr)
        {
            subGroupRank = 1;
            return true;
        }

        subGroupRank = 2;
        return true;
    }

    private static bool HasFoodCarrier(ItemDrop.ItemData.SharedData shared) =>
        shared.m_itemType == ItemType.Consumable ||
        shared.m_itemType == ItemType.Material && shared.m_appendToolTip != null;

    private static ItemDrop.ItemData.SharedData GetFoodSharedData(ItemDrop.ItemData.SharedData shared) =>
        shared.m_appendToolTip?.m_itemData?.m_shared ?? shared;

    private static bool MatchFeast(string? itemName) =>
        HasToken(itemName, "feast") && HasToken(itemName, "material");

    private static bool MatchMeadbase(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        HasToken(itemName, shared, "meadbase");

    private static bool MatchMead(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        !MatchMeadbase(itemName, shared) &&
        HasToken(itemName, shared, "mead");

    private static bool MatchPotion(string? itemName, ItemDrop.ItemData.SharedData shared) =>
        !MatchMead(itemName, shared) &&
        !MatchMeadbase(itemName, shared) &&
        !MatchNativeFood(shared, out _) &&
        (HasToken(itemName, shared, "potion") ||
         (shared.m_itemType == ItemType.Material || shared.m_itemType == ItemType.Consumable) &&
         shared.m_consumeStatusEffect != null);

    private static int SwordDetail(string? itemName, ItemDrop.ItemData.SharedData? shared)
    {
        if (IsTwoHanded(shared) ||
            HasToken(itemName, "greatsword", "twohandedsword", "2hsword"))
        {
            return 1;
        }

        return 0;
    }

    private static int AxeDetail(string? itemName, ItemDrop.ItemData.SharedData? shared)
    {
        if (IsBattleaxe(itemName, shared))
        {
            return 2;
        }

        return IsTwoHanded(shared) ||
               HasToken(itemName, "twohandedaxe", "2haxe", "dualaxe")
            ? 1
            : 0;
    }

    private static bool IsBattleaxe(string? itemName, ItemDrop.ItemData.SharedData? shared)
    {
        if (HasToken(itemName, "dualaxe"))
        {
            return false;
        }

        if (HasToken(itemName, "battleaxe"))
        {
            return true;
        }

        string primaryAnimation = shared?.m_attack?.m_attackAnimation ?? "";
        string secondaryAnimation = shared?.m_secondaryAttack?.m_attackAnimation ?? "";
        return IsTwoHanded(shared) &&
               string.Equals(secondaryAnimation, "battleaxe_secondary", StringComparison.OrdinalIgnoreCase) &&
               !ContainsIgnoreCase(primaryAnimation, "dualaxe") &&
               (ContainsIgnoreCase(primaryAnimation, "battleaxe") ||
                ContainsIgnoreCase(secondaryAnimation, "battleaxe"));
    }

    private static int ClubDetail(string? itemName, ItemDrop.ItemData.SharedData? shared) =>
        IsTwoHanded(shared) ||
        HasToken(itemName, "sledge", "twohandedclub", "2hclub")
            ? 1
            : 0;

    private static bool IsTwoHanded(ItemDrop.ItemData.SharedData? shared) =>
        shared?.m_itemType == ItemType.TwoHandedWeapon ||
        shared?.m_itemType == ItemType.TwoHandedWeaponLeft;

    private static int ShieldDetail(string? itemName)
    {
        if (HasToken(itemName, "buckler"))
        {
            return 0;
        }

        return HasToken(itemName, "tower") ? 2 : 1;
    }

    private static bool IsSkillAttack(ItemDrop.ItemData.SharedData shared, Skills.SkillType skillType) =>
        shared.m_skillType == skillType &&
        HasAttackAnimation(shared) &&
        GetTotalDamage(shared) > 0f;

    private static bool HasAttackAnimation(ItemDrop.ItemData.SharedData shared) =>
        !string.IsNullOrEmpty(shared.m_attack?.m_attackAnimation);

    private static float GetTotalDamage(ItemDrop.ItemData.SharedData shared)
    {
        HitData.DamageTypes damage = shared.m_damages;
        return damage.m_blunt +
               damage.m_slash +
               damage.m_pierce +
               damage.m_chop +
               damage.m_pickaxe +
               damage.m_fire +
               damage.m_frost +
               damage.m_lightning +
               damage.m_poison +
               damage.m_spirit;
    }

    private static bool IsItemTypeOrAttach(ItemDrop.ItemData.SharedData shared, ItemType itemType) =>
        shared.m_itemType == itemType ||
        shared.m_attachOverride == itemType;

    private static bool HasToken(string? itemName, ItemDrop.ItemData.SharedData? shared, params string[] tokens)
    {
        foreach (string? candidate in new[]
                 {
                     itemName,
                     shared?.m_name,
                     StripLocalizationToken(shared?.m_name)
                 })
        {
            if (HasToken(candidate, tokens))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasToken(string? value, params string[] tokens)
    {
        string normalized = DataForgeResourceMap.NormalizeResourceToken(value);
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (string token in tokens)
        {
            string normalizedToken = DataForgeResourceMap.NormalizeResourceToken(token);
            if (normalizedToken.Length > 0 &&
                normalized.IndexOf(normalizedToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsIgnoreCase(string value, string token) =>
        value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string StripLocalizationToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        string trimmed = value!.Trim();
        return trimmed.StartsWith("$item_", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring("$item_".Length)
            : trimmed.TrimStart('$');
    }

    private static DataForgeItemSortGroup Group(int bigGroupRank, int subGroupRank, int detailRank = 0) =>
        new(bigGroupRank, subGroupRank, detailRank);
}
