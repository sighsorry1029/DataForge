using System;
using System.Globalization;
using System.Linq;

namespace DataForge;

internal static class ReferenceDefaultRules
{
    private const float FloatEpsilon = 0.0001f;

    internal static bool IsDefaultString(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string trimmed = value.Trim();
        if (IsDefaultSkillValuePair(trimmed, propertyName))
        {
            return true;
        }

        if (IsDefaultOverTimeTuple(trimmed, propertyName))
        {
            return true;
        }

        if (IsDefaultDamageTakenModifier(trimmed, propertyName))
        {
            return true;
        }

        if (IsDefaultCraftingStationTuple(trimmed, propertyName))
        {
            return true;
        }

        if (IsDefaultDamageTuple(trimmed, propertyName))
        {
            return true;
        }

        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("undefined", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("topLeft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (propertyName.Equals("Category", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Equals("Misc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return propertyName.Equals("MaterialType", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Equals("Wood", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool DefaultBool(string propertyName)
    {
        return propertyName.Equals("Override", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Teleportable", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Floating", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("CanBeRemoved", StringComparison.OrdinalIgnoreCase);
    }

    internal static int DefaultInt(string propertyName)
    {
        if (propertyName.Equals("ListSortWeight", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        return propertyName.Equals("Amount", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("MinStationLevel", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("MaxStackSize", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("MaxQuality", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    internal static bool IsDefaultFloat(float value, string propertyName)
    {
        float defaultValue = 0f;
        if (propertyName.EndsWith("Multiplier", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("RaiseSkillAmount", StringComparison.OrdinalIgnoreCase))
        {
            defaultValue = 1f;
        }
        else if (propertyName.Equals("TimedBlockBonus", StringComparison.OrdinalIgnoreCase))
        {
            defaultValue = 2f;
        }
        else if (propertyName.Equals("LastChainDamageMultiplier", StringComparison.OrdinalIgnoreCase))
        {
            defaultValue = 2f;
        }
        else if (propertyName.Equals("Scale", StringComparison.OrdinalIgnoreCase))
        {
            defaultValue = 1f;
        }

        return Math.Abs(value - defaultValue) <= FloatEpsilon;
    }

    private static bool IsDefaultCraftingStationTuple(string value, string propertyName)
    {
        if (!propertyName.Equals("CraftingStation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = SplitTuple(value);
        if (parts.Length > 2)
        {
            return false;
        }

        bool stationIsNone = parts.Length == 0 ||
                             parts[0].Length == 0 ||
                             parts[0].Equals("None", StringComparison.OrdinalIgnoreCase) ||
                             parts[0].Equals("Null", StringComparison.OrdinalIgnoreCase);
        return stationIsNone && IsDefaultIntPart(parts, 1, 1);
    }

    private static bool IsDefaultDamageTuple(string value, string propertyName)
    {
        return IsDamageTypeProperty(propertyName) && IsDefaultFloatTuple(value, 0f, 0f);
    }

    private static bool IsDefaultDamageTakenModifier(string value, string propertyName)
    {
        return IsDamageTypeProperty(propertyName) && value.Equals("normal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDamageTypeProperty(string propertyName)
    {
        return propertyName.Equals("Blunt", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Slash", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Pierce", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Chop", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Pickaxe", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Fire", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Frost", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Lightning", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Poison", StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals("Spirit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultSkillValuePair(string value, string propertyName)
    {
        bool isSkillPair =
            propertyName.Equals("RaiseSkill", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("SkillLevel", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("SkillLevel2", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("AttackDamage", StringComparison.OrdinalIgnoreCase);
        if (!isSkillPair)
        {
            return false;
        }

        string[] parts = value.Split(new[] { ',' }, 2, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (parts.Length == 0 || !parts[0].Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        float expectedValue = propertyName.Equals("AttackDamage", StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
        if (parts.Length == 1)
        {
            return Math.Abs(expectedValue) <= FloatEpsilon;
        }

        return float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue) &&
               Math.Abs(parsedValue - expectedValue) <= FloatEpsilon;
    }

    private static bool IsDefaultOverTimeTuple(string value, string propertyName)
    {
        if (propertyName.Equals("UpFront", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f, 0f, 0f);
        }

        if (propertyName.Equals("RegenMultiplier", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 1f, 1f, 1f);
        }

        if (propertyName.Equals("RequireOnlyOneIngredient", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultBoolFloatTuple(value, false, 1f);
        }

        if (propertyName.Equals("HealthPerTick", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatFloatStringTuple(value, 0f, 0f, "Undefined");
        }

        if (propertyName.Equals("HealthOverTime", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f, 5f);
        }

        if (propertyName.Equals("Time", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f);
        }

        if (propertyName.Equals("IconFlags", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultBoolTuple(value, false, false);
        }

        if (propertyName.Equals("EitrOverTime", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f);
        }

        if (propertyName.Equals("Draw", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f, 0f);
        }

        if (propertyName.Equals("Cost", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f, 0f, 0f);
        }

        if (propertyName.Equals("MissingHealth", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f, 0f);
        }

        if (propertyName.Equals("SpawnOnHit", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultSpawnOnHitTuple(value);
        }

        if (propertyName.Equals("SpawnOnTrigger", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultNoneValue(value);
        }

        if (propertyName.Equals("Reload", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultBoolFloatTuple(value, false, 0f, 0f, 0f);
        }

        if (propertyName.Equals("Armor", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f);
        }

        if (propertyName.Equals("Block", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f);
        }

        if (propertyName.Equals("Fall", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f);
        }

        if (propertyName.Equals("Sneak", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f);
        }

        if (propertyName.Equals("WindRun", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f);
        }

        if (propertyName.Equals("JumpModifier", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f, 0f);
        }

        if (propertyName.Equals("Weight", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("BuildRange", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("BlockPower", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 0f, 0f);
        }

        if (propertyName.Equals("Comfort", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultIntStringTuple(value, 0, "None");
        }

        if (propertyName.Equals("DeflectionForce", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFloatTuple(value, 20f, 5f);
        }

        if (propertyName.Equals("Durability", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultDurabilityTuple(value);
        }

        if (propertyName.Equals("Food", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultFoodTuple(value);
        }

        if (propertyName.Equals("Fuel", StringComparison.OrdinalIgnoreCase))
        {
            return IsDefaultCookingStationFuelTuple(value);
        }

        return propertyName.Equals("StaminaOverTime", StringComparison.OrdinalIgnoreCase) &&
               IsDefaultFloatBoolTuple(value, 0f, 0f, false);
    }

    private static bool IsDefaultCookingStationFuelTuple(string value)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length != 4)
        {
            return false;
        }

        bool fuelItemIsNone = parts[0].Length == 0 ||
                              parts[0].Equals("None", StringComparison.OrdinalIgnoreCase) ||
                              parts[0].Equals("Null", StringComparison.OrdinalIgnoreCase);
        return fuelItemIsNone &&
               IsDefaultBoolPart(parts, 1, false) &&
               IsDefaultIntPart(parts, 2, 0) &&
               IsDefaultIntPart(parts, 3, 0);
    }

    private static bool IsDefaultFoodTuple(string value)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > 5)
        {
            return false;
        }

        return IsDefaultFloatPart(parts, 0, 0f) &&
               IsDefaultFloatPart(parts, 1, 0f) &&
               IsDefaultFloatPart(parts, 2, 0f) &&
               IsDefaultFloatPart(parts, 3, 0f) &&
               IsDefaultFloatPart(parts, 4, 0f);
    }

    private static bool IsDefaultDurabilityTuple(string value)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > 7)
        {
            return false;
        }

        return IsDefaultFloatPart(parts, 0, 0f) &&
               IsDefaultFloatPart(parts, 1, 0f) &&
               IsDefaultBoolPart(parts, 2, false) &&
               IsDefaultFloatPart(parts, 3, 0f) &&
               IsDefaultBoolPart(parts, 4, false) &&
               IsDefaultBoolPart(parts, 5, false) &&
               IsDefaultFloatPart(parts, 6, 0f);
    }

    private static bool IsDefaultBoolPart(string[] parts, int index, bool defaultValue)
    {
        if (index >= parts.Length || parts[index].Length == 0)
        {
            return true;
        }

        return bool.TryParse(parts[index], out bool parsedValue) && parsedValue == defaultValue;
    }

    private static bool IsDefaultIntPart(string[] parts, int index, int defaultValue)
    {
        if (index >= parts.Length || parts[index].Length == 0)
        {
            return true;
        }

        return int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue) &&
               parsedValue == defaultValue;
    }

    private static bool IsDefaultFloatPart(string[] parts, int index, float defaultValue)
    {
        if (index >= parts.Length || parts[index].Length == 0)
        {
            return true;
        }

        return float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue) &&
               Math.Abs(parsedValue - defaultValue) <= FloatEpsilon;
    }

    private static bool IsDefaultFloatFloatStringTuple(string value, float defaultFirst, float defaultSecond, string defaultThird)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > 3)
        {
            return false;
        }

        if (!IsDefaultFloatTuple(string.Join(", ", parts.Take(Math.Min(parts.Length, 2))), defaultFirst, defaultSecond))
        {
            return false;
        }

        return parts.Length <= 2 ||
               parts[2].Length == 0 ||
               parts[2].Equals(defaultThird, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultIntStringTuple(string value, int defaultFirst, string defaultSecond)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > 2)
        {
            return false;
        }

        if (!IsDefaultIntPart(parts, 0, defaultFirst))
        {
            return false;
        }

        return parts.Length <= 1 ||
               parts[1].Length == 0 ||
               parts[1].Equals(defaultSecond, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultSpawnOnHitTuple(string value)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > 2)
        {
            return false;
        }

        bool prefabIsNone = parts.Length == 0 ||
                            parts[0].Length == 0 ||
                            parts[0].Equals("None", StringComparison.OrdinalIgnoreCase) ||
                            parts[0].Equals("Null", StringComparison.OrdinalIgnoreCase);
        return prefabIsNone && IsDefaultFloatPart(parts, 1, 0f);
    }

    private static bool IsDefaultNoneValue(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0 ||
               trimmed.Equals("None", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Null", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultBoolFloatTuple(string value, bool defaultFirst, float defaultSecond)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > 2)
        {
            return false;
        }

        bool first = defaultFirst;
        if (parts.Length > 0 && parts[0].Length > 0 && !bool.TryParse(parts[0], out first))
        {
            return false;
        }

        float second = defaultSecond;
        if (parts.Length > 1 && parts[1].Length > 0 &&
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out second))
        {
            return false;
        }

        return first == defaultFirst && Math.Abs(second - defaultSecond) <= FloatEpsilon;
    }

    private static bool IsDefaultBoolTuple(string value, params bool[] defaults)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > defaults.Length)
        {
            return false;
        }

        for (int index = 0; index < defaults.Length; index++)
        {
            bool parsedValue = defaults[index];
            if (index < parts.Length && parts[index].Length > 0 &&
                !bool.TryParse(parts[index], out parsedValue))
            {
                return false;
            }

            if (parsedValue != defaults[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDefaultBoolFloatTuple(string value, bool defaultFirst, params float[] defaultRest)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > defaultRest.Length + 1)
        {
            return false;
        }

        bool first = defaultFirst;
        if (parts.Length > 0 && parts[0].Length > 0 && !bool.TryParse(parts[0], out first))
        {
            return false;
        }

        if (first != defaultFirst)
        {
            return false;
        }

        for (int index = 0; index < defaultRest.Length; index++)
        {
            int partIndex = index + 1;
            float parsedValue = defaultRest[index];
            if (partIndex < parts.Length && parts[partIndex].Length > 0 &&
                !float.TryParse(parts[partIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue))
            {
                return false;
            }

            if (Math.Abs(parsedValue - defaultRest[index]) > FloatEpsilon)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDefaultFloatTuple(string value, params float[] defaults)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > defaults.Length)
        {
            return false;
        }

        for (int index = 0; index < defaults.Length; index++)
        {
            float parsedValue = defaults[index];
            if (index < parts.Length && parts[index].Length > 0 &&
                !float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue))
            {
                return false;
            }

            if (Math.Abs(parsedValue - defaults[index]) > FloatEpsilon)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDefaultFloatBoolTuple(string value, float defaultFirst, float defaultSecond, bool defaultThird)
    {
        string[] parts = SplitTuple(value);
        if (parts.Length > 3)
        {
            return false;
        }

        if (!IsDefaultFloatTuple(string.Join(", ", parts.Take(Math.Min(parts.Length, 2))), defaultFirst, defaultSecond))
        {
            return false;
        }

        bool parsedValue = defaultThird;
        return parts.Length <= 2 ||
               parts[2].Length == 0 ||
               (bool.TryParse(parts[2], out parsedValue) && parsedValue == defaultThird);
    }

    private static string[] SplitTuple(string value)
    {
        return value.Split(new[] { ',' }, StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
    }
}
