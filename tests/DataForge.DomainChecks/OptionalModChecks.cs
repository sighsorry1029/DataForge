using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// Original optional DLL contracts and managed recipe graphs only. No Unity objects,
// game/plugin startup, generated fake game assembly or native UI execution.
internal static class OptionalModChecks
{
    internal static void Run(Assembly mod, Assembly game, string cuisinePath, string vneiPath)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        string plugins = Directory.GetParent(Path.GetDirectoryName(vneiPath)!)!.FullName;
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            string file = new AssemblyName(request.Name).Name + ".dll";
            foreach (string dir in new[] { Path.GetDirectoryName(vneiPath)!, Path.GetDirectoryName(cuisinePath)!,
                Path.Combine(plugins, "ValheimModding-Jotunn"), Path.Combine(plugins, "ValheimModding-JsonDotNET") })
            {
                string candidate = Path.Combine(dir, file);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };
        int checks = 0;
        void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); checks++; }
        Assembly cuisine = Assembly.LoadFrom(cuisinePath), vnei = Assembly.LoadFrom(vneiPath);
        Type hook = cuisine.GetType("ValheimCuisine.ValheimCuisinePlugin+ConversionsZNetScene_AwakePost_Patch", true)!;
        MethodInfo? target = hook.GetMethod("ReapplyConversions", all, null, new[] { game.GetType("ZNetScene", true)!, typeof(bool) }, null);
        Assert(target != null && target.IsStatic && target.ReturnType == typeof(void), "Cuisine hook contract changed");
        foreach (string station in new[] { "Fermenter", "CookingStation" })
            Assert(game.GetType(station, true)!.GetField("m_conversion", all)!.IsPublic, station + " conversion access changed");

        Type compatibility = mod.GetType("DataForge.CuisineConversionCompatibility", true)!;
        Type lifecycle = mod.GetType("DataForge.DataForgeWorldLifecycle", true)!;
        void Invoke(string method, params object[] args) => compatibility.GetMethod(method, all)!.Invoke(null, args);
        bool Ready(bool local, bool received) => (bool)compatibility.GetMethod("IsPayloadReady", all)!.Invoke(null, new object[] { local, received })!;
        Invoke("OnAuthorityChanged");
        Assert(Ready(true, false) && !Ready(false, false), "Authority gate conflates local files and absent remote payload");
        Invoke("AcceptPayload", false);
        Assert(!Ready(false, true), "Placeholder/default payload accepted as server input");
        Invoke("AcceptPayload", true); // Includes a valid received empty payload.
        Assert(Ready(false, true) && !Ready(false, false), "Accepted remote payload requires this key's receipt");
        Invoke("OnAuthorityChanged");
        Assert(!Ready(false, true), "New authority reused previous accepted payload");
        Invoke("AcceptPayload", true);
        lifecycle.GetMethod("MarkShuttingDown", all)!.Invoke(null, null);
        Invoke("OnWorldShutdown");
        Invoke("AcceptPayload", true); // ServerSync restores local values during shutdown.
        lifecycle.GetMethod("MarkStarting", all)!.Invoke(null, null);
        Assert(!Ready(false, true), "Shutdown restore event became an accepted payload in the next session");

        Type ui = mod.GetType("DataForge.VneiUiCompatibility", true)!;
        object binding = Activator.CreateInstance(ui.GetNestedType("Binding", all)!, all, null, new object[] { vnei }, null)!;
        Assert(binding != null, "Original VNEI UI reflection preflight failed");
        Type manager = mod.GetType("DataForge.VneiRefreshManager", true)!;
        Type readerType = manager.GetNestedType("ConversionIndex", all)!;
        object reader = Activator.CreateInstance(readerType, all, null, new object[] { vnei }, null)!;
        Type expectation = manager.GetNestedType("ConversionExpectation", all)!;
        Type expectedList = typeof(System.Collections.Generic.List<>).MakeGenericType(expectation);
        IList Expected(string station = "VC_Test", int amount = 6)
        {
            IList list = (IList)Activator.CreateInstance(expectedList)!;
            list.Add(Activator.CreateInstance(expectation, all, null, new object[] { station, "Input", "Output", amount }, null));
            return list;
        }
        int Validate(IList expected, params string[] buildable) => (int)readerType.GetMethod("Validate", all)!.Invoke(reader,
            new object[] { expected, new System.Collections.Generic.HashSet<string>(buildable, StringComparer.Ordinal) })!;
        bool Rejected(IList expected, params string[] buildable)
        {
            try { Validate(expected, buildable); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { return true; }
            return false;
        }
        Type index = vnei.GetType("VNEI.Logic.Indexing", true)!;
        Type itemType = vnei.GetType("VNEI.Logic.Item", true)!;
        Type recipeType = vnei.GetType("VNEI.Logic.RecipeInfo", true)!;
        Type partType = vnei.GetType("VNEI.Logic.Part", true)!;
        Type amountType = vnei.GetType("VNEI.Logic.Amount", true)!;
        Type setType = typeof(System.Collections.Generic.HashSet<>).MakeGenericType(recipeType);
        IDictionary items = (IDictionary)index.GetProperty("Items", all)!.GetValue(null)!;
        IList recipes = (IList)recipeType.GetProperty("Recipes", all)!.GetValue(null)!;
        MethodInfo cleanupName = index.GetMethod("CleanupName", all)!;
        string Key(string name) => (string)cleanupName.Invoke(null, new object[] { name })!;
        object Item(string name)
        {
            object item = RuntimeHelpers.GetUninitializedObject(itemType);
            GC.SuppressFinalize(item); // Its normal ctor/finalizer owns Unity localization events.
            itemType.GetField("internalName")!.SetValue(item, name);
            itemType.GetField("result")!.SetValue(item, Activator.CreateInstance(setType));
            itemType.GetField("ingredient")!.SetValue(item, Activator.CreateInstance(setType));
            return item;
        }
        object stationItem = Item("VC_Test"), input = Item("Input"), output = Item("Output");
        items[Key("VC_Test")] = stationItem;
        items[Key("Input")] = input;
        items[Key("Output")] = output;
        object recipe = Activator.CreateInstance(recipeType)!;
        object Amount(int amount) => Activator.CreateInstance(amountType, new object[] { amount, 1f })!;
        object Part(object item, int count) => Activator.CreateInstance(partType, new[] { item, Amount(count), (object)1 })!;
        ((IList)recipeType.GetProperty("Stations")!.GetValue(recipe)!).Add(Part(stationItem, 1));
        void Group(string name, object item, int count)
        {
            IDictionary dictionary = (IDictionary)recipeType.GetProperty(name)!.GetValue(recipe)!;
            IList parts = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(partType))!;
            parts.Add(Part(item, count));
            dictionary.Add(Amount(1), parts);
        }
        Group("Ingredients", input, 1);
        Group("Results", output, 6);
        object inputs = itemType.GetField("ingredient")!.GetValue(input)!;
        object outputs = itemType.GetField("result")!.GetValue(output)!;
        setType.GetMethod("Add")!.Invoke(inputs, new[] { recipe });
        setType.GetMethod("Add")!.Invoke(outputs, new[] { recipe });
        Assert(Validate(Expected()) == 1, "Valid indexed conversion was rejected");
        Assert(Rejected(Expected(amount: 7)), "Wrong produced amount accepted");
        setType.GetMethod("Clear")!.Invoke(inputs, null);
        Assert(Rejected(Expected()), "Partially registered recipe accepted without input backlink");
        setType.GetMethod("Add")!.Invoke(inputs, new[] { recipe });
        recipes.Clear();
        Assert(Rejected(Expected()), "Nonempty item index incorrectly proves conversion indexing");
        recipes.Add(recipe);
        items.Remove(Key("VC_Test"));
        Assert(Validate(Expected()) == 0, "Unindexed facility must not be reported as verified");
        Assert(Rejected(Expected(), "VC_Test"), "Missing buildable facility was treated as intentional removal");
        items[Key("VC_Test")] = stationItem;
        Assert(Validate((IList)Activator.CreateInstance(expectedList)!) == 0, "Empty expectation rejected");

        ui.GetField("Api", all)!.SetValue(null, binding);
        object stale = Item("Input");
        object[] args = { new object(), stale };
        Assert((bool)ui.GetMethod("BeforeShowRecipe", all)!.Invoke(null, args)! && ReferenceEquals(args[1], input),
            "History input not rebound to the current index");
        // isActive is false: display/history resolution must not invent a visibility policy.
        Assert(!(bool)itemType.GetField("isActive")!.GetValue(input)!, "Fixture unexpectedly active");
        ui.GetField("Api", all)!.SetValue(null, null);
        items.Clear();
        recipes.Clear();
        Console.WriteLine($"Optional-mod checks passed: {checks} (original DLL metadata and managed graphs; not Unity execution).");
    }
}
