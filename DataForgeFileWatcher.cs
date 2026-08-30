using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace DataForge;

internal static class DataForgeFileWatcher
{
    private const long RecreateRetryDelayTicks = 5 * TimeSpan.TicksPerSecond;
    private static readonly object RecreateLock = new();
    private static readonly Dictionary<string, DebouncedAction> PendingRecreates = new(StringComparer.Ordinal);

    internal sealed class DebouncedAction : IDisposable
    {
        private readonly object _lock = new();
        private readonly Action _action;
        private readonly System.Timers.Timer _timer;
        private bool _disposed;

        internal DebouncedAction(long delayTicks, Action action)
        {
            _action = action;
            _timer = new System.Timers.Timer(Math.Max(1d, TimeSpan.FromTicks(delayTicks).TotalMilliseconds))
            {
                AutoReset = false,
                SynchronizingObject = ThreadingHelper.SynchronizingObject
            };
            _timer.Elapsed += OnElapsed;
        }

        internal void Schedule()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _timer.Stop();
                _timer.Start();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _timer.Stop();
                _timer.Elapsed -= OnElapsed;
                _timer.Dispose();
            }
        }

        private void OnElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
            }

            _action();
        }
    }

    internal static FileSystemWatcher Create(
        string directory,
        string filter,
        bool includeSubdirectories,
        FileSystemEventHandler handler,
        ErrorEventHandler? errorHandler = null)
    {
        Directory.CreateDirectory(directory);
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory, filter)
            {
                IncludeSubdirectories = includeSubdirectories,
                SynchronizingObject = ThreadingHelper.SynchronizingObject
            };
            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Renamed += (sender, args) => handler(sender, args);
            if (errorHandler != null)
            {
                watcher.Error += errorHandler;
            }

            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            watcher?.Dispose();
            throw;
        }
    }

    internal static DebouncedAction CreateDebouncedAction(long delayTicks, Action action) =>
        new(delayTicks, action);

    internal static bool TryRecreate(string name, Action recreate)
    {
        try
        {
            recreate();
            CancelPendingRecreate(name);
            return true;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"Could not recreate the {name} file watcher: {ex.Message}");
            ScheduleRecreate(name, recreate);
            return false;
        }
    }

    internal static void CancelPendingRecreates()
    {
        List<DebouncedAction> pending;
        lock (RecreateLock)
        {
            pending = new List<DebouncedAction>(PendingRecreates.Values);
            PendingRecreates.Clear();
        }

        foreach (DebouncedAction retry in pending)
        {
            retry.Dispose();
        }
    }

    private static void ScheduleRecreate(string name, Action recreate)
    {
        DebouncedAction retry;
        lock (RecreateLock)
        {
            if (!PendingRecreates.TryGetValue(name, out retry!))
            {
                retry = null!;
                retry = new DebouncedAction(
                    RecreateRetryDelayTicks,
                    () => RetryRecreate(name, recreate, retry));
                PendingRecreates[name] = retry;
            }
        }

        retry.Schedule();
    }

    private static void RetryRecreate(string name, Action recreate, DebouncedAction retry)
    {
        try
        {
            recreate();
        }
        catch
        {
            retry.Schedule();
            return;
        }

        lock (RecreateLock)
        {
            if (PendingRecreates.TryGetValue(name, out DebouncedAction? current) &&
                ReferenceEquals(current, retry))
            {
                PendingRecreates.Remove(name);
            }
        }

        retry.Dispose();
        DataForgePlugin.Log.LogDebug($"Recreated the {name} file watcher after a retry.");
    }

    internal static void CancelPendingRecreate(string name)
    {
        DebouncedAction? retry = null;
        lock (RecreateLock)
        {
            if (PendingRecreates.TryGetValue(name, out retry))
            {
                PendingRecreates.Remove(name);
            }
        }

        retry?.Dispose();
    }
}
