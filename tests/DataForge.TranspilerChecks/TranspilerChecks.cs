using System.Reflection;
using System.Reflection.Emit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;

internal static class TranspilerChecks
{
    internal static void Run(string modPath, string managed, string core)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            var name = new AssemblyName(request.Name);
            foreach (string directory in new[] { Path.GetDirectoryName(modPath)!, managed, core })
            {
                string candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(Path.GetFullPath(candidate));
            }
            return null;
        };
        Assembly game = Assembly.LoadFrom(Path.GetFullPath(Path.Combine(managed, "assembly_valheim.dll")));
        Assembly mod = Assembly.LoadFrom(Path.GetFullPath(modPath));
        int checks = 0;
        void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        List<CodeInstruction> Apply(string patchType, List<CodeInstruction> input)
        {
            var transpiler = mod.GetType("DataForge." + patchType, true)!.GetMethod("Transpiler", flags)!;
            return ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { input })!).ToList();
        }
        MethodInfo Target(string type, string name) => game.GetType(type, true)!.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        List<CodeInstruction> Original(string type, string name)
        {
            MethodInfo method = Target(type, name);
            // Read original IL with Cecil without initializing Unity or unrelated platform types.
            // Only hook-call operands need reflection resolution; all other instructions are retained symbolically.
            using var module = Mono.Cecil.ModuleDefinition.ReadModule(method.Module.FullyQualifiedName);
            var definition = (Mono.Cecil.MethodDefinition)module.LookupToken(method.MetadataToken);
            var generator = new DynamicMethod("ReadOriginalIL", typeof(void), Type.EmptyTypes).GetILGenerator();
            var labels = definition.Body.Instructions.ToDictionary(i => i, _ => generator.DefineLabel());
            var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);
            var result = new List<CodeInstruction>();
            foreach (var instruction in definition.Body.Instructions)
            {
                object? operand = instruction.Operand switch
                {
                    Mono.Cecil.MethodReference m when m.DeclaringType.Name is "Inventory" or "CookingStation" => method.Module.ResolveMethod(m.MetadataToken.ToInt32()),
                    Mono.Cecil.Cil.Instruction target => labels[target],
                    Mono.Cecil.Cil.Instruction[] targets => targets.Select(t => labels[t]).ToArray(),
                    Mono.Cecil.Cil.VariableDefinition local => local.Index,
                    Mono.Cecil.ParameterDefinition parameter => parameter.Index + 1,
                    _ => instruction.Operand
                };
                var code = new CodeInstruction(opcodes[instruction.OpCode.Value], operand);
                code.labels.Add(labels[instruction]);
                result.Add(code);
            }
            return result;
        }
        static bool Call(CodeInstruction instruction, string type, string name) => instruction.operand is MethodInfo method && method.DeclaringType?.Name == type && method.Name == name;
        static void SameStack(MethodInfo original, MethodInfo replacement)
        {
            var expected = new[] { original.DeclaringType! }.Concat(original.GetParameters().Select(p => p.ParameterType));
            if (!replacement.IsStatic || original.ReturnType != replacement.ReturnType || !expected.SequenceEqual(replacement.GetParameters().Select(p => p.ParameterType)))
                throw new InvalidOperationException("Replacement changes IL stack signature: " + original);
        }
        const string craftPatch = "DataForgeInventoryGuiDoCraftingAmountMultiplierPatch";
        var craft = Original("InventoryGui", "DoCrafting");
        var calls = craft.Select((instruction, index) => (instruction, index)).Where(p => Call(p.instruction, "Inventory", "AddItem") && ((MethodInfo)p.instruction.operand).GetParameters().Length == 10).ToArray();
        Assert(calls.Length == 4, "Reviewed 1.0 ordinary craft/upgrader layout changed");
        var patched = Apply(craftPatch, craft);
        Assert(patched.Count == craft.Count, "Craft patch changes instruction count");
        Assert(patched.Count(i => i.operand is MethodInfo m && m.Name == "AddItemWithMultiplier") == 1, "Craft output replacement count");
        Assert(patched.Count(i => i.operand is MethodInfo m && m.Name == "CanAddCraftedItemWithMultiplier") == 1, "Capacity replacement count");
        for (int i = 0; i < 3; i++) Assert(Equals(patched[calls[i].index].operand, calls[i].instruction.operand), "Upgrader/refund changed");
        SameStack((MethodInfo)calls[3].instruction.operand, (MethodInfo)patched[calls[3].index].operand);
        Assert(patched[calls[3].index].labels.SequenceEqual(calls[3].instruction.labels), "Craft branch labels lost");
        Assert(patched[calls[3].index].blocks.SequenceEqual(calls[3].instruction.blocks), "Craft exception blocks lost");

        // Rejection logging initializes the plugin's ServerSync/Unity ThreadingHelper singleton.
        // Exercise that path in the game; this process does not fake a running Unity instance.

        var cooking = Original("CookingStation", "RPC_RemoveDoneItem");
        var cookCalls = cooking.Select((instruction, index) => (instruction, index)).Where(p => Call(p.instruction, "CookingStation", "SpawnItem")).ToArray();
        Assert(cookCalls.Length == 1, "Cooking call count changed");
        var cooked = Apply("DataForgeCookingStationRemoveDoneItemAmountMultiplierPatch", cooking);
        SameStack((MethodInfo)cookCalls[0].instruction.operand, (MethodInfo)cooked[cookCalls[0].index].operand);
        Assert(cooked.Count == cooking.Count, "Cooking patch changes instruction count");
        Assert(((MethodInfo)cooked[cookCalls[0].index].operand).GetParameters().Last().Name == "cheated", "Cooking cheated argument lost");
        foreach (string name in new[] { "PieceComfortHudBadges", "LocalizationOverrideManager" })
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(mod.GetType("DataForge." + name, true)!.TypeHandle);
            checks++;
        }
        Console.WriteLine($"Transpiler checks passed: {checks} assertions plus replacement stack signatures, using original game IL (not Unity execution).");
    }
}
