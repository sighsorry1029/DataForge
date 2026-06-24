# Changelog

## 1.0.1

- Removed dynamic loading of `UnityEngine.ImageConversionModule` from item and status-effect icon loaders.
- Added a static `UnityEngine.ImageConversionModule` build reference so custom icon loading and auto-icon cache PNG export keep working without runtime module probing.
- Improved package validation compatibility for Thunderstore by avoiding dynamic Unity module loading.
- Added status-effect ownership interop support so DataForge-owned status effects can be treated as exclusive by companion mods.
- Improved hammer comfort UI behavior, including same comfort-group highlighting and hidden-piece masking compatibility.
- Documented `stationExtension` add/remove behavior for piece overrides.

## 1.0.0

- Initial public release of DataForge.
- Added compact YAML override support for items, recipes, pieces, and status effects.
- Added generated reference files with default-value trimming and owner-based sections.
- Added on-demand full scaffold generation through `dataforge:full`.
- Added item cloning, visual material/color/emission overrides, custom icons, and auto icon rendering.
- Added recipe editing, removal, multiple-recipe keys, one-of ingredient support, and quality output bonus support.
- Added piece editing for build placement, sorting, resources, health, comfort, and selected station/container/production components.
- Added status effect editing and cloning with compact stat, skill, modifier, and effect-prefab fields.
- Added server-synced localization files.
- Added material and resource-map reference helpers.
- Added ServerSync support for override payloads and source-of-truth configuration.
- Added modpack helpers for comfort badges, station extension spacing, fireplace fuel overflow, startup profiling, and common prefab/piece-table guards.
