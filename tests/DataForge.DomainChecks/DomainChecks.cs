using System;
using System.Reflection;
using System.Linq;
using System.Collections;
using System.Text;

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
        Type itemEntry = mod.GetType("DataForge.ItemOverrideManager+ItemEntry", true)!;
        object item = Read("item: StaffThunderBlood\nprimaryAttack:\n  projectileVelocity: 0\n  projectileVelocityMin: -2\n  projectileAccuracy: 0\n  projectileAccuracyMin: 1\n  projectiles: 3\n  projectileBursts: 2\n  burstInterval: 0.25\n  blockReloadTime: 1.1\nequipment:\n  iceSkates: false\n  iceShoes: true", itemEntry);
        object itemRoundTrip = Read(Write(item), itemEntry);
        object attack = Get(itemRoundTrip, "PrimaryAttack")!;
        Assert((float)Get(attack, "ProjectileVelocity")! == 0 && (float)Get(attack, "ProjectileVelocityMin")! == -2 && (int)Get(attack, "ProjectileBursts")! == 2, "Attack settings lost or normalized during sync");
        Assert(Get(Get(itemRoundTrip, "Equipment")!, "IceSkates") is false && Get(Get(itemRoundTrip, "Equipment")!, "IceShoes") is true, "Explicit ice equipment flags lost");
        object omittedAttack = Get(Read("item: StaffThunderBlood\nprimaryAttack:\n  damageMultiplier: 2", itemEntry), "PrimaryAttack")!;
        Assert(Get(omittedAttack, "ProjectileVelocity") == null && Get(omittedAttack, "Projectiles") == null, "Omitted attack fields acquire defaults");
        Type effects = mod.GetType("DataForge.StatusEffectOverrideManager", true)!;
        Type damageType = game.GetType("HitData+DamageTypes", true)!;
        object damage = Activator.CreateInstance(damageType)!;
        damageType.GetField("m_fire")!.SetValue(damage, 25f);
        damageType.GetField("m_nonPlayer")!.SetValue(damage, 3f);
        object damagePatch = Read("fire: 0\nfrost: 8", effects.GetNestedType("StatusDamageDefinition", members)!);
        object changedDamage = effects.GetMethod("ApplyDamage", members)!.Invoke(null, new[] { damage, damagePatch })!;
        Assert((float)Field(changedDamage, "m_fire") == 0 && (float)Field(changedDamage, "m_frost") == 8, "Damage value-type update discarded");
        Assert((float)Field(changedDamage, "m_nonPlayer") == 3 && (float)Field(damage, "m_fire") == 25, "Damage edit changes omitted channels or the source value");
        Type effectEntry = effects.GetNestedType("StatusEffectEntry", members)!;
        object reactEntry = Read("effect: Staff_FrostOrbs\nreact:\n  minSpawnDamage: 0\n  projectileVelocity: 20\n  ttlPerItemLevel: 60\n  damagePerLevel:\n    frost: 8", effectEntry);
        object effectDefinition = effectEntry.GetMethod("ToDefinition", members)!.Invoke(reactEntry, null)!;
        object effectOutput = effectEntry.GetMethod("FromDefinition", members)!.Invoke(null, new[] { "Staff_FrostOrbs", effectDefinition, (object)true })!;
        object react = Get(Read(Write(effectOutput), effectEntry), "React")!;
        Assert((float)Get(react, "TtlPerItemLevel")! == 60 && (float)Get(Get(react, "DamagePerLevel")!, "Frost")! == 8 && (float)Get(react, "MinSpawnDamage")! == 0, "React definition/scaffold/sync round trip lost settings");
        Type frostType = effects.GetNestedType("FrostDefinition", members)!;
        object frost = Read("slowMultipliers:\n- Immune: 0\n- Weak: 1.5\n- Weak: 2", frostType);
        object multipliers = Get(Read(Write(frost), frostType), "SlowMultipliers")!;
        object?[] buildArguments = { multipliers, null };
        MethodInfo buildFrost = effects.GetMethod("TryBuildFrostSlowMultipliers", members)!;
        Assert((bool)buildFrost.Invoke(null, buildArguments)!, "Valid ordered Frost multipliers rejected");
        IList built = (IList)buildArguments[1]!;
        Assert(built.Count == 3 && (float)Field(built[0]!, "m_multiplier") == 0 && (float)Field(built[1]!, "m_multiplier") == 1.5f && (float)Field(built[2]!, "m_multiplier") == 2, "Frost zero/order/duplicate-first-match semantics changed");
        buildArguments = new[] { multipliers, null };
        buildFrost.Invoke(null, buildArguments);
        Assert(!ReferenceEquals(built, buildArguments[1]), "Frost rebuild aliases another effect's list");
        foreach (string invalid in new[] { "slowMultipliers:\n- Typo: 1", "slowMultipliers:\n- Weak: 1\n  Immune: 0" })
        {
            object bad = Get(Read(invalid, frostType), "SlowMultipliers")!;
            Assert(!(bool)buildFrost.Invoke(null, new[] { bad, null })!, "Invalid Frost list accepted");
        }
        object empty = Get(Read("slowMultipliers: []", frostType), "SlowMultipliers")!;
        buildArguments = new[] { empty, null };
        Assert((bool)buildFrost.Invoke(null, buildArguments)! && ((IList)buildArguments[1]!).Count == 0, "Explicit empty Frost list does not clear");
        Type pieceEntry = mod.GetType("DataForge.PieceOverrideManager+PieceEntry", true)!;
        object foundry = Read("piece: piece_FrostFoundry\ncookingStation:\n  canOvercookItems: false\n  useFuelWhileEmpty: false\n  skill: None\n  recordCrafter: true", pieceEntry);
        Type pruning = mod.GetType("DataForge.ReferenceValue", true)!;
        object station = Get(foundry, "CookingStation")!;
        object prunedStation = pruning.GetMethod("ClonePruned", members)!.MakeGenericMethod(station.GetType()).Invoke(null, new[] { station })!;
        object stationCopy = Read(Write(prunedStation), station.GetType());
        Assert(Get(stationCopy, "CanOvercookItems") is false && Get(stationCopy, "UseFuelWhileEmpty") is false && (string)Get(stationCopy, "Skill")! == "None" && Get(stationCopy, "RecordCrafter") is true, "Foundry policy is hidden by reference default pruning");
        Assert(Get(Read("fuel: Wood, false, 10, 60", station.GetType()), "CanOvercookItems") == null, "Old cooking config gains an explicit policy");
        StringBuilder comments = new();
        Type sections = mod.GetType("DataForge.DataForgeReferenceSections", true)!;
        sections.GetMethod("AppendEntryComments", members)!.Invoke(null, new object[] { comments, new[] { "prefab\r\n- item: unexpected\r  weight: 0\u2028override: false", "root -> child" } });
        Assert(comments.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).All(line => line.StartsWith("# ", StringComparison.Ordinal)), "Prefab name escapes YAML comment");
        object commentedItem = Read(comments + "item: StaffOrbofAhri\nprimaryAttack:\n  projectiles: 2", itemEntry);
        Assert((string)Get(commentedItem, "Item")! == "StaffOrbofAhri" && (int)Get(Get(commentedItem, "PrimaryAttack")!, "Projectiles")! == 2, "Reference comments change editable YAML");
        Type itemDefinitionType = mod.GetType("DataForge.ItemOverrideManager+ItemDefinition", true)!;
        object diagnosticDefinition = Activator.CreateInstance(itemDefinitionType)!;
        itemDefinitionType.GetProperty("ReferenceComments", members)!.SetValue(diagnosticDefinition, new System.Collections.Generic.List<string> { "diagnostic-only" });
        Assert(!Write(diagnosticDefinition).Contains("diagnostic-only"), "Read-only graph leaked into editable/synced schema");
        object kiln = Read("piece: piece_FrostKiln\nsmelter:\n  conversions:\n  - None: FrozenFuel", pieceEntry);
        object kilnCopy = Read(Write(kiln), pieceEntry);
        IList conversions = (IList)Get(Get(kilnCopy, "Smelter")!, "Conversions")!;
        Assert((string)((IDictionary)conversions[0]!)["None"]! == "FrozenFuel", "Source-free smelter conversion lost during YAML round trip");
        Console.WriteLine($"Domain checks passed: {checks} managed assertions (not Unity execution).");
    }
}
