# Changelog

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
