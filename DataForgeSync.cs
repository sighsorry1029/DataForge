using System;
using ServerSync;

namespace DataForge;

internal static class DataForgeSync
{
    internal static bool PublishPayload(CustomSyncedValue<string>? syncedPayload, string domainName, string payload)
    {
        if (syncedPayload == null || string.Equals(syncedPayload.Value ?? "", payload, StringComparison.Ordinal))
        {
            return false;
        }

        DataForgePlugin.Log.LogDebug($"Publishing {domainName} payload ({System.Text.Encoding.UTF8.GetByteCount(payload)} bytes).");
        syncedPayload.AssignLocalValue(payload);
        return true;
    }
}
