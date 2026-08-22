# Changelog

## 1.2.3

- Coalesced DataForge-configured Hammer pieces into the existing tab with the same displayed name used by non-DataForge pieces, avoiding duplicate tabs created from independently allocated category IDs.
- Re-resolved configured Hammer category identity only after configuration, build-mode, availability, or category changes, while retaining the final post-framework reconciliation without periodic polling.
- Preserved ambiguous external categories instead of merging them arbitrarily and removed only empty category slots inserted by DataForge.

## 1.2.2

- Stabilized configured Hammer categories and tab ordering against later Jotunn and embedded PieceManager refreshes with a scoped, allocation-free final reconciliation pass.
- Preserved external category ownership and labels, repaired missing HUD tab slots, and avoided local custom-category ID collisions with categories already used by piece prefabs.
- Made piece startup and world-transition category restoration deterministic, with bounded availability refresh retries and failure isolation for unrelated configured pieces.

## 1.2.1

- Added server-authoritative PNG synchronization for explicit item, piece, and status-effect icons using content-hash manifests, missing-only transfers, persistent client caching, and live change or removal reapplication.
- Bounded and isolated icon transfer, validation, retry, disk, and texture-memory failures so one bad asset cannot block unrelated definitions or leave clients retrying indefinitely.
- Made fermenter, cooking-station, and smelter resources and conversions validate before commit, preventing unresolved references from leaving partially applied runtime state.
- Hardened file-watcher recovery, source-of-truth setup, world cleanup, recipe validation, acquisition multiplier patches, and generated artifact writes so failures remain scoped and recover live where possible.
- Changed compact item references to emit `toolTier`, including tier `0`, only for axe and pickaxe skill items.

## 1.2.0

- Added stable numbered recipe identities so multiple recipes for the same result, including recipes with identical runtime names, are all written to references and can be overridden independently.
- Made recipe application transactional: unresolved crafting stations or resources keep the safe existing state, and newly added recipes are registered only after complete validation to prevent accidental free crafting.
- Made recipe removal and restoration fully live, refreshed connected clients' crafting UI correctly, isolated per-recipe failures, and reapplied overrides after external mods rebuild recipe objects.

## 1.1.11

- Added top-level item `toolTier` overrides with baseline restoration and live synchronization, while omitting the vanilla minimum tier `0` from compact references.

## 1.1.10

- Deferred server-synced `item:` status-effect icon validation until both effect and item payloads are ready, avoiding transient missing-item warnings while preserving genuine unresolved-reference diagnostics.

## 1.1.9

- Stopped rebuilding ObjectDB item registers during shutdown restoration, avoiding cleanup warnings when Unity has already destroyed a registered prefab.

## 1.1.8

- **Breaking:** Removed item `visual.color` and `visual.emission`. Existing item YAML must remove these keys and select an existing donor with `visual.material` instead.
- Hardened cloned-item and status-effect reloads by ordering clone dependencies, blocking cycles, rebinding dependent references, and deferring network-unsafe clone identity changes until the next world.
- Made startup, shutdown, and world transitions fault-isolated and ownership-safe across overrides, localization, icons, and VNEI refreshes.
- Improved piece reload cleanup and visual ownership so DataForge-managed components are removed only when unused and later visual changes made by other mods are preserved.
- Corrected generated reference defaults, ObjectDB cache invalidation, and station-extension spacing behavior.
- Avoided a HarmonyX type lookup warning when the optional MagicPlugin dependency is not installed.
- Reworked Thunderstore and Nexus packaging to synchronize the source manifest with the DLL version before staging, validating, and atomically promoting release archives.

## 1.1.7

- Replaced the rarely used `extraAmountOnlyOneIngredient` recipe resource value with an exact-quality upgrade field, allowing requirements such as `SurtlingCore: 0, 5, 2`.
- Added automatic reference and full-scaffold detection for exact-quality requirements supplied by compatible recipe frameworks.
- Added the synced `Upgrade Material Scaling` setting with Vanilla, Flat, and Reduced modes while leaving exact-quality and custom-calculated requirements unchanged.

## 1.1.6

- Added `stats.maxStats: health, stamina, eitr` for stackable maximum-stat bonuses on any active status effect, with localized tooltip lines and zero-value reference pruning.
- Added MagicPlugin compatibility that maps native maximum-Eitr and Eitr-regeneration bonuses into `stats.maxStats` and `stats.regenMultiplier`, while preserving native behavior until explicitly overridden.
- Removed the specialized `healthUpgrade` status-effect schema in favor of the generic `stats.maxStats` field.

## 1.1.5

- Hardened server-synced localization across language changes, reconnects, world transitions, and live YAML reloads while preserving later token changes made by other mods.
- Added strict validation and size limits to the versioned localization payload so invalid or excessive data keeps the last-known-good configuration.
- Added source file and line numbers to server-side validation messages for item, recipe, piece, status-effect, and piece-category YAML.

## 1.1.4

- Added whole-category moves between build tools through `pieceCategory.yml`, including optional localized labels and exact-name merging into existing destination tabs.
- Made category moves reversible, hid emptied source tabs, and kept individual `pieces.yml` `pieceTable` assignments as the final priority.
- Improved category YAML validation to support one order/label entry plus multiple source mappings, reject ambiguous conflicts, and document explicit empty sections such as `GB_Parchment_Tool: []`.

## 1.1.3

- Added `pieceCategory.yml` and `pieceCategory.reference.yml` for per-hammer category ordering and localized display labels.
- Improved exact, case-sensitive category discovery so categories added by PieceManager, Jotunn, and other mods keep stable names instead of shifting to numeric tabs.
- Added Homestead compatibility by leaving its category owner-managed, excluding it from DataForge category overrides, and keeping its tab last.

## 1.1.2

- Removed DataForge's built-in startup and lobby profiler now that loading diagnostics are available in the standalone LoadTimeProfiler patcher.
- Delayed reference and full-scaffold generation until the required game databases and piece tables are ready, avoiding premature or repeated generated-artifact work.
- Skipped full baseline scans when an unchanged reference-state cache can be reused.
- Reduced global item stack and weight multiplier overhead by keeping lightweight multiplier baselines separate from full item override baselines.
- Refreshed targeted item, recipe, status-effect, and piece baselines when game database prefab instances are replaced across world transitions.

## 1.1.1

- Improved incremental override reloads so only changed entries are reapplied while invalid YAML and localization payloads keep the last-known-good configuration.
- Restored item, recipe, status effect, piece, PieceTable category, visual, and localization state cleanly across world and dedicated-server transitions.
- Hardened DataForge-created item, recipe, and status-effect ownership so same-name objects supplied by other mods are not removed during cleanup.
- Improved auto-icon cache invalidation, config reload coalescing, component topology refreshes, and first-apply consistency after reconnecting.

## 1.1.0

- Added automatic custom hammer category creation for piece overrides, so entries like `category: Storage` can create usable hammer tabs.
- Reduced effect reference noise by hiding no-op `healthOverTime` and `attackDamage: None` entries.
- Optimized hammer category normalization and consolidated ZNetScene startup handling.

## 1.0.11

- Fixed global item stack and weight multipliers restoring full item baselines on unrelated items.
- Kept stack and weight multiplier application independent so each option only touches its own field.
- Improved compatibility with mods that patch item durability or other shared item fields at runtime.

## 1.0.10

- Fixed item attack health percentage costs being clamped to 1%, which could make BloodMagic weapons consume far less health than intended when applied through DataForge.
- Clarified item attack `cost` comments so the fourth value is documented as a percent value, e.g. `40` means 40%.

## 1.0.9

- Added `visual.scale` for item attach/drop meshes while keeping `icon: auto` snapshots readable.
- Added status-effect icon reuse with `icon: item:ItemPrefabName`.
- Removed item attack `projectile` and `spawnOnTrigger` YAML fields to keep attack overrides focused.

## 1.0.8

- Added item attack projectile overrides for primary and secondary attacks.
- Added full scaffold and reference output for `projectile: prefab, velocity, velocityMin, count, accuracy, accuracyMin`.
- Kept default projectile tuples hidden from compact reference files.

## 1.0.7

- Added `spawnOnTrigger` support for primary and secondary item attacks.
- Improved auto icon cache invalidation with renderer fingerprints and stale cache pruning.
- Fixed auto icon snapshots blending together when multiple icons are generated in the same startup pass.
- Clarified recipe YAML header examples for result amounts and suffixed recipe keys.

## 1.0.6

- Changed DataForge's VNEI compatibility lookup to resolve only the required VNEI types from the VNEI plugin assembly.
- Reduced HarmonyX reflection warnings caused by VNEI's optional EpicLoot compatibility type when EpicLoot is not installed.

## 1.0.5

- Delayed item, recipe, piece, and status-effect override application until the game data is fully ready.
- Improved status-effect VFX/SFX prefab resolution for effects referenced by other status effects.
- Suppressed missing custom icon warnings on headless dedicated servers.
- Improved status-effect clone cleanup and refresh across reloads and world transitions.

## 1.0.4
- Added tooltip lines for status-effect attack damage and skill experience modifiers.
- Added localization defaults and improved missing localization fallback for status-effect tooltips.
- Added piece scale and visual material overrides, plus `stationExtension: None` support.
- Added automatic VNEI reindexing after DataForge item, recipe, and piece changes.
- Improved piece crafting-station component overrides for adding and removing DataForge-managed stations.

## 1.0.3

- Added a client-side hammer highlight for crafting stations and their station extensions.
- Changed the weight multiplier option to apply to all item weights while preserving explicit item YAML `weight` overrides.
- Kept stack multiplier behavior limited to stackable items.

## 1.0.2

- Changed the mod author/GUID to `sighsorry.DataForge`.

## 1.0.1

- Removed dynamic loading of `UnityEngine.ImageConversionModule` from item and status-effect icon loaders.
- Added a static `UnityEngine.ImageConversionModule` build reference so custom icon loading and auto-icon cache PNG export keep working without runtime module probing.
- Improved package validation compatibility for Thunderstore by avoiding dynamic Unity module loading.
- Added status-effect ownership interop support so DataForge-owned status effects can be treated as exclusive by companion mods.
- Improved hammer comfort UI behavior, including same comfort-group highlighting and hidden-piece masking compatibility.
- Documented `stationExtension` add/remove behavior for piece overrides.

## 1.0.0

- Initial public release of DataForge.
