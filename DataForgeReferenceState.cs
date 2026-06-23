using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;

namespace DataForge;

internal static class DataForgeReferenceState
{
    private static string ConfigDirectory => Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName);

    internal static bool ShouldSkip(string stateKey, string referencePath, string sourceSignature, string logicVersion)
    {
        if (!File.Exists(referencePath))
        {
            return false;
        }

        string statePath = GetStatePath(stateKey);
        if (!File.Exists(statePath))
        {
            return false;
        }

        try
        {
            string[] lines = File.ReadAllLines(statePath);
            if (lines.Length < 3)
            {
                return false;
            }

            return string.Equals(lines[0].Trim(), sourceSignature, StringComparison.Ordinal) &&
                   string.Equals(lines[1].Trim(), BuildFileStamp(referencePath), StringComparison.Ordinal) &&
                   string.Equals(lines[2].Trim(), logicVersion, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static void Record(string stateKey, string referencePath, string sourceSignature, string logicVersion)
    {
        if (!File.Exists(referencePath))
        {
            return;
        }

        string statePath = GetStatePath(stateKey);
        string? stateDirectory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory);
        }

        string content = sourceSignature.Trim() + Environment.NewLine +
                         BuildFileStamp(referencePath) + Environment.NewLine +
                         logicVersion + Environment.NewLine;
        GeneratedArtifactWriter.WriteTextIfChanged(statePath, content);
    }

    internal static string BuildFileStamp(string path)
    {
        if (!File.Exists(path))
        {
            return "missing";
        }

        FileInfo fileInfo = new(path);
        return $"{fileInfo.Length.ToString(CultureInfo.InvariantCulture)}:{fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static string ComputeStableHash(string value)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
        byte[] hash = sha256.ComputeHash(bytes);
        StringBuilder builder = new(hash.Length * 2);
        foreach (byte part in hash)
        {
            builder.Append(part.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string GetStatePath(string stateKey)
    {
        return Path.Combine(ConfigDirectory, "cache", $".reference-state.{stateKey}.txt");
    }
}
