using System;
using System.Linq;

namespace DataForge;

internal static class DataForgeValue
{
    internal static void Copy(string? value, Action<string> assign)
    {
        if (value != null)
        {
            assign(value);
        }
    }

    internal static void Copy(bool? value, Action<bool> assign)
    {
        if (value.HasValue)
        {
            assign(value.Value);
        }
    }

    internal static void Copy(int? value, Action<int> assign)
    {
        if (value.HasValue)
        {
            assign(value.Value);
        }
    }

    internal static void Copy(float? value, Action<float> assign)
    {
        if (value.HasValue)
        {
            assign(value.Value);
        }
    }

    internal static string[] SplitTuple(string? value)
    {
        return value?.Split(new[] { ',' }, StringSplitOptions.None)
                   .Select(part => part.Trim())
                   .ToArray() ??
               Array.Empty<string>();
    }

    internal static bool IsNone(string? value)
    {
        string trimmed = value?.Trim() ?? "";
        return trimmed.Length == 0 ||
               trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("null", StringComparison.OrdinalIgnoreCase);
    }
}
