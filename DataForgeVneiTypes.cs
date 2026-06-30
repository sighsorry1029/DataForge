using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;

namespace DataForge;

internal static class DataForgeVneiTypes
{
    private const string VneiPluginGuid = "com.maxsch.valheim.vnei";
    private static readonly HashSet<string> LoggedTypeFailures = new(StringComparer.Ordinal);
    private static Assembly? VneiAssembly;

    internal static Type? Get(string fullName)
    {
        Assembly? assembly = GetAssembly();
        if (assembly == null)
        {
            return null;
        }

        try
        {
            return assembly.GetType(fullName, throwOnError: false);
        }
        catch (Exception ex)
        {
            if (LoggedTypeFailures.Add(fullName))
            {
                DataForgePlugin.Log.LogWarning($"Could not resolve VNEI type '{fullName}': {ex.Message}");
            }

            return null;
        }
    }

    private static Assembly? GetAssembly()
    {
        if (VneiAssembly != null)
        {
            return VneiAssembly;
        }

        if (Chainloader.PluginInfos.TryGetValue(VneiPluginGuid, out var pluginInfo))
        {
            VneiAssembly = pluginInfo.Instance?.GetType().Assembly;
        }

        VneiAssembly ??= AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "VNEI", StringComparison.OrdinalIgnoreCase));

        return VneiAssembly;
    }
}
