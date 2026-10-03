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
        object Build(object definition, object? previous = null) =>
            requirement.GetMethod("Build", members)!.Invoke(definition, new[] { null, previous })!;
        object Field(object data, string name) => data.GetType().GetField(name)!.GetValue(data)!;
        bool Rejected(string yaml, Type type)
        {
            try { Read(yaml, type); }
            catch (TargetInvocationException exception) when (exception.InnerException?.GetType().FullName?.StartsWith("YamlDotNet.", StringComparison.Ordinal) == true) { return true; }
            return false;
        }

        object regular = Read("Iron: 20, 10");
        Assert((int)Get(regular, "Amount")! == 20 && (int)Get(regular, "AmountPerLevel")! == 10, "Resource tuple changed");
        Assert(requirement.GetProperty("UpgraderResource", members) == null, "Resource classification remains exposed as tuple metadata");
        Assert(Write(regular).Trim() == "Iron: 20, 10", "Resource tuple output changed");
        Assert((int)Get(Read("SurtlingCore: 0, 5, 2"), "ExactQuality")! == 2, "Exact-quality tuple changed");
        object special = Read("upgrade: Upgrader7Weapon");
        Assert((bool)Get(special, "UpgradeResource")! && (int)Get(special, "Amount")! == 1 &&
               Get(special, "AmountPerLevel") == null && Get(special, "ExactQuality") == null,
            "Reserved upgrade entry acquired editable amount metadata");
        Assert(Write(Read("upgrade: CustomIdol")).Trim() == "upgrade: CustomIdol", "Custom upgrade prefab is not preserved");
        Assert(!(bool)Field(Build(Read("Upgrader7Weapon: 1")), "m_upgraderResource"), "Prefab name silently selects upgrade semantics");
        object original = Build(special);
        original.GetType().GetField("m_recover")!.SetValue(original, false);
        original.GetType().GetField("m_extraAmountOnlyOneIngredient")!.SetValue(original, 3);
        Assert((bool)Field(original, "m_upgraderResource") && !(bool)Field(original, "m_recover") && (int)Field(original, "m_extraAmountOnlyOneIngredient") == 3, "Upgrade requirement setup failed");
        object inherited = Build(Read("upgrade: Upgrader7Weapon"), original);
        Assert((bool)Field(inherited, "m_upgraderResource") && !(bool)Field(inherited, "m_recover") && (int)Field(inherited, "m_extraAmountOnlyOneIngredient") == 3, "Upgrade entry drops hidden game metadata");
        Assert((int)inherited.GetType().GetMethod("GetAmount")!.Invoke(inherited, new object[] { 2 })! == 1, "Upgrade entry is not fixed at one item");
        object baseline = Read("upgrade: Upgrader7Weapon");
        requirement.GetProperty("Recover", members)!.SetValue(baseline, false);
        requirement.GetProperty("ExtraAmountOnlyOneIngredient", members)!.SetValue(baseline, 3);
        object restored = Build(baseline);
        Assert((bool)Field(restored, "m_upgraderResource") && !(bool)Field(restored, "m_recover") && (int)Field(restored, "m_extraAmountOnlyOneIngredient") == 3, "Baseline restore loses hidden game metadata");
        object reference = requirement.GetMethod("ForReference", members)!.Invoke(null, new[] { baseline, (object)true })!;
        Assert(Write(reference).Trim() == "upgrade: Upgrader7Weapon", "Upgrade resource reference is not reserved shorthand");
        object plain = Build(regular);
        Assert(!(bool)Field(plain, "m_upgraderResource") && (bool)Field(plain, "m_recover") && (int)Field(plain, "m_extraAmountOnlyOneIngredient") == 0, "New resource defaults changed");
        Type entryType = manager.GetNestedType("RecipeEntry", members)!;
        object entry = Read("recipe: StaffThunderBlood;2\nnoCraftOnlyUpgrade: false\nresources:\n- Gold: 10, 5\n- upgrade: Upgrader7Weapon", entryType);
        Assert(Get(Read(Write(entry), entryType), "NoCraftOnlyUpgrade") is false, "Explicit noCraftOnlyUpgrade false lost in sync round trip");
        object entryRoundTrip = Read(Write(entry), entryType);
        IList resources = (IList)Get(entryRoundTrip, "Resources")!;
        Assert(resources.Count == 2, "Regular and upgrade entries did not share one resource list");
        Assert((bool)Field(Build(resources[1]!), "m_upgraderResource"), "Reserved upgrade entry does not set the game classification");
        // Exercise the whole compact reference projection, including default pruning,
        // before copying its YAML back into an editable entry. Upgradeability is
        // supplied by the runtime caller; no ObjectDB or Unity prefab is fabricated.
        Type recipeDefinitionType = manager.GetNestedType("RecipeDefinition", members)!;
        Type recipeReferenceType = manager.GetNestedType("RecipeReferenceEntry", members)!;
        object RecipeReference(object definition, bool upgradeable) => recipeReferenceType.GetMethod("From", members)!
            .Invoke(null, new[] { "SwordNiedhoggBlood", definition, (object)upgradeable })!;
        object recipeBaseline = Read("amount: 1\ncraftingStation: blackforge\nminStationLevel: 4\nlistSortWeight: 100\nnoCraftOnlyUpgrade: false\nresources:\n" +
            "- SwordNiedhogg: 0\n- FlametalNew: 6, 6\n- GemstoneRed: 0, 1\n- upgrade: Upgrader6Weapon\n" +
            "- Wood: 1\n- SurtlingCore: 1, 0, 2\n- Wood: 1, 10\n- Upgrader6Weapon: 0", recipeDefinitionType);
        string baselineYaml = Write(recipeBaseline);
        object recipeReference = RecipeReference(recipeBaseline, true);
        string referenceYaml = Write(recipeReference);
        Assert(referenceYaml.Contains("upgrade: Upgrader6Weapon") && !referenceYaml.Contains("upgradeResource:"),
            "Compact recipe reference lost reserved Upgrader shorthand");
        Assert(referenceYaml.Contains("Wood: 1") && referenceYaml.Contains("SwordNiedhogg: 0") &&
               referenceYaml.Contains("SurtlingCore: 1, 0, 2"), "Compact recipe reference lost a required tuple value");
        object copiedRecipe = Read(referenceYaml, entryType);
        Assert((string)Get(copiedRecipe, "Recipe")! == "SwordNiedhoggBlood" &&
               (string)Get(copiedRecipe, "CraftingStation")! == "blackforge, 4" &&
               Get(copiedRecipe, "ListSortWeight") == null && Get(copiedRecipe, "NoCraftOnlyUpgrade") == null,
            "Compact recipe header/default omission changed");
        IList baselineResources = (IList)Get(recipeBaseline, "Resources")!;
        IList copiedResources = (IList)Get(copiedRecipe, "Resources")!;
        Assert(copiedResources.Count == baselineResources.Count, "Reference copy lost repeated or same-prefab resources");
        for (int index = 0; index < baselineResources.Count; index++)
        {
            object source = baselineResources[index]!;
            object copied = copiedResources[index]!;
            Assert(Equals(Get(source, "Item"), Get(copied, "Item")) && Equals(Get(source, "Amount"), Get(copied, "Amount")) &&
                   Equals(Get(source, "AmountPerLevel") ?? 0, Get(copied, "AmountPerLevel") ?? 0) &&
                   Equals(Get(source, "ExactQuality"), Get(copied, "ExactQuality")) &&
                   Equals(Get(source, "UpgradeResource"), Get(copied, "UpgradeResource")),
                "Reference copy changed resource semantics/order at index " + index);
            object built = Build(copied);
            Assert(Equals(Field(built, "m_amount"), Get(source, "Amount")) &&
                   Equals(Field(built, "m_upgraderResource"), Get(source, "UpgradeResource")),
                "Copied reference changed game amount/classification at index " + index);
        }
        Assert(Write(recipeBaseline) == baselineYaml, "Reference generation changed the recipe baseline");
        IList projectedResources = (IList)Get(recipeReference, "Resources")!;
        requirement.GetProperty("Amount", members)!.SetValue(projectedResources[4], 9);
        Assert((int)Get(baselineResources[4]!, "Amount")! == 1, "Reference resource shares mutable state with the baseline");
        object nonUpgradeable = Read(Write(RecipeReference(recipeBaseline, false)), entryType);
        IList nonUpgradeableResources = (IList)Get(nonUpgradeable, "Resources")!;
        Assert(Get(nonUpgradeableResources[1]!, "AmountPerLevel") == null && Get(nonUpgradeableResources[6]!, "AmountPerLevel") == null,
            "Non-upgradeable recipe exports irrelevant upgrade amounts");
        Assert((int)Get(nonUpgradeableResources[5]!, "AmountPerLevel")! == 0 && (int)Get(nonUpgradeableResources[5]!, "ExactQuality")! == 2 &&
               (bool)Get(nonUpgradeableResources[3]!, "UpgradeResource")!, "Non-upgradeable recipe lost exact-quality or Upgrader semantics");
        foreach (string emptyResources in new[] { "", "resources: []", "resources: null" })
        {
            object emptyDefinition = Read("amount: 1\n" + emptyResources, recipeDefinitionType);
            string emptyReference = Write(RecipeReference(emptyDefinition, true));
            Assert(!emptyReference.Contains("resources:") && Get(Read(emptyReference, entryType), "Resources") == null,
                "Empty reference resources are no longer omitted");
        }
        Assert(entryType.GetProperty("UpgradeResources", members) == null &&
               Rejected("recipe: StaffThunderBlood\nupgradeResources:\n- Upgrader7Weapon: 1", entryType),
            "Removed upgradeResources block remains accepted");
        foreach (string invalid in new[] { "Iron: 1, 2, 3, 4", "item: Iron\namount: 1", "Iron: 0, 5, 1", "Iron: one", "Upgrader7Weapon", "upgrade: ''", "upgrade: Upgrader7Weapon, 2" })
        {
            bool rejected = false;
            try { Read(invalid); }
            catch (TargetInvocationException exception) when (exception.InnerException?.GetType().FullName?.StartsWith("YamlDotNet.", StringComparison.Ordinal) == true) { rejected = true; }
            Assert(rejected, "Invalid resource silently accepted: " + invalid);
        }
        Type itemEntry = mod.GetType("DataForge.ItemOverrideManager+ItemEntry", true)!;
        object item = Read("item: StaffThunderBlood\nequipment:\n  iceSkates: false\n  iceShoes: true", itemEntry);
        object itemRoundTrip = Read(Write(item), itemEntry);
        Assert(Get(Get(itemRoundTrip, "Equipment")!, "IceSkates") is false && Get(Get(itemRoundTrip, "Equipment")!, "IceShoes") is true, "Explicit ice equipment flags lost");
        Type attackType = mod.GetType("DataForge.ItemOverrideManager+AttackDefinition", true)!;
        foreach (string removed in new[] { "ProjectileVelocity", "ProjectileVelocityMin", "ProjectileAccuracy", "ProjectileAccuracyMin", "Projectiles", "ProjectileBursts", "BurstInterval", "BlockReloadTime" })
            Assert(attackType.GetProperty(removed, members) == null, "Removed attack field remains in schema: " + removed);
        Assert(Rejected("item: StaffThunderBlood\nprimaryAttack:\n  projectileVelocity: 20", itemEntry), "Removed attack YAML remains accepted");
        Type effects = mod.GetType("DataForge.StatusEffectOverrideManager", true)!;
        Type effectListType = game.GetType("EffectList", true)!;
        Type effectDataType = game.GetType("EffectList+EffectData", true)!;
        object originalEffectData = Activator.CreateInstance(effectDataType)!;
        foreach (FieldInfo field in effectDataType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType == typeof(bool)) field.SetValue(originalEffectData, true);
            else if (field.FieldType == typeof(int)) field.SetValue(originalEffectData, 2);
            else if (field.FieldType == typeof(string)) field.SetValue(originalEffectData, "Spine");
        }
        // Disabled entries are not exported to compact YAML, but baseline/clone
        // copies must still retain their metadata. No native prefab is fabricated.
        effectDataType.GetField("m_enabled")!.SetValue(originalEffectData, false);
        object originalEffectList = Activator.CreateInstance(effectListType)!;
        Array originalEffectArray = Array.CreateInstance(effectDataType, 1);
        originalEffectArray.SetValue(originalEffectData, 0);
        effectListType.GetField("m_effectPrefabs")!.SetValue(originalEffectList, originalEffectArray);
        object copiedEffectList = effects.GetMethod("CloneEffectList", members)!.Invoke(null, new[] { originalEffectList })!;
        Array copiedEffectArray = (Array)Field(copiedEffectList, "m_effectPrefabs");
        object copiedEffectData = copiedEffectArray.GetValue(0)!;
        Assert(!ReferenceEquals(originalEffectList, copiedEffectList) && !ReferenceEquals(originalEffectArray, copiedEffectArray) &&
               !ReferenceEquals(originalEffectData, copiedEffectData), "Effect list copy shares mutable metadata with its source");
        foreach (FieldInfo field in effectDataType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Assert(Equals(field.GetValue(originalEffectData), field.GetValue(copiedEffectData)), "Effect metadata copy lost " + field.Name);
        effectDataType.GetField("m_attach")!.SetValue(copiedEffectData, false);
        Assert((bool)Field(originalEffectData, "m_attach"), "Changing copied attachment alters the baseline");
        foreach (string clear in new[] { "", "  ", "None", " none " })
        {
            object cleared = effects.GetMethod("ParseEffectList", members)!.Invoke(null, new object[] { "Burning", clear, "startEffects", originalEffectList })!;
            Assert(((Array)Field(cleared, "m_effectPrefabs")).Length == 0 && originalEffectArray.Length == 1 &&
                   (bool)Field(originalEffectData, "m_attach"), "Clearing an effect list changes the baseline or retains effects");
        }
        // A null assignment delegate makes any accidental assignment fail.
        effects.GetMethod("ApplyEffectList", members)!.Invoke(null, new object?[] { "Burning", null, originalEffectList, null, "startEffects" });
        Assert(ReferenceEquals(Field(originalEffectList, "m_effectPrefabs"), originalEffectArray), "Omitted effect list changed");
        Type damageType = game.GetType("HitData+DamageTypes", true)!;
        object damage = Activator.CreateInstance(damageType)!;
        damageType.GetField("m_fire")!.SetValue(damage, 25f);
        damageType.GetField("m_nonPlayer")!.SetValue(damage, 3f);
        object damagePatch = Read("fire: 0\nfrost: 8", effects.GetNestedType("StatusDamageDefinition", members)!);
        object changedDamage = effects.GetMethod("ApplyDamage", members)!.Invoke(null, new[] { damage, damagePatch })!;
        Assert((float)Field(changedDamage, "m_fire") == 0 && (float)Field(changedDamage, "m_frost") == 8, "Damage value-type update discarded");
        Assert((float)Field(changedDamage, "m_nonPlayer") == 3 && (float)Field(damage, "m_fire") == 25, "Damage edit changes omitted channels or the source value");
        Type effectEntry = effects.GetNestedType("StatusEffectEntry", members)!;
        object reactEntry = Read("effect: Staff_FrostOrbs\nreact:\n  minSpawnDamage: 0\n  damagePerLevel:\n    frost: 8", effectEntry);
        object effectDefinition = effectEntry.GetMethod("ToDefinition", members)!.Invoke(reactEntry, null)!;
        object effectOutput = effectEntry.GetMethod("FromDefinition", members)!.Invoke(null, new[] { "Staff_FrostOrbs", effectDefinition, (object)true })!;
        object react = Get(Read(Write(effectOutput), effectEntry), "React")!;
        Assert((float)Get(Get(react, "DamagePerLevel")!, "Frost")! == 8 && (float)Get(react, "MinSpawnDamage")! == 0, "React definition/scaffold/sync round trip lost settings");
        Type reactType = effects.GetNestedType("ReactDefinition", members)!;
        Assert(reactType.GetProperty("ProjectileVelocity", members) == null && reactType.GetProperty("TtlPerItemLevel", members) == null,
            "Removed React fields remain in the schema");
        Assert(Rejected("effect: Staff_FrostOrbs\nreact:\n  projectileVelocity: 20", effectEntry) &&
               Rejected("effect: Staff_FrostOrbs\nreact:\n  ttlPerItemLevel: 60", effectEntry),
            "Removed React YAML remains accepted");
        Type frostType = effects.GetNestedType("FrostDefinition", members)!;
        Assert(frostType.GetProperty("SlowMultipliers", members) == null, "Removed Frost field remains in schema");
        Assert(Rejected("slowMultipliers:\n- Weak: 1.5", frostType), "Removed Frost YAML remains accepted");
        Type pieceEntry = mod.GetType("DataForge.PieceOverrideManager+PieceEntry", true)!;
        Type stationType = mod.GetType("DataForge.PieceOverrideManager+CookingStationDefinition", true)!;
        foreach (string removed in new[] { "CanOvercookItems", "UseFuelWhileEmpty", "Skill", "RecordCrafter" })
            Assert(stationType.GetProperty(removed, members) == null, "Removed cookingStation field remains in schema: " + removed);
        Assert(Rejected("piece: piece_FrostFoundry\ncookingStation:\n  canOvercookItems: false", pieceEntry), "Removed cookingStation YAML remains accepted");
        StringBuilder comments = new();
        Type sections = mod.GetType("DataForge.DataForgeReferenceSections", true)!;
        sections.GetMethod("AppendEntryComments", members)!.Invoke(null, new object[] { comments, new[] { "prefab\r\n- item: unexpected\r  weight: 0\u2028override: false", "root -> child" } });
        Assert(comments.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).All(line => line.StartsWith("# ", StringComparison.Ordinal)), "Prefab name escapes YAML comment");
        object commentedItem = Read(comments + "item: StaffOrbofAhri\nprimaryAttack:\n  damageMultiplier: 2", itemEntry);
        Assert((string)Get(commentedItem, "Item")! == "StaffOrbofAhri" && (float)Get(Get(commentedItem, "PrimaryAttack")!, "DamageMultiplier")! == 2, "Reference comments change editable YAML");
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
