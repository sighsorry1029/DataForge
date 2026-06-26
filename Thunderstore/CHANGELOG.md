# Changelog

## 1.0.4

- Added automatic VNEI reindexing after DataForge item, recipe, and piece changes.
- Added Korean localization defaults and improved missing localization fallback for status-effect tooltips.
- Added tooltip lines for status-effect attack damage and skill experience modifiers.
- Added piece scale and visual material overrides, plus `stationExtension: None` support.
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
