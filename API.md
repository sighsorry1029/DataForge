# DataForge integration API v1

Available in DataForge 1.3.0+. `DataForge.DataForgeApi` is a read-only API for other
mods to invalidate caches after DataForge applies runtime data. It does not grant
permission to edit YAML, register overrides, force reloads, or bypass server rules.
The existing `DataForgeStatusEffectOwnership` API remains unchanged.

## Public surface

| Member | Contract |
| --- | --- |
| `ApiVersion` | Integer contract major version, currently `1`; separate from the mod version. |
| `Changed` | `Action<DataForgeChange>`; deferred and coalesced per domain on the Unity thread. |
| `GetState(domain)` | Immutable current `DataForgeDomainState`, including readiness and revision, even before subscribing. |
| `HasConfiguredOverride(domain, key)` | Whether an enabled configured target was indexed by the last completed apply pass. Not proof that every field resolved successfully. |
| `GetConfiguredOverrideKeys(domain)` | Immutable, deterministic configured-target snapshot. Empty while not ready. |
| `TryGetCloneSource(domain, key, out source)` | Immediate source of an actual DataForge-managed clone. Not the transitive root or arbitrary third-party prefab ancestry. |
| `GetCloneSources(domain)` | Immutable clone-to-source snapshot. Current clone support covers items and status effects. |

All API calls and event subscription changes must occur on the Unity thread.
Returned snapshots may be retained, but are not live collections. No mutable
Unity object or internal configuration entry is exposed. Unknown enum domains
throw `ArgumentOutOfRangeException`; missing query keys return false.

## Domains and keys

`DataForgeDomain`: `Items`, `Recipes`, `Pieces`, `StatusEffects`, `Localization`.

- Items and pieces: prefab names, e.g. `Amber` or `wood_wall`.
- Recipes: the existing public recipe key, including a variant suffix where
  applicable, e.g. `SwordIron;1` and `SwordIron;2`. Do not collapse variants to
  their output item. Created recipes do not imply item-style `cloneFrom` ancestry.
- Status effects: canonical effect names.
- Localization: `$token` keys; `Language` identifies the currently applied
  language. Readiness is also possible in the main menu, independently of a world.

Non-localization lookups use ordinal case-insensitive comparison. Localization
keys use ordinal case-sensitive comparison. Configured-target queries describe
per-entry configuration, not global multipliers or piece-table/category rules.
Those wider changes are covered by full-domain invalidation notifications.
World-pinned item clones may remain queryable after their YAML entry is removed:
the clone query describes actual retained ownership, not merely current config.

## State and changes

`DataForgeDomainState` contains `Domain`, `SessionId`, `Revision`, `IsReady`,
`IsAuthority`, `Language`, and `LastChangeKind`.

`DataForgeChange` exposes the same values (with `Kind`), its `State` snapshot,
`AffectedKeys`, and `FullRefresh`.

- `Applied`: the runtime apply pass and its normal cleanup finished. Includes
  empty configuration and disabled overrides after baseline restoration.
- `Failed`: an apply pass started but did not finish normally. Some runtime data
  may already have changed: invalidate caches and re-read as appropriate, but do
  not treat this as a successful complete configuration. Query metadata is cleared.
- `Reset`: a world/session boundary or plugin teardown invalidated prior state.
  Discard caches from the previous `SessionId`.

`IsReady` is true only for an `Applied` state. An applied pass does **not** promise
that every YAML entry resolved, that warning-skipped entries succeeded, that all
visual/icon jobs finished, or that every other domain/client is ready. Existing
per-entry warnings and deferred-asset behavior remain in force. A YAML parse
failure before application keeps the previous runtime state and emits no fake
successful apply event.

`Revision` increments for local applied/failed passes; a reset creates a new
`SessionId` with revisions zeroed. Neither value is a persistent world ID or a
server-wide synchronized version. Never compare revisions between peers.
`IsAuthority` reports the local DataForge authority at that pass; a server event
does not acknowledge client application. Clients receive their own events when
their synchronized data is applied locally.

`AffectedKeys` is a conservative set of runtime keys to re-read, **not** an exact
old/new field diff. Names can refer to removed entries whose baseline was restored.
`FullRefresh` means the consumer must recheck its whole relevant domain even if
the key list is empty. Global multipliers, clone topology, recipe rebuilds, and
piece categories/layout can require this. An empty key list alone never proves
that no change occurred.

## Event timing and consumer pattern

The plugin collects notifications until its next `Update` and emits at most one
per domain in that dispatch. Repeated keys are merged, full invalidation is sticky,
and the latest state is included. This is **not** an atomic all-domain/network
transaction. Events caused by an event subscriber are deferred to a later dispatch;
subscriber and logger exceptions cannot break another subscriber or data apply.

Subscribe first, then read `GetState` to cover data applied before subscription.
Only mark your own cache dirty inside the event. Rebuild it during your next
update, using actual runtime data and your own authority rules:

```csharp
using DataForge;

private bool itemsDirty;

void Attach()
{
    DataForgeApi.Changed += OnDataForgeChanged;
    itemsDirty = DataForgeApi.GetState(DataForgeDomain.Items).IsReady;
}

void OnDataForgeChanged(DataForgeChange change)
{
    if (change.Domain == DataForgeDomain.Items)
        itemsDirty = true; // Includes Failed/Reset: do not retain stale caches.
}

void Detach()
{
    DataForgeApi.Changed -= OnDataForgeChanged;
}
```

An optional integration can bind the public API by reflection after checking the
plugin GUID `sighsorry.DataForge` and `ApiVersion == 1`. NPCGoesLive uses this
approach, so it does not require or embed the DataForge assembly. A directly typed
consumer must manage its assembly dependency; do not invoke typed API code when
the dependency is absent. Never package a second DataForge DLL inside a consumer.

## Existing status-effect ownership API

`StatusEffectOverridesWillApply`, `StatusEffectOverridesApplied`, and
`HasActiveStatusEffectOverride` retain their previous signatures and timing.
Those synchronous events are an ownership handoff, including a `finally` path;
they are not aliases for the deferred general API and do not guarantee success.

## Verification

Build the current Debug DLL first using the [development checks](README.md#development-checks). From the repository root, pass that DLL explicitly to include the merged-assembly checks:

```powershell
dotnet run --project tests/DataForge.ApiChecks/DataForge.ApiChecks.csproj -c Debug --no-build -- bin/Debug/DataForge.dll
dotnet run --project tests/DataForge.LogicChecks/DataForge.LogicChecks.csproj -c Debug --no-build
```

The API suite checks coalescing, immutable snapshots, failure/reset/recovery,
late-state queries, re-entrancy, subscriber isolation, world generations and the
actual merged DLL's exported types/reflection event binding.

In-game acceptance (not replaced by those tests):

1. Subscribe on a host and a remote client. Edit item value, recipe requirements,
   piece category, effect stats and a translation; verify separate local-domain
   notifications after runtime application, not on file-save/payload receipt.
2. Add/change/remove a value override and set `override: false`; verify restoration
   is included and a subscribed merchant updates without a restart.
3. Test clone creation/removal, including world-pinned item clones, and compare
   configured-target queries with actual clone-source ownership.
4. Toggle an entire override domain off and on, including an empty config. Verify
   readiness, restored values, and metadata refresh.
5. Change language, return to menu, join a second world/server and reconnect.
   Verify new session generations, no stale ready state, and current-language keys.
6. Test malformed YAML (prior runtime state retained) and a runtime apply failure
   (Failed/full invalidation, no false Applied). A throwing consumer must not stop
   other subscribers or DataForge's ordinary apply behavior.
7. Verify async icon/visual changes independently; API v1 does not signal that
   every deferred asset has finished rendering/downloading.
