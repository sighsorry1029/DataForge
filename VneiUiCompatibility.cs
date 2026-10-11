using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DataForge;

// All private VNEI UI contracts live here. No VNEI/Jotunn runtime dependency is added.
internal static class VneiUiCompatibility
{
    internal const string PluginGuid = "com.maxsch.valheim.vnei";
    private static Binding? Api;

    internal static void Initialize(Harmony harmony)
    {
        Type? type = DataForgeVneiTypes.Get("VNEI.UI.BaseUI");
        if (type == null) return;
        try
        {
            var binding = new Binding(type.Assembly);
            harmony.Patch(binding.ShowRecipe, prefix: new HarmonyMethod(typeof(VneiUiCompatibility), nameof(BeforeShowRecipe)));
            Api = binding;
        }
        catch (Exception ex)
        {
            DataForgePlugin.Log.LogWarning($"VNEI UI compatibility unavailable: {ex.GetBaseException().Message}");
        }
    }

    // History snapshots and external callers can still hold old Item objects.
    // Resolve at the public display boundary instead of editing Jotunn's undo queue.
    private static bool BeforeShowRecipe(object __instance, ref object __0)
    {
        if (Api == null) return true;
        object? item = Api.Resolve(Api.Name(__0));
        if (item == null)
        {
            Api.ShowSearch.Invoke(__instance, new object[] { false });
            return false;
        }
        __0 = item;
        return true;
    }

    internal static List<Snapshot> Capture()
    {
        Binding api = Api ?? throw new InvalidOperationException("VNEI UI adapter is unavailable; keeping the existing index.");
        var result = new List<Snapshot>();
        foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(api.BaseType))
            if (obj is Component ui && ui != null && ui.gameObject.scene.IsValid())
                result.Add(new Snapshot(api, ui));
        return result;
    }

    // Returns only failed views for a bounded UI-only retry. Never trigger a new index.
    internal static List<Snapshot> Restore(IEnumerable<Snapshot> snapshots)
    {
        var failed = new List<Snapshot>();
        foreach (Snapshot snapshot in snapshots)
        {
            if (DataForgeWorldLifecycle.IsShuttingDown) break;
            try { snapshot.Restore(); }
            catch (Exception ex)
            {
                failed.Add(snapshot);
                DataForgePlugin.Log.LogWarning($"VNEI UI rebind failed: {ex.GetBaseException().Message}");
            }
        }
        return failed;
    }

    internal sealed class Snapshot
    {
        internal Component Target => ui;
        private readonly Binding api;
        private readonly Component ui, search, recipe;
        private readonly object stationList;
        private readonly string? currentItem, station;
        private readonly string[] recent;
        private readonly int searchPage, stationPage;
        private readonly bool searchActive, recipeActive;
        private readonly Vector2[] scrollPositions;

        internal Snapshot(Binding api, Component ui)
        {
            this.api = api;
            this.ui = ui;
            search = (Component)api.Search.GetValue(ui);
            recipe = (Component)api.Recipe.GetValue(ui);
            stationList = api.StationList.GetValue(recipe);
            currentItem = api.Name(api.GetRecipeItem.Invoke(recipe, null));
            station = api.Name(api.ActiveStation.GetValue(stationList));
            recent = ((IEnumerable)api.Recent.GetValue(ui)).Cast<object>().Select(api.Name)
                .Where(name => name != null).Cast<string>().ToArray();
            searchPage = (int)api.SearchPage.GetValue(search);
            stationPage = (int)api.StationPage.GetValue(stationList);
            searchActive = search.gameObject.activeSelf;
            recipeActive = recipe.gameObject.activeSelf;
            scrollPositions = api.Scrolls.Select(field =>
                ((ScrollRect)api.ScrollRect.GetValue(field.GetValue(recipe))).normalizedPosition).ToArray();
        }

        internal void Restore()
        {
            if (ui == null || search == null || recipe == null) return;
            object? item = api.Resolve(currentItem);
            try
            {
                if ((bool)api.SearchInitialized.GetValue(search))
                {
                    IList list = (IList)api.SearchItems.GetValue(search);
                    list.Clear();
                    foreach (object pair in (IEnumerable)api.GetActiveItems.Invoke(null, null))
                        list.Add(api.ListItemConstructor.Invoke(new[] { api.ActiveItemValue.GetValue(pair, null) }));
                    api.SearchPage.SetValue(search, Math.Max(0, searchPage));
                    api.UpdateSearch.Invoke(search, new object[] { true });
                    api.SearchPage.SetValue(search, Math.Min(Math.Max(0, searchPage), (int)api.SearchMaxPages.GetValue(search)));
                }

                if (item != null)
                {
                    api.ActiveStation.SetValue(stationList, api.Resolve(station));
                    api.SetRecipeItem.Invoke(recipe, new[] { item });
                    int perPage = Math.Max(1, (int)api.StationsPerPage.Invoke(stationList, null));
                    int maxPage = Math.Max(0, (((IList)api.StationElements.GetValue(stationList)).Count - 1) / perPage);
                    api.StationPage.SetValue(stationList, Math.Min(Math.Max(0, stationPage), maxPage));
                    api.UpdateStationButtons.Invoke(stationList, null);
                    for (int i = 0; i < api.Scrolls.Length; i++)
                    {
                        object scroll = api.Scrolls[i].GetValue(recipe);
                        ((ScrollRect)api.ScrollRect.GetValue(scroll)).normalizedPosition = scrollPositions[i];
                        api.UpdateHidden.Invoke(scroll, null);
                    }
                }
                else
                {
                    // SetItem(null) is not supported by VNEI. Clear the hidden view
                    // directly so a removed item cannot keep live controls/listeners.
                    api.CurrentRecipeItem.SetValue(recipe, null);
                    foreach (FieldInfo field in api.Scrolls) api.ClearScroll.Invoke(field.GetValue(recipe), null);
                    api.ActiveStation.SetValue(stationList, null);
                    api.ClearStations.Invoke(stationList, null);
                    api.SetDisplayItem.Invoke(api.InfoIcon.GetValue(recipe), new object?[] { null, 1 });
                }

                IList recentItems = (IList)api.Recent.GetValue(ui);
                recentItems.Clear();
                foreach (string name in recent.Distinct(StringComparer.Ordinal))
                {
                    object? remapped = api.Resolve(name);
                    if (remapped != null) recentItems.Add(remapped);
                }
                api.UpdateRecent.Invoke(null, new[] { api.RecentDisplay.GetValue(ui), recentItems });
            }
            finally
            {
                // UpdateSearch switches views, even for an inactive UI.
                recipe.gameObject.SetActive(recipeActive && item != null);
                search.gameObject.SetActive(searchActive || (recipeActive && item == null));
            }
        }
    }

    // Resolve once, before any index is cleared. A changed optional-mod layout fails
    // closed instead of leaving a partially rebound UI. Original DLLs are unmodified.
    internal sealed class Binding
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        internal readonly Type BaseType;
        internal readonly FieldInfo Search, Recipe, Recent, RecentDisplay, SearchItems, SearchInitialized,
            SearchPage, SearchMaxPages, StationList, ActiveStation, StationPage, StationElements,
            ScrollRect, ItemName, CurrentRecipeItem, InfoIcon;
        internal readonly FieldInfo[] Scrolls;
        internal readonly MethodInfo ShowRecipe, ShowSearch, GetRecipeItem, SetRecipeItem, UpdateSearch,
            GetActiveItems, GetItem, StationsPerPage, UpdateStationButtons, UpdateHidden, UpdateRecent,
            ClearScroll, ClearStations, SetDisplayItem;
        internal readonly ConstructorInfo ListItemConstructor;
        internal readonly PropertyInfo ActiveItemValue;

        internal Binding(Assembly assembly)
        {
            Type Type(string name) => assembly.GetType("VNEI." + name, true)!;
            BaseType = Type("UI.BaseUI");
            Type search = Type("UI.SearchUI"), recipe = Type("UI.RecipeUI"), item = Type("Logic.Item"),
                stations = Type("UI.CraftingStationList"), scroll = Type("UI.RecipeScroll"), indexing = Type("Logic.Indexing"),
                display = Type("UI.DisplayItem"), listItem = Type("UI.SearchUI+ListItem");
            Search = Field(BaseType, "searchUi", search);
            Recipe = Field(BaseType, "recipeUi", recipe);
            Recent = Field(BaseType, "lastViewedItems", typeof(List<>).MakeGenericType(item));
            RecentDisplay = Field(BaseType, "lastViewedDisplayItems", typeof(List<>).MakeGenericType(display));
            SearchItems = Field(search, "listItems", typeof(List<>).MakeGenericType(listItem));
            SearchInitialized = Field(search, "hasInit", typeof(bool));
            SearchPage = Field(search, "currentPage", typeof(int));
            SearchMaxPages = Field(search, "maxPages", typeof(int));
            StationList = Field(recipe, "craftingStationList", stations);
            ActiveStation = Field(stations, "activeStation", item);
            StationPage = Field(stations, "currentPage", typeof(int));
            StationElements = Field(stations, "stationElements", typeof(List<>).MakeGenericType(Type("UI.CraftingStationElement")));
            ScrollRect = Field(scroll, "scrollRect", typeof(ScrollRect));
            ItemName = Field(item, "internalName", typeof(string));
            CurrentRecipeItem = Field(recipe, "currentItem", item);
            InfoIcon = Field(recipe, "infoIcon", display);
            Scrolls = new[] { "obtainingScroll", "usingScroll", "bothScroll", "combinedScroll" }
                .Select(name => Field(recipe, name, scroll)).ToArray();
            ShowRecipe = Method(BaseType, "ShowRecipe", typeof(void), item, typeof(bool));
            ShowSearch = Method(BaseType, "ShowSearch", typeof(void), typeof(bool));
            GetRecipeItem = Method(recipe, "GetItem", item);
            SetRecipeItem = Method(recipe, "SetItem", typeof(void), item);
            UpdateSearch = Method(search, "UpdateSearch", typeof(void), typeof(bool));
            GetItem = Method(indexing, "GetItem", item, typeof(string));
            Type activePair = typeof(KeyValuePair<,>).MakeGenericType(typeof(string), item);
            GetActiveItems = Method(indexing, "GetActiveItems", typeof(IEnumerable<>).MakeGenericType(activePair));
            ActiveItemValue = activePair.GetProperty("Value")!;
            StationsPerPage = Method(stations, "GetStationsPerPage", typeof(int));
            UpdateStationButtons = Method(stations, "UpdateButtons", typeof(void));
            UpdateHidden = Method(scroll, "UpdateHidden", typeof(void));
            UpdateRecent = Method(BaseType, "UpdateDisplayItemRow", typeof(void), RecentDisplay.FieldType, Recent.FieldType);
            ClearScroll = Method(scroll, "Clear", typeof(void));
            ClearStations = Method(stations, "ClearStations", typeof(void));
            SetDisplayItem = Method(display, "SetItem", typeof(void), item, typeof(int));
            ListItemConstructor = listItem.GetConstructor(new[] { item }) ?? throw new MissingMethodException(listItem.FullName, ".ctor(Item)");
        }

        internal string? Name(object? item) => item == null ? null : (string?)ItemName.GetValue(item);
        internal object? Resolve(string? name) => name == null ? null : GetItem.Invoke(null, new object[] { name });
        internal static FieldInfo Field(Type type, string name, Type fieldType)
        {
            FieldInfo? field = type.GetField(name, Members);
            if (field == null || field.FieldType != fieldType || field.IsStatic)
                throw new MissingFieldException(type.FullName, name);
            return field;
        }
        internal static MethodInfo Method(Type type, string name, Type result, params Type[] parameters)
        {
            MethodInfo? method = type.GetMethod(name, Members, null, parameters, null);
            if (method == null || method.ReturnType != result) throw new MissingMethodException(type.FullName, name);
            return method;
        }
    }
}
