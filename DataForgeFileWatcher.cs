using System;
using System.IO;
using BepInEx;

namespace DataForge;

internal static class DataForgeFileWatcher
{
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
        FileSystemEventHandler handler)
    {
        Directory.CreateDirectory(directory);
        FileSystemWatcher watcher = new(directory, filter)
        {
            IncludeSubdirectories = includeSubdirectories,
            SynchronizingObject = ThreadingHelper.SynchronizingObject
        };
        watcher.Changed += handler;
        watcher.Created += handler;
        watcher.Deleted += handler;
        watcher.Renamed += (sender, args) => handler(sender, args);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    internal static DebouncedAction CreateDebouncedAction(long delayTicks, Action action) =>
        new(delayTicks, action);
}
