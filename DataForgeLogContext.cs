using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.RepresentationModel;

namespace DataForge;

internal static class DataForgeLogContext
{
    [ThreadStatic]
    private static string? CurrentContext;

    internal static IDisposable Push(string? context)
    {
        string? previous = CurrentContext;
        CurrentContext = string.IsNullOrWhiteSpace(context) ? previous : context;
        return new PopWhenDisposed(previous);
    }

    internal static string FormatSource(string source, int entryIndex)
    {
        return FormatSource(source, entryIndex, null);
    }

    internal static string FormatSource(string source, int entryIndex, long? lineNumber)
    {
        string displaySource = GetDisplaySource(source);
        return lineNumber is > 0 && IsLocalAuthorityFile(source)
            ? $"{displaySource}:{lineNumber.Value} (#{entryIndex})"
            : $"{displaySource}#{entryIndex}";
    }

    internal static string FormatSourceLine(string source, long lineNumber)
    {
        string displaySource = GetDisplaySource(source);
        return lineNumber > 0 && IsLocalAuthorityFile(source)
            ? $"{displaySource}:{lineNumber}"
            : displaySource;
    }

    internal static IReadOnlyList<long> GetLocalTopLevelEntryLines(string yaml, string source)
    {
        if (!IsLocalAuthorityFile(source) || string.IsNullOrWhiteSpace(yaml))
        {
            return Array.Empty<long>();
        }

        YamlStream stream = new();
        using StringReader reader = new(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlSequenceNode entries)
        {
            return Array.Empty<long>();
        }

        List<long> lines = new(entries.Children.Count);
        foreach (YamlNode entry in entries.Children)
        {
            lines.Add(entry.Start.Line);
        }

        return lines;
    }

    internal static long? GetEntryLine(IReadOnlyList<long> lines, int entryIndex)
    {
        int index = entryIndex - 1;
        return index >= 0 && index < lines.Count ? lines[index] : null;
    }

    internal static void Warning(string message)
    {
        DataForgePlugin.Log.LogWarning(WithContext(message));
    }

    private static string WithContext(string message)
    {
        return string.IsNullOrWhiteSpace(CurrentContext)
            ? message
            : $"{CurrentContext}: {message}";
    }

    private static bool IsLocalAuthorityFile(string source)
    {
        return DataForgePlugin.UsesLocalAuthorityFiles &&
               !string.IsNullOrWhiteSpace(source) &&
               File.Exists(source);
    }

    private static string GetDisplaySource(string source)
    {
        string trimmed = source?.Trim() ?? "";
        string displaySource = trimmed.Length == 0
            ? "unknown source"
            : Path.GetFileName(trimmed);
        return displaySource.Length == 0 ? trimmed : displaySource;
    }

    private sealed class PopWhenDisposed : IDisposable
    {
        private readonly string? previous;
        private bool disposed;

        internal PopWhenDisposed(string? previous)
        {
            this.previous = previous;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            CurrentContext = previous;
            disposed = true;
        }
    }
}
