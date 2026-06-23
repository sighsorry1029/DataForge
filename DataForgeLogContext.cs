using System;
using System.IO;

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
        string trimmed = source?.Trim() ?? "";
        string displaySource = trimmed.Length == 0
            ? "unknown source"
            : Path.GetFileName(trimmed);
        if (displaySource.Length == 0)
        {
            displaySource = trimmed;
        }

        return $"{displaySource}#{entryIndex}";
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
