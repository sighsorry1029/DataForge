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
