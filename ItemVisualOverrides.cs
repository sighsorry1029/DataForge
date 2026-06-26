using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.Rendering;
using static DataForge.DataForgeValue;

namespace DataForge;

internal static class ItemVisualOverrides
{
    internal const string AutoIconValue = "auto";
    internal const string DefaultAutoIconRotationValue = "23, 51, 25.8";
    private const string AutoIconRenderRevision = "jotunn-prefab-main-renderers-3";
    private const int AutoIconSize = 128;
    private const int AutoIconLayer = 30;
    private static readonly Vector3 DefaultAutoIconRotation = new(23f, 51f, 25.8f);

    private static readonly Dictionary<string, List<RendererMaterialSnapshot>> OriginalMaterials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Sprite[]?> OriginalIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Material>> CreatedMaterials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IconCacheEntry> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Material> MaterialLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MaterialLookupMissCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly MethodInfo? LoadImageMethod = ResolveLoadImageMethod();
    private static readonly MethodInfo? EncodeToPngMethod = typeof(ImageConversion).GetMethod(
        "EncodeToPNG",
        BindingFlags.Public | BindingFlags.Static,
        null,
        new[] { typeof(Texture2D) },
        null);
    private static bool MaterialLookupCacheBuilt;

    private static string ConfigDirectory => Path.Combine(Paths.ConfigPath, DataForgePlugin.ModName);
    internal static string IconDirectory => Path.Combine(ConfigDirectory, "icon");
    private static string AutoIconCacheDirectory => Path.Combine(IconDirectory, "cache");

    internal static void EnsureIconDirectory()
    {
        Directory.CreateDirectory(IconDirectory);
    }

    internal static bool IsIconFile(string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        string iconRoot = Path.GetFullPath(IconDirectory);
        return fullPath.StartsWith(iconRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Apply(ItemDrop itemDrop, ItemOverrideManager.VisualDefinition? visual)
    {
        if (visual == null)
        {
            return;
        }

        string prefabName = GetPrefabName(itemDrop.gameObject);
        bool useAutoIcon = IsAutoIconValue(visual.Icon);
        bool hasExplicitIcon = !useAutoIcon && !string.IsNullOrWhiteSpace(visual.Icon);
        bool hasMaterial = !string.IsNullOrWhiteSpace(visual.Material);
        bool hasColor = TryParseColor(visual.Color, out Color color);
        bool hasEmission = visual.Emission.HasValue;

        if (hasExplicitIcon)
        {
            ApplyVisualIcon(prefabName, itemDrop, visual.Icon);
        }

        if (!hasMaterial && !hasColor && !hasEmission)
        {
            return;
        }

        List<Renderer> renderers = GetItemVisualRenderers(itemDrop.gameObject);
        if (renderers.Count == 0)
        {
            DataForgeLogContext.Warning($"{prefabName} has no item renderers for visual override.");
            return;
        }

        Material? materialOverride = null;
        if (hasMaterial)
        {
            string materialName = visual.Material!.Trim();
            materialOverride = ResolveMaterial(materialName);
            if (materialOverride == null)
            {
                DataForgeLogContext.Warning($"{prefabName} has unknown visual material '{materialName}'. Check z_materials.reference.txt.");
                return;
            }
        }

        StoreOriginalMaterials(prefabName, renderers);
        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.sharedMaterials;
            Material[] updatedMaterials = materials.ToArray();
            for (int index = 0; index < materials.Length; index++)
            {
                Material? sourceMaterial = materialOverride != null ? materialOverride : materials[index];
                if (sourceMaterial is null)
                {
                    continue;
                }

                Material baseMaterial = sourceMaterial;
                if (hasColor || hasEmission)
                {
                    Material instance = new(baseMaterial)
                    {
                        name = $"{NormalizeMaterialName(baseMaterial.name)}_DataForge_{prefabName}"
                    };

                    if (hasColor)
                    {
                        ApplyMaterialColor(instance, color);
                    }

                    if (hasEmission)
                    {
                        ApplyMaterialEmission(instance, Math.Max(0f, visual.Emission!.Value), hasColor ? color : (Color?)null);
                    }

                    TrackCreatedMaterial(prefabName, instance);
                    updatedMaterials[index] = instance;
                }
                else
                {
                    updatedMaterials[index] = baseMaterial;
                }
            }

            renderer.sharedMaterials = updatedMaterials;
        }

        if (useAutoIcon)
        {
            ApplyAutoVisualIcon(prefabName, itemDrop, visual);
        }
    }

    internal static void Restore(string prefabName, ItemDrop itemDrop)
    {
        if (OriginalMaterials.TryGetValue(prefabName, out List<RendererMaterialSnapshot> snapshots))
        {
            foreach (RendererMaterialSnapshot snapshot in snapshots)
            {
                if (snapshot.Renderer != null)
                {
                    snapshot.Renderer.sharedMaterials = snapshot.Materials.ToArray();
                }
            }
        }

        if (CreatedMaterials.TryGetValue(prefabName, out List<Material>? materials))
        {
            foreach (Material material in materials)
            {
                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }
            }

            materials.Clear();
            CreatedMaterials.Remove(prefabName);
        }

        if (OriginalIcons.TryGetValue(prefabName, out Sprite[]? icons))
        {
            itemDrop.m_itemData.m_shared.m_icons = icons?.ToArray();
        }
    }

    private static void ApplyVisualIcon(string prefabName, ItemDrop itemDrop, string? iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return;
        }

        string iconKey = iconName!.Trim();
        Sprite? icon = ResolveIconSprite(iconKey);
        if (icon == null)
        {
            DataForgeLogContext.Warning($"{prefabName} has unknown visual icon '{iconKey}'. Expected a png under DataForge/icon.");
            return;
        }

        StoreOriginalIcons(prefabName, itemDrop.m_itemData.m_shared);
        itemDrop.m_itemData.m_shared.m_icons = new[] { icon };
    }

    private static void ApplyAutoVisualIcon(string prefabName, ItemDrop itemDrop, ItemOverrideManager.VisualDefinition visual)
    {
        if (IsHeadlessGraphics())
        {
            return;
        }

        Vector3 iconRotation = ParseIconRotation(visual.IconRotation, prefabName);
        Sprite? icon = ResolveAutoIconSprite(prefabName, itemDrop, visual, iconRotation);
        if (icon == null)
        {
            DataForgeLogContext.Warning($"{prefabName} could not generate visual.icon auto. Keeping the current icon.");
            return;
        }

        StoreOriginalIcons(prefabName, itemDrop.m_itemData.m_shared);
        itemDrop.m_itemData.m_shared.m_icons = new[] { icon };
    }

    private static Sprite? ResolveAutoIconSprite(
        string prefabName,
        ItemDrop itemDrop,
        ItemOverrideManager.VisualDefinition visual,
        Vector3 iconRotation)
    {
        string cachePath = GetAutoIconCachePath(prefabName, visual, iconRotation);
        if (File.Exists(cachePath))
        {
            return LoadSpriteFromPath(cachePath, $"{prefabName} auto icon");
        }

        Sprite? icon = RenderAutoIconSprite(prefabName, itemDrop, iconRotation);
        if (icon == null)
        {
            return null;
        }

        TryWriteAutoIconCache(cachePath, icon);
        return icon;
    }

    private static Sprite? RenderAutoIconSprite(string prefabName, ItemDrop itemDrop, Vector3 iconRotation)
    {
        GameObject? renderObject = null;
        Camera? camera = null;
        Light? light = null;
        RenderTexture? renderTexture = null;
        RenderTexture? previousActive = RenderTexture.active;

        try
        {
            renderObject = SpawnAutoIconRenderClone(prefabName, itemDrop.gameObject, iconRotation, out List<Renderer> renderers);
            if (renderObject == null || renderers.Count == 0)
            {
                return null;
            }

            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers.Skip(1))
            {
                bounds.Encapsulate(renderer.bounds);
            }

            Vector3 renderSize = bounds.size;
            renderObject.transform.position -= bounds.center;

            camera = new GameObject("DataForge Auto Icon Camera", typeof(Camera)).GetComponent<Camera>();
            camera.backgroundColor = Color.clear;
            camera.clearFlags = CameraClearFlags.Color;
            camera.fieldOfView = 0.5f;
            camera.farClipPlane = 10000000f;
            camera.cullingMask = 1 << AutoIconLayer;
            camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            light = new GameObject("DataForge Auto Icon Light", typeof(Light)).GetComponent<Light>();
            light.transform.position = Vector3.zero;
            light.transform.rotation = Quaternion.Euler(5f, 180f, 5f);
            light.type = LightType.Directional;
            light.cullingMask = 1 << AutoIconLayer;
            light.intensity = 1.3f;

            float framedSize = Mathf.Max(renderSize.x, renderSize.y) + 0.1f;
            float distance = framedSize / Mathf.Tan(camera.fieldOfView * ((float)Math.PI / 180f));
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance <= 0f)
            {
                distance = Mathf.Max(renderSize.x, Mathf.Max(renderSize.y, renderSize.z)) + 1f;
            }

            camera.transform.position = new Vector3(0f, 0f, distance);

            renderTexture = RenderTexture.GetTemporary(AutoIconSize, AutoIconSize, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = renderTexture;
            RenderTexture.active = renderTexture;
            camera.Render();

            Texture2D texture = new(AutoIconSize, AutoIconSize, TextureFormat.RGBA32, mipChain: false)
            {
                name = $"{prefabName}_DataForgeAutoIcon"
            };
            Rect rect = new(0f, 0f, AutoIconSize, AutoIconSize);
            texture.ReadPixels(rect, 0, 0);
            texture.Apply();

            Sprite sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f);
            sprite.name = texture.name;
            return sprite;
        }
        catch (Exception ex)
        {
            DataForgeLogContext.Warning($"{prefabName} visual.icon auto failed: {ex.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (camera != null)
            {
                camera.targetTexture = null;
                UnityEngine.Object.Destroy(camera.gameObject);
            }

            if (light != null)
            {
                UnityEngine.Object.Destroy(light.gameObject);
            }

            if (renderTexture != null)
            {
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            if (renderObject != null)
            {
                UnityEngine.Object.Destroy(renderObject);
            }
        }
    }

    private static GameObject? SpawnAutoIconRenderClone(
        string prefabName,
        GameObject sourcePrefab,
        Vector3 iconRotation,
        out List<Renderer> renderers)
    {
        renderers = new List<Renderer>();
        GameObject? inactiveRoot = null;
        try
        {
            inactiveRoot = new GameObject($"{prefabName}_DataForgeAutoIconRoot");
            inactiveRoot.SetActive(false);

            GameObject renderObject = UnityEngine.Object.Instantiate(sourcePrefab, inactiveRoot.transform, worldPositionStays: false);
            renderObject.name = $"{prefabName}_DataForgeAutoIcon";
            StripAutoIconRenderClone(renderObject);
            renderObject.transform.SetParent(null, worldPositionStays: false);
            UnityEngine.Object.DestroyImmediate(inactiveRoot);
            inactiveRoot = null;

            renderObject.transform.position = Vector3.zero;
            renderObject.transform.rotation = Quaternion.Euler(iconRotation);
            SetLayerRecursive(renderObject, AutoIconLayer);
            renderObject.SetActive(true);

            renderers = SelectAutoIconRenderers(renderObject);
            if (renderers.Count == 0)
            {
                UnityEngine.Object.Destroy(renderObject);
                return null;
            }

            HashSet<Renderer> selected = new(renderers);
            foreach (Renderer renderer in renderObject.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (renderer != null && !selected.Contains(renderer))
                {
                    renderer.enabled = false;
                }
            }

            return renderObject;
        }
        catch
        {
            if (inactiveRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(inactiveRoot);
            }

            throw;
        }
    }

    private static void StripAutoIconRenderClone(GameObject renderObject)
    {
        foreach (Transform transform in renderObject.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            Component[] components = transform.GetComponents<Component>();
            for (int index = components.Length - 1; index >= 0; index--)
            {
                Component component = components[index];
                if (component == null || IsAutoIconRenderComponent(component))
                {
                    continue;
                }

                try
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }
                catch (Exception ex)
                {
                    DataForgePlugin.Log.LogDebug($"Could not strip auto icon component '{component.GetType().Name}' from '{renderObject.name}': {ex.Message}");
                }
            }
        }
    }

    private static bool IsAutoIconRenderComponent(Component component) =>
        component is Transform ||
        component is MeshFilter ||
        component is MeshRenderer ||
        component is SkinnedMeshRenderer;

    private static List<Renderer> SelectAutoIconRenderers(GameObject renderObject)
    {
        List<Renderer> allRenderers = renderObject
            .GetComponentsInChildren<Renderer>(includeInactive: false)
            .Where(IsRenderableAutoIconRenderer)
            .ToList();
        if (allRenderers.Count <= 1)
        {
            return allRenderers;
        }

        List<Renderer> mainRenderers = allRenderers
            .Where(renderer => !LooksLikeEffectRenderer(renderer))
            .ToList();

        return mainRenderers.Count > 0 ? mainRenderers : allRenderers;
    }

    private static bool LooksLikeEffectRenderer(Renderer renderer)
    {
        string path = GetTransformPath(renderer.transform).ToLowerInvariant();
        return path.Contains("flare") ||
               path.Contains("glow") ||
               path.Contains("vfx") ||
               path.Contains("fx_") ||
               path.Contains("_fx") ||
               path.Contains("particle") ||
               path.Contains("spark") ||
               path.Contains("light") ||
               path.Contains("smoke") ||
               path.Contains("mist") ||
               path.Contains("aura") ||
               path.Contains("beam");
    }

    private static string GetTransformPath(Transform transform)
    {
        List<string> parts = new();
        Transform? current = transform;
        while (current != null)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static Sprite? ResolveIconSprite(string iconName)
    {
        string? iconPath = ResolveIconPath(iconName);
        if (iconPath == null || !File.Exists(iconPath))
        {
            return null;
        }

        return LoadSpriteFromPath(iconPath, iconName);
    }

    private static Sprite? LoadSpriteFromPath(string iconPath, string iconName)
    {
        DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(iconPath);
        if (IconCache.TryGetValue(iconPath, out IconCacheEntry? cached) &&
            cached.LastWriteTimeUtc == lastWriteTimeUtc)
        {
            return cached.Sprite;
        }

        try
        {
            Texture2D texture = new(2, 2, TextureFormat.RGBA32, mipChain: false)
            {
                name = Path.GetFileNameWithoutExtension(iconPath)
            };

            if (!TryLoadImage(texture, File.ReadAllBytes(iconPath)))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            sprite.name = Path.GetFileNameWithoutExtension(iconPath);
            IconCache[iconPath] = new IconCacheEntry(lastWriteTimeUtc, sprite);
            return sprite;
        }
        catch (Exception ex)
        {
            DataForgeLogContext.Warning($"Could not load visual icon '{iconName}' from '{iconPath}': {ex.Message}");
            return null;
        }
    }

    private static void TryWriteAutoIconCache(string cachePath, Sprite icon)
    {
        try
        {
            if (icon.texture == null || !TryEncodePng(icon.texture, out byte[] png))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(cachePath, png);
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogDebug($"Could not write auto icon cache '{cachePath}': {ex.Message}");
        }
    }

    private static bool TryLoadImage(Texture2D texture, byte[] data)
    {
        if (LoadImageMethod == null)
        {
            DataForgeLogContext.Warning("Could not locate UnityEngine.ImageConversion.LoadImage for visual.icon.");
            return false;
        }

        object?[] args = LoadImageMethod.GetParameters().Length == 3
            ? new object?[] { texture, data, false }
            : new object?[] { texture, data };
        return LoadImageMethod.Invoke(null, args) is bool loaded && loaded;
    }

    private static bool TryEncodePng(Texture2D texture, out byte[] png)
    {
        png = Array.Empty<byte>();
        if (EncodeToPngMethod == null)
        {
            return false;
        }

        if (EncodeToPngMethod.Invoke(null, new object?[] { texture }) is byte[] encoded && encoded.Length > 0)
        {
            png = encoded;
            return true;
        }

        return false;
    }

    private static MethodInfo? ResolveLoadImageMethod()
    {
        return typeof(ImageConversion).GetMethod(
                   "LoadImage",
                   BindingFlags.Public | BindingFlags.Static,
                   null,
                   new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) },
                   null)
               ?? typeof(ImageConversion).GetMethod(
                   "LoadImage",
                   BindingFlags.Public | BindingFlags.Static,
                   null,
                   new[] { typeof(Texture2D), typeof(byte[]) },
                   null);
    }

    private static string? ResolveIconPath(string iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return null;
        }

        string relativePath = iconName.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (!Path.HasExtension(relativePath))
        {
            relativePath += ".png";
        }

        string fullPath = Path.GetFullPath(Path.Combine(IconDirectory, relativePath));
        string iconRoot = Path.GetFullPath(IconDirectory);
        if (!fullPath.StartsWith(iconRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    private static string GetAutoIconCachePath(
        string prefabName,
        ItemOverrideManager.VisualDefinition visual,
        Vector3 iconRotation)
    {
        string fingerprint = string.Join("|", new[]
        {
            DataForgePlugin.ModVersion,
            AutoIconRenderRevision,
            prefabName,
            visual.Material?.Trim() ?? "",
            visual.Color?.Trim() ?? "",
            visual.Emission?.ToString(CultureInfo.InvariantCulture) ?? "",
            FormatVector3(iconRotation),
            AutoIconSize.ToString(CultureInfo.InvariantCulture)
        });
        string hash = fingerprint.GetStableHashCode().ToString("x8", CultureInfo.InvariantCulture);
        return Path.Combine(AutoIconCacheDirectory, $"{SanitizeFileName(prefabName)}-{hash}.png");
    }

    private static string FormatVector3(Vector3 value)
    {
        return string.Join(",", new[]
        {
            value.x.ToString("R", CultureInfo.InvariantCulture),
            value.y.ToString("R", CultureInfo.InvariantCulture),
            value.z.ToString("R", CultureInfo.InvariantCulture)
        });
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static bool IsAutoIconValue(string? iconName)
    {
        string? value = iconName?.Trim();
        if (value == null || value.Length == 0)
        {
            return true;
        }

        return value.Equals(AutoIconValue, StringComparison.OrdinalIgnoreCase);
    }

    private static Vector3 ParseIconRotation(string? value, string prefabName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultAutoIconRotation;
        }

        string[] parts = SplitTuple(value);
        if (parts.Length < 3 ||
            !TryParseIconRotationPart(parts, 0, out float x) ||
            !TryParseIconRotationPart(parts, 1, out float y) ||
            !TryParseIconRotationPart(parts, 2, out float z))
        {
            DataForgeLogContext.Warning($"{prefabName} has invalid visual.iconRotation '{value}'. Expected 'x, y, z'; using {DefaultAutoIconRotationValue}.");
            return DefaultAutoIconRotation;
        }

        return new Vector3(x, y, z);
    }

    private static bool TryParseIconRotationPart(string[] parts, int index, out float parsed)
    {
        parsed = 0f;
        return parts.Length > index &&
               parts[index].Length > 0 &&
               float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool IsHeadlessGraphics()
    {
        return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    }

    private static void SetLayerRecursive(GameObject root, int layer)
    {
        root.layer = layer;
        foreach (Transform child in root.transform)
        {
            SetLayerRecursive(child.gameObject, layer);
        }
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = Color.white;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string colorValue = value!;
        string[] parts = SplitTuple(colorValue);
        if (parts.Length < 3)
        {
            DataForgeLogContext.Warning($"Could not parse visual.color value '{colorValue}'. Expected 'r, g, b, a'.");
            return false;
        }

        if (!TryParseColorPart(parts, 0, colorValue, out float red) ||
            !TryParseColorPart(parts, 1, colorValue, out float green) ||
            !TryParseColorPart(parts, 2, colorValue, out float blue))
        {
            return false;
        }

        float alpha = 1f;
        if (parts.Length > 3 && parts[3].Length > 0 &&
            !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out alpha))
        {
            DataForgeLogContext.Warning($"Could not parse visual.color value '{colorValue}'. Expected numeric alpha.");
            return false;
        }

        color = new Color(
            Mathf.Clamp01(red),
            Mathf.Clamp01(green),
            Mathf.Clamp01(blue),
            Mathf.Clamp01(alpha));
        return true;
    }

    private static bool TryParseColorPart(string[] parts, int index, string originalValue, out float parsed)
    {
        parsed = 0f;
        if (parts.Length <= index || parts[index].Length == 0)
        {
            DataForgeLogContext.Warning($"Could not parse visual.color value '{originalValue}'. Expected 'r, g, b, a'.");
            return false;
        }

        if (float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            return true;
        }

        DataForgeLogContext.Warning($"Could not parse visual.color value '{originalValue}'. Expected numeric RGBA values.");
        return false;
    }

    private static void ApplyMaterialColor(Material material, Color color)
    {
        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
    }

    private static void ApplyMaterialEmission(Material material, float intensity, Color? tint)
    {
        Color baseColor = tint ?? Color.white;
        Color emissionColor = new(baseColor.r * intensity, baseColor.g * intensity, baseColor.b * intensity, 1f);

        if (material.HasProperty("_EmissionColor"))
        {
            material.SetColor("_EmissionColor", emissionColor);
        }

        if (intensity > 0f)
        {
            material.EnableKeyword("_EMISSION");
        }
        else
        {
            material.DisableKeyword("_EMISSION");
        }
    }

    internal static Material? ResolveMaterial(string materialName)
    {
        string normalizedName = NormalizeMaterialName(materialName);
        if (normalizedName.Length == 0)
        {
            return null;
        }

        if (MaterialLookupMissCache.Contains(normalizedName))
        {
            return null;
        }

        EnsureMaterialLookupCache(force: false);
        if (TryResolveCachedMaterial(materialName, normalizedName, out Material? material))
        {
            return material;
        }

        EnsureMaterialLookupCache(force: true);
        if (TryResolveCachedMaterial(materialName, normalizedName, out material))
        {
            return material;
        }

        MaterialLookupMissCache.Add(normalizedName);
        return null;
    }

    private static bool TryResolveCachedMaterial(string materialName, string normalizedName, out Material? material)
    {
        if (MaterialLookupCache.TryGetValue(materialName, out Material exactMaterial) && exactMaterial != null)
        {
            material = exactMaterial;
            return true;
        }

        if (MaterialLookupCache.TryGetValue(normalizedName, out Material normalizedMaterial) && normalizedMaterial != null)
        {
            material = normalizedMaterial;
            return true;
        }

        material = null;
        return false;
    }

    private static void EnsureMaterialLookupCache(bool force)
    {
        if (MaterialLookupCacheBuilt && !force)
        {
            return;
        }

        MaterialLookupCache.Clear();
        MaterialLookupMissCache.Clear();
        Material[] materials = Resources.FindObjectsOfTypeAll<Material>();
        foreach (Material material in materials)
        {
            if (material == null || string.IsNullOrWhiteSpace(material.name))
            {
                continue;
            }

            if (!MaterialLookupCache.ContainsKey(material.name))
            {
                MaterialLookupCache[material.name] = material;
            }

            string normalizedName = NormalizeMaterialName(material.name);
            if (normalizedName.Length > 0 && !MaterialLookupCache.ContainsKey(normalizedName))
            {
                MaterialLookupCache[normalizedName] = material;
            }
        }

        MaterialLookupCacheBuilt = true;
    }

    private static string NormalizeMaterialName(string name)
    {
        return (name ?? "")
            .Replace("(Instance)", "")
            .Replace("(Clone)", "")
            .Trim();
    }

    private static List<Renderer> GetItemVisualRenderers(GameObject itemPrefab)
    {
        List<Renderer> renderers = new();
        HashSet<Renderer> seen = new();
        AddRenderers(itemPrefab.transform.Find("attach_skin"), renderers, seen);
        AddRenderers(itemPrefab.transform.Find("attach"), renderers, seen);
        AddRenderers(GetDropChild(itemPrefab), renderers, seen);

        if (renderers.Count == 0)
        {
            AddRenderers(itemPrefab.transform, renderers, seen);
        }

        return renderers;
    }

    private static void AddRenderers(Transform? root, List<Renderer> renderers, HashSet<Renderer> seen)
    {
        if (root == null)
        {
            return;
        }

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            if (IsPotentialAutoIconRenderer(renderer) && seen.Add(renderer))
            {
                renderers.Add(renderer);
            }
        }
    }

    private static bool IsPotentialAutoIconRenderer(Renderer? renderer)
    {
        return renderer != null &&
               renderer is not ParticleSystemRenderer &&
               renderer.enabled;
    }

    private static bool IsRenderableAutoIconRenderer(Renderer? renderer)
    {
        return IsPotentialAutoIconRenderer(renderer) &&
               renderer!.gameObject.activeInHierarchy;
    }

    private static Transform? GetDropChild(GameObject itemPrefab)
    {
        for (int index = 0; index < itemPrefab.transform.childCount; index++)
        {
            Transform child = itemPrefab.transform.GetChild(index);
            if (!child.name.Contains("attach"))
            {
                return child;
            }
        }

        return null;
    }

    private static void StoreOriginalMaterials(string prefabName, List<Renderer> renderers)
    {
        if (OriginalMaterials.ContainsKey(prefabName))
        {
            return;
        }

        OriginalMaterials[prefabName] = renderers
            .Select(renderer => new RendererMaterialSnapshot(renderer, renderer.sharedMaterials.ToArray()))
            .ToList();
    }

    private static void StoreOriginalIcons(string prefabName, ItemDrop.ItemData.SharedData shared)
    {
        if (OriginalIcons.ContainsKey(prefabName))
        {
            return;
        }

        OriginalIcons[prefabName] = shared.m_icons?.ToArray();
    }

    private static void TrackCreatedMaterial(string prefabName, Material material)
    {
        if (!CreatedMaterials.TryGetValue(prefabName, out List<Material>? materials))
        {
            materials = new List<Material>();
            CreatedMaterials[prefabName] = materials;
        }

        materials.Add(material);
    }

    private static string GetPrefabName(GameObject gameObject)
    {
        return gameObject.name.Replace("(Clone)", "").Trim();
    }

    private sealed class RendererMaterialSnapshot
    {
        public RendererMaterialSnapshot(Renderer renderer, Material[] materials)
        {
            Renderer = renderer;
            Materials = materials;
        }

        public Renderer Renderer { get; }
        public Material[] Materials { get; }
    }

    private sealed class IconCacheEntry
    {
        public IconCacheEntry(DateTime lastWriteTimeUtc, Sprite sprite)
        {
            LastWriteTimeUtc = lastWriteTimeUtc;
            Sprite = sprite;
        }

        public DateTime LastWriteTimeUtc { get; }
        public Sprite Sprite { get; }
    }
}
