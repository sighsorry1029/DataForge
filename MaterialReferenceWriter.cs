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
    private static bool Written;

    internal static void WriteReferenceIfReady()
    {
        if (Written || !DataForgePlugin.UsesLocalAuthorityFiles)
        {
            return;
        }

        List<string> materialNames = Resources.FindObjectsOfTypeAll<Material>()
            .Where(material => material != null)
            .Select(material => NormalizeMaterialName(material.name))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (materialNames.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(ConfigDirectory);
        StringBuilder builder = new();
        builder.AppendLine("# DataForge material lookup.");
        builder.AppendLine("# Use these names with visual.material.");
        builder.AppendLine("# One material name per line.");
        builder.AppendLine();
        foreach (string materialName in materialNames)
        {
            builder.AppendLine(materialName);
        }

        GeneratedArtifactWriter.WriteTextIfChanged(
            Path.Combine(ConfigDirectory, ReferenceFileName),
            builder.ToString());
        Written = true;
    }

    private static string NormalizeMaterialName(string name)
    {
        return (name ?? "")
            .Replace("(Instance)", "")
            .Replace("(Clone)", "")
            .Trim();
    }
}
