using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;

namespace DataForge;

internal static class MaterialReferenceWriter
{
    private const string ReferenceFileName = "z_materials.reference.txt";
    private static string ConfigDirectory => Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName);
    private static string ReferencePath => Path.Combine(ConfigDirectory, ReferenceFileName);

    internal static void WriteReferenceIfReady()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles ||
            DataForgeWorldLifecycle.IsShuttingDown ||
            !TryBuildReferenceContent(out string content))
        {
            return;
        }

        Directory.CreateDirectory(ConfigDirectory);
        GeneratedArtifactWriter.WriteTextIfChanged(ReferencePath, content);
    }

    internal static bool TryRegenerateReferenceFile(out string path, out bool changed, out string error)
    {
        path = ReferencePath;
        changed = false;
        if (!GeneratedArtifactWriter.CanWriteGeneratedArtifact(
                ZNetScene.instance != null,
                "Material game data is not ready yet.",
                out error))
        {
            return false;
        }

        try
        {
            if (!TryBuildReferenceContent(out string content))
            {
                error = "Material game data is not ready yet.";
                return false;
            }

            Directory.CreateDirectory(ConfigDirectory);
            changed = GeneratedArtifactWriter.WriteTextIfChanged(path, content);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not regenerate the material reference file: {ex.Message}";
            return false;
        }
    }

    private static bool TryBuildReferenceContent(out string content)
    {
        List<string> materialNames = Resources.FindObjectsOfTypeAll<Material>()
            .Where(material => material != null)
            .Select(material => NormalizeMaterialName(material.name))
            .Where(name => name.Length > 0)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(name => name, StringComparer.Ordinal).First())
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (materialNames.Count == 0)
        {
            content = "";
            return false;
        }

        StringBuilder builder = new();
        builder.AppendLine("# DataForge material lookup.");
        builder.AppendLine("# Use these names with visual.material.");
        builder.AppendLine("# One material name per line.");
        builder.AppendLine();
        foreach (string materialName in materialNames)
        {
            builder.AppendLine(materialName);
        }

        content = builder.ToString();
        return true;
    }

    private static string NormalizeMaterialName(string name)
    {
        return (name ?? "")
            .Replace("(Instance)", "")
            .Replace("(Clone)", "")
            .Trim();
    }
}
