using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

// Pure managed calculations and real Harmony detours. No Unity objects/worlds.
internal static class FireplaceChecks
{
    private static MethodInfo SafeMinimum = null!;

    internal static void Run(string modPath, string? azuPath)
    {
        Assembly mod = Assembly.LoadFrom(modPath);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        Type capacity = mod.GetType("DataForge.FireplaceFuelCapacity", true)!;
        Type guard = mod.GetType("DataForge.AzuCraftyBoxesFuelGuard", true)!;
        SafeMinimum = guard.GetMethod("NonNegativeMinimum", flags)!;
        MethodInfo rewrite = guard.GetMethod("Transpiler", flags)!;
        int checks = 0;
        void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); checks++; }
        float Call(string method, params object[] args) => Convert.ToSingle(capacity.GetMethod(method, flags)!.Invoke(null, args));
        Assert(Call("ResolveCapacity", 4f, 10) == 40f && Call("ResolveCapacity", 20f, 10) == 200f &&
               Call("ResolveCapacity", 200f, 10) == 2000f,
            "The multiplier must retain different vanilla and mod fireplace capacities");
        Assert(Call("ResolveCapacity", 4f, 1) == 4f && Call("ResolveCapacity", 200f, 1) == 200f,
            "Multiplier 1 must restore each original value");
        Assert(Call("ResolveCapacity", 2.5f, 3) == 7.5f, "A mod fireplace's fractional original capacity must not be rounded");
        Assert(Call("ClampFuelChange", 79f, 80f, 20f) == 79f, "A decrement after lowering capacity discards stored fuel");
        Assert(Call("ClampFuelChange", 81f, 80f, 20f) == 80f, "An over-capacity fireplace can gain more fuel");
        Assert(Call("ClampFuelChange", 101f, 80f, 100f) == 100f && Call("ClampFuelChange", -1f, 80f, 100f) == 0f,
            "Normal fuel bounds changed");
        Assert(Call("RefundAmount", 80f, 3f) == 77f && Call("RefundAmount", 4.9f, 3f) == 1f &&
               Call("RefundAmount", 2f, 3f) == 0f, "Refunds must preserve stored excess and exclude starting/fractional fuel");

        // The game wrapper may already have called/JITted Azu's prefix before
        // DataForge patches the prefix itself. Exercise both installation orders.
        MethodInfo target = typeof(FireplaceChecks).GetMethod(nameof(Fill), flags)!;
        MethodInfo prefix = typeof(FireplaceChecks).GetMethod(nameof(FillPrefix), flags)!;
        Harmony provider = new("DataForge.Tests.FuelProvider");
        Harmony nested = new("DataForge.Tests.FuelGuard");
        try
        {
            provider.Patch(target, prefix: new HarmonyMethod(prefix));
            Assert(Fill(10f, 60f, 5f) == -50f, "The regression fixture must reproduce negative consumption");
            nested.Patch(prefix, transpiler: new HarmonyMethod(typeof(FireplaceChecks).GetMethod(nameof(SmokeTranspiler), flags)));
            Assert(Fill(10f, 60f, 5f) == 0f, "Patching an already registered prefix did not affect its game wrapper");
            Assert(Fill(100f, 60f, 5f) == 5f && Fill(100f, 60f, 50f) == 40f && Fill(100f, 100f, 5f) == 0f,
                "Nested fuel guard changed positive or full-capacity consumption");
            nested.UnpatchSelf();
            Assert(Fill(10f, 60f, 5f) == -50f, "Removing the guard did not restore the provider prefix");
            provider.UnpatchSelf();
            nested.Patch(prefix, transpiler: new HarmonyMethod(typeof(FireplaceChecks).GetMethod(nameof(SmokeTranspiler), flags)));
            provider.Patch(target, prefix: new HarmonyMethod(prefix));
            Assert(Fill(10f, 60f, 5f) == 0f, "Guard-first installation failed");
        }
        finally
        {
            provider.UnpatchSelf();
            nested.UnpatchSelf();
        }
        Assert(Fill(10f, 60f, 5f) == -999f, "Test detours leaked after cleanup");

        if (azuPath != null)
        {
            // Fireplace implements a default-interface method in the original
            // game. net48 cannot resolve that full method signature. Read Azu's
            // original IL symbolically, as in the existing game transpiler checks.
            using var azu = Mono.Cecil.ModuleDefinition.ReadModule(azuPath);
            var azuPrefix = azu.GetType("AzuCraftyBoxes.Patches.FireplaceInteractPatch").Methods.Single(method => method.Name == "Prefix");
            var original = ReadFuelInstructions(azuPrefix, guard);
            var snapshot = original.Select(code => new CodeInstruction(code)).ToList();
            var patched = ((IEnumerable<CodeInstruction>)rewrite.Invoke(null, new object[] { original })!).ToList();
            Assert(patched.Count == snapshot.Count, "Azu guard changed instruction count");
            int replacements = 0;
            for (int index = 0; index < patched.Count; index++)
            {
                var before = snapshot[index];
                var after = patched[index];
                if (Equals(after.operand, SafeMinimum))
                {
                    var originalMethod = (MethodInfo)before.operand;
                    Assert(originalMethod.IsStatic && originalMethod.ReturnType == SafeMinimum.ReturnType &&
                           originalMethod.GetParameters().Select(p => p.ParameterType).SequenceEqual(SafeMinimum.GetParameters().Select(p => p.ParameterType)),
                        "Fuel replacement changes the IL stack signature");
                    replacements++;
                }
                else Assert(before.opcode == after.opcode && Equals(before.operand, after.operand), "Azu logic outside fuel quantity was modified");
                Assert(before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks), "Azu branch/exception metadata was lost");
            }
            Assert(replacements == 2, "Expected inventory and container quantity guards");
            var changedLayout = snapshot.Where(code => !Equals(code.operand, snapshot.First(c => c.operand is MethodInfo m && m.Name == "Min").operand)).ToList();
            bool rejected = false;
            try { rewrite.Invoke(null, new object[] { changedLayout }); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
            Assert(rejected, "An unsupported Azu IL layout was silently accepted");
            Console.WriteLine($"AzuCraftyBoxes {azu.Assembly.Name.Version}: original IL rewrite checked; actual game prefix installation/interaction not executed.");
        }
        Console.WriteLine($"Fireplace checks passed: {checks} assertions (managed/Harmony runtime, not Unity execution).");
    }

    private static List<CodeInstruction> ReadFuelInstructions(Mono.Cecil.MethodDefinition method, Type guard)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var min = (MethodInfo)guard.GetField("Min", flags)!.GetValue(null)!;
        var maxFuel = (FieldInfo)guard.GetField("MaxFuel", flags)!.GetValue(null)!;
        var generator = new DynamicMethod("ReadFuelIL", typeof(void), Type.EmptyTypes).GetILGenerator();
        var labels = method.Body.Instructions.ToDictionary(instruction => instruction, _ => generator.DefineLabel());
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(opcode => opcode.Value);
        var result = new List<CodeInstruction>();
        foreach (var instruction in method.Body.Instructions)
        {
            object? operand = instruction.Operand switch
            {
                Mono.Cecil.MethodReference reference when reference.FullName == "System.Single UnityEngine.Mathf::Min(System.Single,System.Single)" => min,
                Mono.Cecil.FieldReference reference when reference.FullName == "System.Single Fireplace::m_maxFuel" => maxFuel,
                Mono.Cecil.Cil.Instruction target => labels[target],
                Mono.Cecil.Cil.Instruction[] targets => targets.Select(target => labels[target]).ToArray(),
                _ => instruction.Operand
            };
            var code = new CodeInstruction(opcodes[instruction.OpCode.Value], operand);
            code.labels.Add(labels[instruction]);
            result.Add(code);
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float Fill(float capacity, float current, float available) => -999f;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FillPrefix(float capacity, float current, float available, ref float __result)
    {
        __result = Math.Min(capacity - current, available);
        return false;
    }

    private static IEnumerable<CodeInstruction> SmokeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo min = typeof(Math).GetMethod(nameof(Math.Min), new[] { typeof(float), typeof(float) })!;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(min)) instruction.operand = SafeMinimum;
            yield return instruction;
        }
    }
}
