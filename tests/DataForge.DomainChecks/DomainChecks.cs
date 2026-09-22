using System;
using System.Reflection;
using System.Linq;

// Exercise the shipped serializer and plain managed data against original game types.
// No Unity objects are created and no native/gameplay execution is simulated.
internal static class DomainChecks
{
    internal static void Run(Assembly mod, Assembly game)
    {
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        int checks = 0;
        void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
        Type manager = mod.GetType("DataForge.RecipeOverrideManager", true)!;
        Type requirement = manager.GetNestedType("RequirementDefinition", members)!;
        // Construct the merged YAML codec with the actual converter without running
        // the manager's Unity/Harmony-dependent static initializer on desktop .NET.
        object Codec(string builderName)
        {
            Type builderType = mod.GetType("YamlDotNet.Serialization." + builderName, true)!;
            object builder = Activator.CreateInstance(builderType)!;
            Type namingType = mod.GetType("YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention", true)!;
            object naming = namingType.GetField("Instance", members)!.GetValue(null)!;
            builderType.GetMethod("WithNamingConvention")!.Invoke(builder, new[] { naming });
            object converter = Activator.CreateInstance(manager.GetNestedType("RequirementDefinitionYamlConverter", members)!, true)!;
            builderType.GetMethods().Single(method => method.Name == "WithTypeConverter" && method.GetParameters().Length == 1).Invoke(builder, new[] { converter });
            if (builderName == "SerializerBuilder")
            {
                Type handling = mod.GetType("YamlDotNet.Serialization.DefaultValuesHandling", true)!;
                builderType.GetMethod("ConfigureDefaultValuesHandling")!.Invoke(builder, new[] { Enum.Parse(handling, "OmitNull") });
            }
            return builderType.GetMethod("Build")!.Invoke(builder, null)!;
        }
        object deserializer = Codec("DeserializerBuilder");
        object serializer = Codec("SerializerBuilder");
        object Read(string yaml, Type? type = null) => deserializer.GetType().GetMethod("Deserialize", new[] { typeof(string), typeof(Type) })!.Invoke(deserializer, new object[] { yaml, type ?? requirement })!;
        string Write(object data) => (string)serializer.GetType().GetMethod("Serialize", new[] { typeof(object) })!.Invoke(serializer, new[] { data })!;
        object? Get(object data, string name) => data.GetType().GetProperty(name, members)!.GetValue(data);
        object Build(object definition, object? previous = null) => requirement.GetMethod("Build", members)!.Invoke(definition, new[] { null, previous })!;
        object Field(object data, string name) => data.GetType().GetField(name)!.GetValue(data)!;

        object legacy = Read("Iron: 20, 10");
        Assert((int)Get(legacy, "Amount")! == 20 && (int)Get(legacy, "AmountPerLevel")! == 10, "Legacy resource tuple changed");
        Assert(Get(legacy, "UpgraderResource") == null, "Omitted resource classification becomes explicit");
        Assert(Write(legacy).Trim() == "Iron: 20, 10", "Legacy tuple output changed");
        Assert((int)Get(Read("SurtlingCore: 0, 5, 2"), "ExactQuality")! == 2, "Exact-quality tuple changed");
        object special = Read("item: Upgrader7Weapon\namount: 1\nupgraderResource: true\nrecover: false\nextraAmountOnlyOneIngredient: 3\n");
        object original = Build(special);
        Assert((bool)Field(original, "m_upgraderResource") && !(bool)Field(original, "m_recover") && (int)Field(original, "m_extraAmountOnlyOneIngredient") == 3, "Explicit requirement metadata lost");
        object inherited = Build(Read("Upgrader7Weapon: 2"), original);
        Assert((bool)Field(inherited, "m_upgraderResource") && !(bool)Field(inherited, "m_recover") && (int)Field(inherited, "m_extraAmountOnlyOneIngredient") == 3, "Legacy edit drops original metadata");
        Assert((int)inherited.GetType().GetMethod("GetAmount")!.Invoke(inherited, new object[] { 2 })! == 2, "Upgrader's base amount is lost on rebuilding");
        object cleared = Build(Read("item: Upgrader7Weapon\namount: 1\nupgraderResource: false\nrecover: true\nextraAmountOnlyOneIngredient: 0"), original);
        Assert(!(bool)Field(cleared, "m_upgraderResource") && (bool)Field(cleared, "m_recover") && (int)Field(cleared, "m_extraAmountOnlyOneIngredient") == 0, "Explicit default cannot clear metadata");
        object baseline = special;
        object restored = Build(Read(Write(baseline)), cleared);
        Assert((bool)Field(restored, "m_upgraderResource") && !(bool)Field(restored, "m_recover") && (int)Field(restored, "m_extraAmountOnlyOneIngredient") == 3, "Serialize/rebuild loses explicit metadata");
        Type referenceType = manager.GetNestedType("ResourceReferenceDefinition", members)!;
        object reference = referenceType.GetMethod("From", members)!.Invoke(null, new[] { baseline, (object)true })!;
        Assert((bool)Get(Read(Write(reference)), "UpgraderResource")!, "Copying a reference loses upgrader classification");
        object plain = Build(legacy);
        Assert(!(bool)Field(plain, "m_upgraderResource") && (bool)Field(plain, "m_recover") && (int)Field(plain, "m_extraAmountOnlyOneIngredient") == 0, "New resource defaults changed");
        Type entryType = manager.GetNestedType("RecipeEntry", members)!;
        object entry = Read("recipe: StaffThunderBlood;2\nnoCraftOnlyUpgrade: false", entryType);
        Assert(Get(Read(Write(entry), entryType), "NoCraftOnlyUpgrade") is false, "Explicit noCraftOnlyUpgrade false lost in sync round trip");
        foreach (string invalid in new[] { "Iron: 1, 2, 3, 4", "item: Iron\namount: 1\nupgraderResource: yes", "item: Iron\namount: 1\nexactQuality: 1", "item: Iron\namount: 1\nunknown: 1", "item: Iron\namount: 1\namount: 2" })
        {
            bool rejected = false;
            try { Read(invalid); }
            catch (TargetInvocationException exception) when (exception.InnerException?.GetType().FullName?.StartsWith("YamlDotNet.", StringComparison.Ordinal) == true) { rejected = true; }
            Assert(rejected, "Invalid resource silently accepted: " + invalid);
        }
        Console.WriteLine($"Domain checks passed: {checks} managed assertions (not Unity execution).");
    }
}
