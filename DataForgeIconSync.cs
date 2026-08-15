using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using ServerSync;

namespace DataForge;

internal static class DataForgeIconSync
{
    private const string SyncedManifestKey = "icon_manifest";
    private const string RequestRpcName = DataForgePlugin.ModGUID + ".IconRequest";
    private const string ChunkRpcName = DataForgePlugin.ModGUID + ".IconChunk";
    private const int ManifestPriority = 100;
    private const int ChunkBytes = 64 * 1024;
    private const int MaxRequestedHashes = 16;
    private const int MaxQueuedTransfersPerPeer = 32;
    private const int MaxChunksPerUpdate = 4;
    private const int MaxRemoteInstallAttempts = 3;
    private const int MaxRemoteRequestAttempts = 8;
    private const int MaxSendQueueBytes = 20_000;
    private const int MaxScheduledBytesPerMinute = 8 * 1024 * 1024;
    private const int MaxActivatedRemoteIconsPerWorld = DataForgeIconProtocol.MaxIconCount * 2;
    private const long MaxActivatedRemotePixelsPerWorld = DataForgeIconProtocol.MaxTotalPixels * 2;
    private const long MaxDiskCacheBytes = 64L * 1024L * 1024L;
    private static readonly TimeSpan RequestRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IncomingTransferTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OutgoingTransferTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ManifestRefreshDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClientApplyDelay = TimeSpan.FromMilliseconds(150);

    private static readonly Dictionary<string, ServerIconAsset> ServerAssetsByHash =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<ZRpc, PeerOutboundState> OutboundByPeer = new();
    private static readonly Dictionary<string, DataForgeIconManifestEntry> ActiveRemoteEntriesByName =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DataForgeIconManifestEntry> PendingRemoteEntriesByHash =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MissingRemoteHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> RequestedRemoteHashes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IncomingTransfer> IncomingByHash =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> RemoteInstallFailuresByHash =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> RemoteRequestAttemptsByHash =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PendingChangedNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ActivatedRemoteHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ActivationLimitWarnings = new(StringComparer.OrdinalIgnoreCase);

    private static CustomSyncedValue<string>? SyncedManifest;
    private static DataForgeFileWatcher.DebouncedAction? ManifestRefreshDebouncer;
    private static DataForgeIconManifest? ServerManifest;
    private static DataForgeIconManifest? PendingRemoteManifest;
    private static bool ClientApplyPending;
    private static bool RemoteManifestReceived;
    private static bool RemoteManifestAuthorityObserved;
    private static DateTime ClientApplyNotBeforeUtc;
    private static string? CurrentRemoteRevision;
    private static long ActivatedRemotePixels;

    private static string CacheDirectory =>
        Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName, "cache", "server-icons");

    internal static void Initialize(ConfigSync configSync)
    {
        SyncedManifest = new CustomSyncedValue<string>(configSync, SyncedManifestKey, "", ManifestPriority);
        SyncedManifest.ValueChanged += OnSyncedManifestChanged;
        ManifestRefreshDebouncer = DataForgeFileWatcher.CreateDebouncedAction(
            ManifestRefreshDelay.Ticks,
            PublishManifestNow);
    }

    internal static void Dispose()
    {
        if (SyncedManifest != null)
        {
            SyncedManifest.ValueChanged -= OnSyncedManifestChanged;
        }

        ManifestRefreshDebouncer?.Dispose();
        ManifestRefreshDebouncer = null;
        SyncedManifest = null;
        ServerManifest = null;
        ServerAssetsByHash.Clear();
        ResetNetworkState();
        ResetActivatedRemoteIconBudget();
    }

    internal static void ScheduleManifestRefresh()
    {
        if (DataForgePlugin.UsesLocalAuthorityFiles)
        {
            ManifestRefreshDebouncer?.Schedule();
        }
    }

    internal static void OnSourceOfTruthChanged()
    {
        if (DataForgePlugin.UsesLocalAuthorityFiles)
        {
            ScheduleManifestRefresh();
        }
        else
        {
            ServerManifest = null;
            ServerAssetsByHash.Clear();
        }
    }

    internal static void OnNewConnection(ZNet net, ZNetPeer peer)
    {
        if (peer?.m_rpc == null)
        {
            return;
        }

        peer.m_rpc.Register<ZPackage>(RequestRpcName, OnIconRequestReceived);
        peer.m_rpc.Register<ZPackage>(ChunkRpcName, OnIconChunkReceived);
        if (!net.IsServer() && peer.m_server)
        {
            ResetRemoteSession();
        }
    }

    internal static void OnPeerDisconnected(ZNetPeer peer)
    {
        if (peer?.m_rpc == null)
        {
            return;
        }

        OutboundByPeer.Remove(peer.m_rpc);
        if (peer.m_server)
        {
            ResetRemoteSession();
        }
    }

    internal static void OnWorldShutdown()
    {
        ResetRemoteSession();
    }

    internal static void Update()
    {
        ZNet? net = ZNet.instance;
        if (net != null && net.IsServer())
        {
            ProcessOutboundTransfers(net);
        }
        else
        {
            ExpireIncomingTransfers();
            RequestMissingIcons();
        }

        if (ClientApplyPending &&
            DateTime.UtcNow >= ClientApplyNotBeforeUtc &&
            DataForgeWorldLifecycle.IsGameStarted &&
            !DataForgeWorldLifecycle.IsShuttingDown)
        {
            NotifySyncedIconsChanged();
        }
    }

    internal static bool TryResolveRemoteIconPath(string iconName, out string path)
    {
        path = "";
        if (!DataForgePlugin.IsRemoteServerClient ||
            !DataForgeIconProtocol.TryNormalizeLogicalName(iconName, out string logicalName, out _) ||
            !ActiveRemoteEntriesByName.TryGetValue(logicalName, out DataForgeIconManifestEntry? entry))
        {
            return false;
        }

        string cachePath = GetCachePath(entry.Hash);
        if (!File.Exists(cachePath))
        {
            return false;
        }

        if (!ActivatedRemoteHashes.Contains(entry.Hash))
        {
            if (ActivatedRemoteHashes.Count >= MaxActivatedRemoteIconsPerWorld ||
                ActivatedRemotePixels + entry.PixelCount > MaxActivatedRemotePixelsPerWorld)
            {
                if (ActivationLimitWarnings.Add(entry.Hash))
                {
                    DataForgePlugin.Log.LogWarning(
                        $"Synchronized icon '{entry.LogicalName}' was cached but not loaded because the per-world " +
                        $"texture budget ({MaxActivatedRemoteIconsPerWorld} unique files or " +
                        $"{MaxActivatedRemotePixelsPerWorld} pixels) was reached. " +
                        "It can be loaded after the next clean world transition.");
                }

                return false;
            }

            ActivatedRemoteHashes.Add(entry.Hash);
            ActivatedRemotePixels += entry.PixelCount;
        }

        path = cachePath;
        return true;
    }

    internal static void OnIconResourcesReleased()
    {
        ResetActivatedRemoteIconBudget();
    }

    internal static bool HasRemoteManifestAuthority => RemoteManifestAuthorityObserved;

    internal static bool ContainsLogicalIconName(
        ISet<string> normalizedNames,
        string? iconName,
        bool excludeAuto)
    {
        return !string.IsNullOrWhiteSpace(iconName) &&
               (!excludeAuto || !ItemVisualOverrides.IsAutoIconValue(iconName)) &&
               DataForgeIconProtocol.TryNormalizeLogicalName(iconName, out string logicalName, out _) &&
               normalizedNames.Contains(logicalName);
    }

    private static void PublishManifestNow()
    {
        if (!DataForgePlugin.UsesLocalAuthorityFiles)
        {
            return;
        }

        try
        {
            BuildServerManifest(out DataForgeIconManifest manifest, out Dictionary<string, ServerIconAsset> assets);
            bool revisionChanged = ServerManifest == null ||
                                   !string.Equals(ServerManifest.Revision, manifest.Revision, StringComparison.OrdinalIgnoreCase);
            string payload = DataForgeIconProtocol.SerializeManifest(manifest);
            DataForgeSync.PublishPayload(SyncedManifest, "icon manifest", payload);
            ServerManifest = manifest;
            ServerAssetsByHash.Clear();
            foreach (KeyValuePair<string, ServerIconAsset> pair in assets)
            {
                ServerAssetsByHash[pair.Key] = pair.Value;
            }

            if (revisionChanged)
            {
                foreach (PeerOutboundState state in OutboundByPeer.Values)
                {
                    state.CancelTransfers();
                }
                DataForgePlugin.Log.LogInfo(
                    $"Prepared synchronized icon manifest {manifest.Revision} " +
                    $"({manifest.Entries.Count} name(s), {manifest.UniqueContentCount} unique file(s), {manifest.TotalBytes} bytes).");
            }
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"Could not publish the icon manifest; keeping the last-known-good manifest: {ex}");
        }
    }

    private static void BuildServerManifest(
        out DataForgeIconManifest manifest,
        out Dictionary<string, ServerIconAsset> assets)
    {
        HashSet<string> referencedNames = new(StringComparer.Ordinal);
        ItemOverrideManager.CollectReferencedExplicitIconNames(referencedNames);
        PieceOverrideManager.CollectReferencedExplicitIconNames(referencedNames);
        StatusEffectOverrideManager.CollectReferencedExplicitIconNames(referencedNames);

        string iconRoot = Path.GetFullPath(ItemVisualOverrides.IconDirectory);
        Dictionary<string, string> normalizedSources = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ServerIconAsset> stagedAssets = new(StringComparer.OrdinalIgnoreCase);
        List<DataForgeIconManifestEntry> entries = new();
        int totalBytes = 0;
        long totalPixels = 0;

        foreach (string configuredName in referencedNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!DataForgeIconProtocol.TryNormalizeLogicalName(configuredName, out string logicalName, out string nameError))
            {
                DataForgePlugin.Log.LogWarning($"Icon '{configuredName}' is not eligible for server sync: {nameError}");
                continue;
            }

            if (normalizedSources.TryGetValue(logicalName, out string? previousName))
            {
                if (!string.Equals(previousName, configuredName, StringComparison.Ordinal))
                {
                    DataForgePlugin.Log.LogWarning(
                        $"Icon names '{previousName}' and '{configuredName}' resolve to the same synchronized name '{logicalName}'.");
                }

                continue;
            }

            if (entries.Count >= DataForgeIconProtocol.MaxIconCount)
            {
                DataForgePlugin.Log.LogWarning(
                    $"Icon '{configuredName}' was not synchronized because the {DataForgeIconProtocol.MaxIconCount}-icon limit was reached.");
                continue;
            }

            try
            {
                normalizedSources[logicalName] = configuredName;
                string configuredRelativePath = configuredName.Trim()
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                if (!Path.HasExtension(configuredRelativePath))
                {
                    configuredRelativePath += ".png";
                }

                string sourcePath = Path.GetFullPath(Path.Combine(
                    iconRoot,
                    configuredRelativePath));
                if (!sourcePath.StartsWith(iconRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(sourcePath))
                {
                    DataForgePlugin.Log.LogWarning(
                        $"Synchronized icon '{configuredName}' was not found under '{ItemVisualOverrides.IconDirectory}'.");
                    continue;
                }

                FileInfo fileInfo = new(sourcePath);
                if (fileInfo.Length <= 0 || fileInfo.Length > DataForgeIconProtocol.MaxIconBytes)
                {
                    DataForgePlugin.Log.LogWarning(
                        $"Synchronized icon '{configuredName}' is {fileInfo.Length} bytes; the allowed range is 1-{DataForgeIconProtocol.MaxIconBytes} bytes.");
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(sourcePath);
                if (!DataForgeIconProtocol.TryReadPngInfo(bytes, out int width, out int height, out string pngError))
                {
                    DataForgePlugin.Log.LogWarning($"Synchronized icon '{configuredName}' was rejected: {pngError}");
                    continue;
                }

                string hash = DataForgeIconProtocol.ComputeSha256(bytes);
                bool isNewContent = !stagedAssets.ContainsKey(hash);
                if (isNewContent &&
                    (totalBytes + bytes.Length > DataForgeIconProtocol.MaxTotalBytes ||
                     totalPixels + (long)width * height > DataForgeIconProtocol.MaxTotalPixels))
                {
                    DataForgePlugin.Log.LogWarning(
                        $"Synchronized icon '{configuredName}' was omitted because the aggregate icon cache limit was reached.");
                    continue;
                }

                DataForgeIconManifestEntry entry = new(logicalName, hash, bytes.Length, width, height);
                entries.Add(entry);
                if (isNewContent)
                {
                    stagedAssets[hash] = new ServerIconAsset(entry, bytes);
                    totalBytes += bytes.Length;
                    totalPixels += (long)width * height;
                }
            }
            catch (Exception ex)
            {
                DataForgePlugin.Log.LogWarning(
                    $"Synchronized icon '{configuredName}' could not be read and was omitted: {ex.Message}");
            }
        }

        if (!DataForgeIconProtocol.TryCreateManifest(entries, out manifest, out string manifestError))
        {
            throw new InvalidDataException(manifestError);
        }

        assets = stagedAssets;
    }

    private static void OnSyncedManifestChanged()
    {
        if (DataForgePlugin.UsesLocalAuthorityFiles || !DataForgePlugin.IsRemoteServerClient)
        {
            return;
        }

        string payload = SyncedManifest?.Value ?? "";
        bool firstAuthorityPayload = !RemoteManifestAuthorityObserved && payload.Length > 0;
        RemoteManifestAuthorityObserved |= payload.Length > 0;
        if (!DataForgeIconProtocol.TryParseManifest(payload, out DataForgeIconManifest manifest, out string error))
        {
            if (firstAuthorityPayload)
            {
                AddAllReferencedLogicalNames(PendingChangedNames);
                ScheduleClientApply();
            }

            if (!string.IsNullOrEmpty(payload))
            {
                DataForgePlugin.Log.LogWarning(
                    $"Synced icon manifest was rejected; keeping the last-known-good icon catalog: {error}");
            }

            return;
        }

        if (PendingRemoteManifest != null &&
            string.Equals(PendingRemoteManifest.Revision, manifest.Revision, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(CurrentRemoteRevision, manifest.Revision, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StageRemoteManifest(manifest);
    }

    private static void StageRemoteManifest(DataForgeIconManifest manifest)
    {
        bool firstAuthoritativeManifest = !RemoteManifestReceived;
        RemoteManifestReceived = true;
        CurrentRemoteRevision = manifest.Revision;
        PendingRemoteManifest = manifest;
        PendingRemoteEntriesByHash.Clear();
        MissingRemoteHashes.Clear();
        RequestedRemoteHashes.Clear();
        IncomingByHash.Clear();
        RemoteInstallFailuresByHash.Clear();
        RemoteRequestAttemptsByHash.Clear();

        foreach (DataForgeIconManifestEntry entry in manifest.Entries)
        {
            if (!PendingRemoteEntriesByHash.ContainsKey(entry.Hash))
            {
                PendingRemoteEntriesByHash[entry.Hash] = entry;
            }
        }

        foreach (DataForgeIconManifestEntry entry in PendingRemoteEntriesByHash.Values)
        {
            if (!TryValidateCachedIcon(entry))
            {
                MissingRemoteHashes.Add(entry.Hash);
            }
        }

        if (firstAuthoritativeManifest)
        {
            AddAllReferencedLogicalNames(PendingChangedNames);
            ScheduleClientApply();
        }

        ActivateAvailableRemoteEntries();
        if (MissingRemoteHashes.Count == 0)
        {
            FinishPendingRemoteManifest();
            return;
        }

        DataForgePlugin.Log.LogDebug(
            $"Requesting {MissingRemoteHashes.Count} missing synchronized icon file(s) for manifest {manifest.Revision}.");
    }

    private static void AddAllReferencedLogicalNames(ISet<string> names)
    {
        HashSet<string> configuredNames = new(StringComparer.Ordinal);
        ItemOverrideManager.CollectReferencedExplicitIconNames(configuredNames);
        PieceOverrideManager.CollectReferencedExplicitIconNames(configuredNames);
        StatusEffectOverrideManager.CollectReferencedExplicitIconNames(configuredNames);
        foreach (string configuredName in configuredNames)
        {
            if (DataForgeIconProtocol.TryNormalizeLogicalName(configuredName, out string logicalName, out _))
            {
                names.Add(logicalName);
            }
        }
    }

    private static void RequestMissingIcons()
    {
        if (PendingRemoteManifest == null ||
            MissingRemoteHashes.Count == 0 ||
            !DataForgePlugin.IsRemoteServerClient)
        {
            return;
        }

        ZNet? net = ZNet.instance;
        ZNetPeer? serverPeer = net?.GetServerPeer();
        ZRpc? serverRpc = serverPeer?.m_rpc;
        if (net == null || net.IsServer() || serverPeer == null || !serverPeer.IsReady() ||
            serverRpc == null || !serverRpc.IsConnected())
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        foreach (string hash in MissingRemoteHashes.ToArray())
        {
            if (!RemoteRequestAttemptsByHash.TryGetValue(hash, out int attempts) ||
                attempts < MaxRemoteRequestAttempts ||
                IncomingByHash.ContainsKey(hash) ||
                RequestedRemoteHashes.TryGetValue(hash, out DateTime lastRequestAt) &&
                now - lastRequestAt < RequestRetryDelay ||
                !PendingRemoteEntriesByHash.TryGetValue(hash, out DataForgeIconManifestEntry? failedEntry))
            {
                continue;
            }

            RejectRemoteHash(
                hash,
                failedEntry,
                $"the server did not complete the transfer after {attempts} requests");
            if (PendingRemoteManifest == null)
            {
                return;
            }
        }

        int recentlyRequested = RequestedRemoteHashes.Count(pair => now - pair.Value < RequestRetryDelay);
        int availableSlots = MaxRequestedHashes - recentlyRequested;
        if (availableSlots <= 0)
        {
            return;
        }

        List<string> hashes = MissingRemoteHashes
            .Where(hash => !IncomingByHash.ContainsKey(hash) &&
                           (!RequestedRemoteHashes.TryGetValue(hash, out DateTime requestedAt) ||
                            now - requestedAt >= RequestRetryDelay))
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .Take(availableSlots)
            .ToList();
        if (hashes.Count == 0)
        {
            return;
        }

        ZPackage request = new();
        request.Write(DataForgeIconProtocol.ProtocolVersion);
        WriteHash(request, PendingRemoteManifest.Revision);
        request.Write(hashes.Count);
        foreach (string hash in hashes)
        {
            WriteHash(request, hash);
            RequestedRemoteHashes[hash] = now;
            RemoteRequestAttemptsByHash[hash] =
                RemoteRequestAttemptsByHash.TryGetValue(hash, out int attempts)
                    ? attempts + 1
                    : 1;
        }

        try
        {
            serverRpc.Invoke(RequestRpcName, request);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not send a synchronized icon request: {ex.Message}");
        }
    }

    private static void OnIconRequestReceived(ZRpc rpc, ZPackage package)
    {
        try
        {
            ZNet? net = ZNet.instance;
            ZNetPeer? peer = net?.GetPeer(rpc);
            if (net == null || !net.IsServer() || peer == null || peer.m_server || !peer.IsReady())
            {
                return;
            }

            if (package.ReadInt() != DataForgeIconProtocol.ProtocolVersion ||
                !TryReadHash(package, out string revision) ||
                ServerManifest == null ||
                !string.Equals(ServerManifest.Revision, revision, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int count = package.ReadInt();
            if (count < 1 || count > MaxRequestedHashes)
            {
                return;
            }

            List<string> requestedHashes = new(count);
            for (int index = 0; index < count; index++)
            {
                if (!TryReadHash(package, out string hash))
                {
                    return;
                }

                if (!requestedHashes.Contains(hash, StringComparer.OrdinalIgnoreCase))
                {
                    requestedHashes.Add(hash);
                }
            }

            QueueRequestedAssets(rpc, revision, requestedHashes);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Rejected malformed icon request: {ex.Message}");
        }
    }

    private static void QueueRequestedAssets(ZRpc rpc, string revision, IEnumerable<string> requestedHashes)
    {
        if (!OutboundByPeer.TryGetValue(rpc, out PeerOutboundState? state))
        {
            state = new PeerOutboundState();
            OutboundByPeer[rpc] = state;
        }

        DateTime now = DateTime.UtcNow;
        state.ResetRateWindowIfNeeded(now);
        foreach (string hash in requestedHashes)
        {
            if (state.QueuedHashes.Count >= MaxQueuedTransfersPerPeer ||
                !ServerAssetsByHash.TryGetValue(hash, out ServerIconAsset? asset) ||
                state.QueuedHashes.Contains(hash))
            {
                continue;
            }

            if (state.ScheduledBytes + asset.Bytes.Length > MaxScheduledBytesPerMinute)
            {
                if (!state.RateLimitWarningLogged)
                {
                    state.RateLimitWarningLogged = true;
                    DataForgePlugin.Log.LogWarning("A client exceeded the synchronized icon transfer rate limit.");
                }
                break;
            }

            state.ScheduledBytes += asset.Bytes.Length;
            state.QueuedHashes.Add(hash);
            state.Transfers.Enqueue(new OutgoingTransfer(revision, asset, now));
        }
    }

    private static void ProcessOutboundTransfers(ZNet net)
    {
        int chunksSent = 0;
        foreach (KeyValuePair<ZRpc, PeerOutboundState> pair in OutboundByPeer.ToArray())
        {
            ZRpc rpc = pair.Key;
            PeerOutboundState state = pair.Value;
            ZNetPeer? peer = net.GetPeer(rpc);
            if (peer == null || !peer.IsReady() || !rpc.IsConnected())
            {
                OutboundByPeer.Remove(rpc);
                continue;
            }

            if (state.Transfers.Count == 0)
            {
                continue;
            }

            OutgoingTransfer transfer = state.Transfers.Peek();
            DateTime now = DateTime.UtcNow;
            transfer.MarkAsHead(now);
            if (now - transfer.LastProgressAt > OutgoingTransferTimeout)
            {
                state.Transfers.Dequeue();
                state.QueuedHashes.Remove(transfer.Asset.Entry.Hash);
                DataForgePlugin.Log.LogWarning("A synchronized icon transfer timed out and was cancelled.");
                continue;
            }

            if (peer.m_socket.GetSendQueueSize() > MaxSendQueueBytes)
            {
                continue;
            }

            try
            {
                SendNextChunk(rpc, transfer);
            }
            catch (Exception ex)
            {
                state.Transfers.Dequeue();
                state.QueuedHashes.Remove(transfer.Asset.Entry.Hash);
                DataForgePlugin.Log.LogDebug($"Could not send a synchronized icon chunk: {ex.Message}");
                continue;
            }

            chunksSent++;
            if (transfer.IsComplete)
            {
                state.Transfers.Dequeue();
                state.QueuedHashes.Remove(transfer.Asset.Entry.Hash);
            }

            if (chunksSent >= MaxChunksPerUpdate)
            {
                break;
            }
        }
    }

    private static void SendNextChunk(ZRpc rpc, OutgoingTransfer transfer)
    {
        int remaining = transfer.Asset.Bytes.Length - transfer.Offset;
        int length = Math.Min(ChunkBytes, remaining);
        byte[] chunk = new byte[length];
        Buffer.BlockCopy(transfer.Asset.Bytes, transfer.Offset, chunk, 0, length);
        int chunkIndex = transfer.Offset / ChunkBytes;
        int chunkCount = (transfer.Asset.Bytes.Length + ChunkBytes - 1) / ChunkBytes;

        ZPackage package = new();
        package.Write(DataForgeIconProtocol.ProtocolVersion);
        WriteHash(package, transfer.Revision);
        WriteHash(package, transfer.Asset.Entry.Hash);
        package.Write(transfer.Asset.Bytes.Length);
        package.Write(chunkIndex);
        package.Write(chunkCount);
        package.Write(chunk);
        rpc.Invoke(ChunkRpcName, package);

        transfer.Offset += length;
        transfer.LastProgressAt = DateTime.UtcNow;
    }

    private static void OnIconChunkReceived(ZRpc rpc, ZPackage package)
    {
        try
        {
            ZNet? net = ZNet.instance;
            ZNetPeer? serverPeer = net?.GetServerPeer();
            if (net == null || net.IsServer() || serverPeer == null ||
                !ReferenceEquals(serverPeer.m_rpc, rpc) || PendingRemoteManifest == null)
            {
                return;
            }

            if (package.ReadInt() != DataForgeIconProtocol.ProtocolVersion ||
                !TryReadHash(package, out string revision) ||
                !string.Equals(PendingRemoteManifest.Revision, revision, StringComparison.OrdinalIgnoreCase) ||
                !TryReadHash(package, out string hash) ||
                !PendingRemoteEntriesByHash.TryGetValue(hash, out DataForgeIconManifestEntry? entry) ||
                !MissingRemoteHashes.Contains(hash))
            {
                return;
            }

            int totalLength = package.ReadInt();
            int chunkIndex = package.ReadInt();
            int chunkCount = package.ReadInt();
            int expectedChunkCount = (entry.ByteLength + ChunkBytes - 1) / ChunkBytes;
            if (totalLength != entry.ByteLength ||
                chunkCount != expectedChunkCount ||
                chunkIndex < 0 ||
                chunkIndex >= chunkCount ||
                !TryReadBoundedBytes(package, ChunkBytes, out byte[] chunk))
            {
                return;
            }

            int expectedLength = Math.Min(ChunkBytes, totalLength - chunkIndex * ChunkBytes);
            if (chunk.Length != expectedLength)
            {
                return;
            }

            if (!IncomingByHash.TryGetValue(hash, out IncomingTransfer? transfer))
            {
                transfer = new IncomingTransfer(entry, chunkCount);
                IncomingByHash[hash] = transfer;
            }

            transfer.AcceptChunk(chunkIndex, chunk);
            if (!transfer.IsComplete)
            {
                return;
            }

            CompleteIncomingTransfer(hash, transfer);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Rejected malformed synchronized icon chunk: {ex.Message}");
        }
    }

    private static void CompleteIncomingTransfer(string hash, IncomingTransfer transfer)
    {
        byte[] bytes = transfer.Bytes;
        IncomingByHash.Remove(hash);
        RequestedRemoteHashes.Remove(hash);

        if (!string.Equals(DataForgeIconProtocol.ComputeSha256(bytes), hash, StringComparison.OrdinalIgnoreCase))
        {
            RetryOrRejectRemoteHash(hash, transfer.Entry, "content hash verification failed");
            return;
        }

        if (!DataForgeIconProtocol.TryReadPngInfo(bytes, out int width, out int height, out string pngError))
        {
            RejectRemoteHash(hash, transfer.Entry, $"PNG validation failed: {pngError}");
            return;
        }

        if (width != transfer.Entry.Width || height != transfer.Entry.Height)
        {
            RejectRemoteHash(
                hash,
                transfer.Entry,
                $"PNG dimensions {width}x{height} did not match manifest dimensions " +
                $"{transfer.Entry.Width}x{transfer.Entry.Height}");
            return;
        }

        if (!ItemVisualOverrides.CanDecodeSynchronizedIcon(bytes, width, height))
        {
            RejectRemoteHash(hash, transfer.Entry, "Unity could not decode the PNG");
            return;
        }

        if (!TryWriteCacheFile(hash, bytes))
        {
            RetryOrRejectRemoteHash(hash, transfer.Entry, "the local cache file could not be written");
            return;
        }

        MissingRemoteHashes.Remove(hash);
        RemoteInstallFailuresByHash.Remove(hash);
        RemoteRequestAttemptsByHash.Remove(hash);
        ActivateReceivedHash(hash);
        if (MissingRemoteHashes.Count == 0)
        {
            FinishPendingRemoteManifest();
        }
    }

    private static void RetryOrRejectRemoteHash(
        string hash,
        DataForgeIconManifestEntry entry,
        string reason)
    {
        int failures = RemoteInstallFailuresByHash.TryGetValue(hash, out int previousFailures)
            ? previousFailures + 1
            : 1;
        RemoteInstallFailuresByHash[hash] = failures;
        if (failures >= MaxRemoteInstallAttempts)
        {
            RejectRemoteHash(hash, entry, $"{reason} after {failures} attempts");
            return;
        }

        DataForgePlugin.Log.LogWarning(
            $"Synchronized icon '{entry.LogicalName}' was not installed because {reason}; " +
            $"retrying ({failures}/{MaxRemoteInstallAttempts}).");
    }

    private static void RejectRemoteHash(
        string hash,
        DataForgeIconManifestEntry entry,
        string reason)
    {
        MissingRemoteHashes.Remove(hash);
        RequestedRemoteHashes.Remove(hash);
        IncomingByHash.Remove(hash);
        RemoteInstallFailuresByHash.Remove(hash);
        RemoteRequestAttemptsByHash.Remove(hash);
        DataForgePlugin.Log.LogWarning(
            $"Synchronized icon '{entry.LogicalName}' was rejected for manifest {CurrentRemoteRevision}: {reason}. " +
            "The previous icon or baseline will remain in use.");
        if (MissingRemoteHashes.Count == 0)
        {
            FinishPendingRemoteManifest();
        }
    }

    private static void ActivateAvailableRemoteEntries()
    {
        if (PendingRemoteManifest == null)
        {
            return;
        }

        Dictionary<string, DataForgeIconManifestEntry> nextEntries = new(StringComparer.OrdinalIgnoreCase);
        foreach (DataForgeIconManifestEntry desiredEntry in PendingRemoteManifest.Entries)
        {
            if (!MissingRemoteHashes.Contains(desiredEntry.Hash))
            {
                nextEntries[desiredEntry.LogicalName] = desiredEntry;
            }
            else if (ActiveRemoteEntriesByName.TryGetValue(
                         desiredEntry.LogicalName,
                         out DataForgeIconManifestEntry? previousEntry) &&
                     File.Exists(GetCachePath(previousEntry.Hash)))
            {
                nextEntries[desiredEntry.LogicalName] = previousEntry;
            }
        }

        foreach (KeyValuePair<string, DataForgeIconManifestEntry> pair in ActiveRemoteEntriesByName)
        {
            if (!nextEntries.TryGetValue(pair.Key, out DataForgeIconManifestEntry? next) ||
                !string.Equals(pair.Value.Hash, next.Hash, StringComparison.OrdinalIgnoreCase))
            {
                PendingChangedNames.Add(pair.Key);
            }
        }

        foreach (KeyValuePair<string, DataForgeIconManifestEntry> pair in nextEntries)
        {
            if (!ActiveRemoteEntriesByName.TryGetValue(pair.Key, out DataForgeIconManifestEntry? previous) ||
                !string.Equals(previous.Hash, pair.Value.Hash, StringComparison.OrdinalIgnoreCase))
            {
                PendingChangedNames.Add(pair.Key);
            }
        }

        ActiveRemoteEntriesByName.Clear();
        foreach (KeyValuePair<string, DataForgeIconManifestEntry> pair in nextEntries)
        {
            ActiveRemoteEntriesByName[pair.Key] = pair.Value;
        }

        ScheduleClientApply();
    }

    private static void ActivateReceivedHash(string hash)
    {
        if (PendingRemoteManifest == null)
        {
            return;
        }

        foreach (DataForgeIconManifestEntry entry in PendingRemoteManifest.Entries)
        {
            if (!string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ActiveRemoteEntriesByName[entry.LogicalName] = entry;
            PendingChangedNames.Add(entry.LogicalName);
        }

        ScheduleClientApply();
    }

    private static void FinishPendingRemoteManifest()
    {
        if (PendingRemoteManifest == null)
        {
            return;
        }

        string revision = PendingRemoteManifest.Revision;
        int entryCount = PendingRemoteManifest.Entries.Count;
        PendingRemoteManifest = null;
        PendingRemoteEntriesByHash.Clear();
        MissingRemoteHashes.Clear();
        RequestedRemoteHashes.Clear();
        IncomingByHash.Clear();
        RemoteInstallFailuresByHash.Clear();
        RemoteRequestAttemptsByHash.Clear();
        PruneDiskCache();
        DataForgePlugin.Log.LogInfo($"Synchronized icon manifest {revision} is ready ({entryCount} icon name(s)).");
    }

    private static void NotifySyncedIconsChanged()
    {
        if (PendingChangedNames.Count == 0)
        {
            ClientApplyPending = false;
            return;
        }

        HashSet<string> changedNames = new(PendingChangedNames, StringComparer.OrdinalIgnoreCase);
        PendingChangedNames.Clear();
        ClientApplyPending = false;
        DataForgeLifecycleStep.Run(
            "item synced-icon apply",
            () => ItemOverrideManager.OnSyncedIconsChanged(changedNames));
        DataForgeLifecycleStep.Run(
            "piece synced-icon apply",
            () => PieceOverrideManager.OnSyncedIconsChanged(changedNames));
        DataForgeLifecycleStep.Run(
            "status-effect synced-icon apply",
            () => StatusEffectOverrideManager.OnSyncedIconsChanged(changedNames));
    }

    private static void ScheduleClientApply()
    {
        if (PendingChangedNames.Count == 0)
        {
            return;
        }

        ClientApplyPending = true;
        ClientApplyNotBeforeUtc = DateTime.UtcNow + ClientApplyDelay;
    }

    private static void ExpireIncomingTransfers()
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<string, IncomingTransfer> pair in IncomingByHash.ToArray())
        {
            if (now - pair.Value.LastProgressAt <= IncomingTransferTimeout)
            {
                continue;
            }

            IncomingByHash.Remove(pair.Key);
            RequestedRemoteHashes.Remove(pair.Key);
        }
    }

    private static bool TryValidateCachedIcon(DataForgeIconManifestEntry entry)
    {
        string path = GetCachePath(entry.Hash);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            FileInfo fileInfo = new(path);
            if (fileInfo.Length != entry.ByteLength)
            {
                TryDeleteCacheFile(path);
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (!string.Equals(DataForgeIconProtocol.ComputeSha256(bytes), entry.Hash, StringComparison.OrdinalIgnoreCase) ||
                !DataForgeIconProtocol.TryReadPngInfo(bytes, out int width, out int height, out _) ||
                width != entry.Width ||
                height != entry.Height ||
                !ItemVisualOverrides.CanDecodeSynchronizedIcon(bytes, width, height))
            {
                TryDeleteCacheFile(path);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not validate synchronized icon cache '{path}': {ex.Message}");
            return false;
        }
    }

    private static bool TryWriteCacheFile(string hash, byte[] bytes)
    {
        string destination = GetCachePath(hash);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(temporary, destination);
            return true;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"Could not store synchronized icon cache '{destination}': {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // Best-effort cleanup of a generated temporary cache file.
            }
        }
    }

    private static void PruneDiskCache()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return;
            }

            DateTime staleTemporaryCutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (FileInfo temporary in new DirectoryInfo(CacheDirectory)
                         .EnumerateFiles("*.tmp", SearchOption.TopDirectoryOnly))
            {
                if (temporary.LastWriteTimeUtc < staleTemporaryCutoff)
                {
                    TryDeleteCacheFile(temporary.FullName);
                }
            }

            FileInfo[] cacheFiles = new DirectoryInfo(CacheDirectory)
                .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .Where(file => DataForgeIconProtocol.IsValidSha256(Path.GetFileNameWithoutExtension(file.Name)))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            long totalBytes = cacheFiles.Sum(file => file.Length);
            if (totalBytes <= MaxDiskCacheBytes)
            {
                return;
            }

            HashSet<string> protectedHashes = new(
                ActiveRemoteEntriesByName.Values.Select(entry => entry.Hash),
                StringComparer.OrdinalIgnoreCase);
            if (PendingRemoteManifest != null)
            {
                protectedHashes.UnionWith(PendingRemoteManifest.Entries.Select(entry => entry.Hash));
            }

            foreach (FileInfo file in cacheFiles.Reverse())
            {
                string hash = Path.GetFileNameWithoutExtension(file.Name);
                if (totalBytes <= MaxDiskCacheBytes || protectedHashes.Contains(hash))
                {
                    continue;
                }

                long length = file.Length;
                TryDeleteCacheFile(file.FullName);
                totalBytes -= length;
            }
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not prune synchronized icon cache: {ex.Message}");
        }
    }

    private static string GetCachePath(string hash)
    {
        if (!DataForgeIconProtocol.IsValidSha256(hash))
        {
            throw new InvalidDataException("Invalid synchronized icon content hash.");
        }

        return Path.Combine(CacheDirectory, hash.ToLowerInvariant() + ".png");
    }

    private static void TryDeleteCacheFile(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetFullPath(CacheDirectory);
            if (fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not delete invalid synchronized icon cache '{path}': {ex.Message}");
        }
    }

    private static void ResetRemoteSession()
    {
        ResetNetworkState();
    }

    private static void ResetActivatedRemoteIconBudget()
    {
        ActivatedRemoteHashes.Clear();
        ActivationLimitWarnings.Clear();
        ActivatedRemotePixels = 0;
    }

    private static void ResetNetworkState()
    {
        OutboundByPeer.Clear();
        PendingRemoteManifest = null;
        PendingRemoteEntriesByHash.Clear();
        MissingRemoteHashes.Clear();
        RequestedRemoteHashes.Clear();
        IncomingByHash.Clear();
        PendingChangedNames.Clear();
        ClientApplyPending = false;
        ClientApplyNotBeforeUtc = default;
        RemoteManifestReceived = false;
        RemoteManifestAuthorityObserved = false;
        CurrentRemoteRevision = null;
        RemoteInstallFailuresByHash.Clear();
        RemoteRequestAttemptsByHash.Clear();
        ActiveRemoteEntriesByName.Clear();
    }

    private static void WriteHash(ZPackage package, string hash)
    {
        if (!DataForgeIconProtocol.IsValidSha256(hash))
        {
            throw new InvalidDataException("Invalid synchronized icon hash.");
        }

        byte[] bytes = new byte[32];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(hash.Substring(index * 2, 2), 16);
        }

        package.Write(bytes);
    }

    private static bool TryReadHash(ZPackage package, out string hash)
    {
        hash = "";
        if (package.Size() - package.GetPos() < sizeof(int))
        {
            return false;
        }

        int length = package.ReadInt();
        if (length != 32 || package.Size() - package.GetPos() < length)
        {
            return false;
        }

        byte[] bytes = package.ReadByteArray(length);
        char[] chars = new char[bytes.Length * 2];
        const string alphabet = "0123456789abcdef";
        for (int index = 0; index < bytes.Length; index++)
        {
            chars[index * 2] = alphabet[bytes[index] >> 4];
            chars[index * 2 + 1] = alphabet[bytes[index] & 0x0f];
        }

        hash = new string(chars);
        return true;
    }

    private static bool TryReadBoundedBytes(ZPackage package, int maxLength, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (package.Size() - package.GetPos() < sizeof(int))
        {
            return false;
        }

        int length = package.ReadInt();
        if (length < 0 || length > maxLength || package.Size() - package.GetPos() < length)
        {
            return false;
        }

        bytes = package.ReadByteArray(length);
        return bytes.Length == length;
    }

    private sealed class ServerIconAsset
    {
        internal ServerIconAsset(DataForgeIconManifestEntry entry, byte[] bytes)
        {
            Entry = entry;
            Bytes = bytes;
        }

        internal DataForgeIconManifestEntry Entry { get; }
        internal byte[] Bytes { get; }
    }

    private sealed class OutgoingTransfer
    {
        internal OutgoingTransfer(string revision, ServerIconAsset asset, DateTime now)
        {
            Revision = revision;
            Asset = asset;
            LastProgressAt = now;
        }

        internal string Revision { get; }
        internal ServerIconAsset Asset { get; }
        internal int Offset { get; set; }
        internal DateTime LastProgressAt { get; set; }
        internal bool IsComplete => Offset >= Asset.Bytes.Length;

        internal void MarkAsHead(DateTime now)
        {
            if (HasBeenHead)
            {
                return;
            }

            HasBeenHead = true;
            LastProgressAt = now;
        }

        private bool HasBeenHead { get; set; }
    }

    private sealed class PeerOutboundState
    {
        internal PeerOutboundState()
        {
            RateWindowStartedAt = DateTime.UtcNow;
        }

        internal Queue<OutgoingTransfer> Transfers { get; } = new();
        internal HashSet<string> QueuedHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal DateTime RateWindowStartedAt { get; private set; }
        internal int ScheduledBytes { get; set; }
        internal bool RateLimitWarningLogged { get; set; }

        internal void CancelTransfers()
        {
            Transfers.Clear();
            QueuedHashes.Clear();
        }

        internal void ResetRateWindowIfNeeded(DateTime now)
        {
            if (now - RateWindowStartedAt < TimeSpan.FromMinutes(1))
            {
                return;
            }

            RateWindowStartedAt = now;
            ScheduledBytes = 0;
            RateLimitWarningLogged = false;
        }
    }

    private sealed class IncomingTransfer
    {
        private readonly bool[] _receivedChunks;
        private int _receivedCount;

        internal IncomingTransfer(DataForgeIconManifestEntry entry, int chunkCount)
        {
            Entry = entry;
            Bytes = new byte[entry.ByteLength];
            _receivedChunks = new bool[chunkCount];
            LastProgressAt = DateTime.UtcNow;
        }

        internal DataForgeIconManifestEntry Entry { get; }
        internal byte[] Bytes { get; }
        internal DateTime LastProgressAt { get; private set; }
        internal bool IsComplete => _receivedCount == _receivedChunks.Length;

        internal void AcceptChunk(int chunkIndex, byte[] chunk)
        {
            if (_receivedChunks[chunkIndex])
            {
                return;
            }

            Buffer.BlockCopy(chunk, 0, Bytes, chunkIndex * ChunkBytes, chunk.Length);
            _receivedChunks[chunkIndex] = true;
            _receivedCount++;
            LastProgressAt = DateTime.UtcNow;
        }
    }
}
