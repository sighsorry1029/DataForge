using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;

internal static class VneiHistoryChecks
{
    // Exercise the production prefix in a real Harmony detour with the original
    // VNEI Item parameter type, without constructing a Unity UI or starting VNEI.
    internal static void Run(string modPath, string vneiPath)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        string plugins = Directory.GetParent(Path.GetDirectoryName(vneiPath)!)!.FullName;
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            string file = new AssemblyName(request.Name).Name + ".dll";
            foreach (string dir in new[] { Path.GetDirectoryName(vneiPath)!,
                Path.Combine(plugins, "ValheimModding-Jotunn"), Path.Combine(plugins, "ValheimModding-JsonDotNET") })
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        Assembly mod = Assembly.LoadFrom(modPath), vnei = Assembly.LoadFrom(vneiPath);
        Type ui = mod.GetType("DataForge.VneiUiCompatibility", true)!;
        Type itemType = vnei.GetType("VNEI.Logic.Item", true)!;
        object binding = Activator.CreateInstance(ui.GetNestedType("Binding", all)!, all, null, new object[] { vnei }, null)!;
        ui.GetField("Api", all)!.SetValue(null, binding);
        object Item()
        {
            object item = FormatterServices.GetUninitializedObject(itemType);
            GC.SuppressFinalize(item);
            itemType.GetField("internalName")!.SetValue(item, "Input");
            return item;
        }
        object old = Item(), current = Item();
        IDictionary items = (IDictionary)vnei.GetType("VNEI.Logic.Indexing", true)!.GetProperty("Items", all)!.GetValue(null)!;
        items["input"] = current;
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DataForge.VneiHistoryFixture"), AssemblyBuilderAccess.Run);
        TypeBuilder type = assembly.DefineDynamicModule("main").DefineType("HistoryView", TypeAttributes.Public);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        MethodBuilder method = type.DefineMethod("ShowRecipe", MethodAttributes.Public, typeof(object), new[] { itemType, typeof(bool) });
        method.SetImplementationFlags(MethodImplAttributes.NoInlining);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ret);
        Type viewType = type.CreateType()!;
        object view = Activator.CreateInstance(viewType)!;
        MethodInfo target = viewType.GetMethod("ShowRecipe")!;
        object? Call() => target.Invoke(view, new[] { old, (object)false });
        void Assert(bool condition) { if (!condition) throw new InvalidOperationException("VNEI history Harmony argument replacement failed"); }
        Harmony patch = new("DataForge.Tests.VneiHistory");
        try
        {
            Assert(ReferenceEquals(Call(), old));
            patch.Patch(target, prefix: new HarmonyMethod(ui.GetMethod("BeforeShowRecipe", all)!));
            Assert(ReferenceEquals(Call(), current));
            patch.UnpatchSelf();
            Assert(ReferenceEquals(Call(), old));
        }
        finally
        {
            patch.UnpatchSelf();
            ui.GetField("Api", all)!.SetValue(null, null);
            items.Clear();
        }
        Console.WriteLine("VNEI history checks passed: 3 real Harmony detour assertions (not Unity UI execution).");
    }
}
