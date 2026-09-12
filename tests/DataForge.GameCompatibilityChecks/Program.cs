using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

// These are static checks against ORIGINAL game assemblies, not a substitute for Unity/network tests.
if (args.Length < 3)
    throw new ArgumentException("check <mod.dll> <original Managed directory> [BepInEx/core directory] OR patch-serversync <original ServerSync.dll> <output.dll> <original assembly_valheim.dll>");

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
    types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
static string Scope(TypeReference type) => type.GetElementType().Scope.Name.Replace(".dll", "");
static string Signature(TypeReference type) => type is ByReferenceType byRef ? Signature(byRef.ElementType) : type.FullName;

if (args[0] == "patch-serversync")
{
    // Preserve the bundled fork's exact RPC, security, version negotiation and buffering code.
    const string sourceHash = "166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60";
    if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))) != sourceHash)
        throw new InvalidOperationException("Unexpected ServerSync input; review the new library before adapting it.");
    using var game = ModuleDefinition.ReadModule(args[3]);
    var target = game.GetType("ZRoutedRpc").Fields.Single(f => f.Name == "Everybody");
    if (!target.IsLiteral || target.FieldType.FullName != "System.Int64" || Convert.ToInt64(target.Constant) != 0)
        throw new InvalidOperationException("Target game does not have the reviewed const long Everybody = 0 contract.");
    using var library = ModuleDefinition.ReadModule(args[1]);
    int count = 0;
    foreach (var method in AllTypes(library.Types).SelectMany(t => t.Methods).Where(m => m.HasBody))
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.Operand is not FieldReference field || field.FullName != "System.Int64 ZRoutedRpc::Everybody") continue;
        if (instruction.OpCode != OpCodes.Ldsfld) throw new InvalidOperationException("Unexpected Everybody instruction.");
        instruction.OpCode = OpCodes.Ldc_I8;
        instruction.Operand = (long)0;
        count++;
        Console.WriteLine($"Adapted {method.FullName}");
    }
    if (count != 3) throw new InvalidOperationException($"Expected 3 field loads, found {count}; output not written.");
    library.Write(args[2]);
    Console.WriteLine($"Preserved ServerSync except {count} constant loads: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2])))}");
    return;
}

using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetFullPath(args[2]));
if (args.Length > 3) resolver.AddSearchDirectory(Path.GetFullPath(args[3]));
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
using var mod = ModuleDefinition.ReadModule(args[1], new ReaderParameters { AssemblyResolver = resolver });
var gameNames = new HashSet<string> { "assembly_valheim", "assembly_utils", "assembly_guiutils", "gui_framework", "Splatform", "SoftReferenceableAssets" };
int checkedReferences = 0, privateReferences = 0, patches = 0;
var errors = new SortedSet<string>();
var deferred = new SortedSet<string>();
void Error(string message) => errors.Add(message);

foreach (var method in AllTypes(mod.Types).SelectMany(t => t.Methods).Where(m => m.HasBody))
foreach (var instruction in method.Body.Instructions)
{
    if (instruction.Operand is not MemberReference reference || reference.DeclaringType == null || !gameNames.Contains(Scope(reference.DeclaringType))) continue;
    try
    {
        if (reference is MethodReference call)
        {
            var definition = call.Resolve();
            if (definition == null) Error($"Missing method: {call.FullName} in {method.FullName}");
            else
            {
                checkedReferences++;
                if (!definition.IsPublic) privateReferences++;
                if (definition.IsStatic == call.HasThis) Error($"Static/instance mismatch: {call.FullName}");
            }
        }
        else if (reference is FieldReference access)
        {
            var definition = access.Resolve();
            if (definition == null) Error($"Missing field: {access.FullName} in {method.FullName}");
            else
            {
                checkedReferences++;
                if (!definition.IsPublic) privateReferences++;
                if (definition.IsLiteral) Error($"Literal used as runtime field: {access.FullName} in {method.FullName}");
                bool staticOp = instruction.OpCode == OpCodes.Ldsfld || instruction.OpCode == OpCodes.Stsfld || instruction.OpCode == OpCodes.Ldsflda;
                if (definition.IsStatic != staticOp && instruction.OpCode != OpCodes.Ldtoken) Error($"Static/instance field mismatch: {access.FullName}");
            }
        }
    }
    catch (Exception exception) { Error($"Resolution failed: {reference.FullName}: {exception.Message}"); }
}

var groups = new List<(string Name, CustomAttribute[] Attributes, MethodDefinition[] Methods)>();
foreach (var type in AllTypes(mod.Types))
{
    var attributes = type.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch").ToArray();
    if (attributes.Length > 0) groups.Add((type.FullName, attributes, type.Methods.ToArray()));
    foreach (var method in type.Methods)
    {
        var methodAttributes = method.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch").ToArray();
        if (methodAttributes.Length > 0) groups.Add((method.FullName, attributes.Concat(methodAttributes).ToArray(), new[] { method }));
    }
}
foreach (var group in groups)
{
    TypeReference? targetType = null;
    string? methodName = null;
    var argumentTypes = new List<string>();
    bool explicitSignature = false;
    foreach (var attribute in group.Attributes)
    foreach (var argument in attribute.ConstructorArguments)
    {
        if (argument.Value is TypeReference typeRef)
        {
            if (targetType == null) targetType = typeRef;
            else { argumentTypes.Add(typeRef.FullName); explicitSignature = true; }
        }
        else if (argument.Value is string name) methodName = name;
        else if (argument.Value is CustomAttributeArgument[] array && argument.Type.FullName == "System.Type[]")
        {
            explicitSignature = true;
            argumentTypes.AddRange(array.Select(a => ((TypeReference)a.Value).FullName));
        }
    }
    if (targetType == null || methodName == null)
    {
        if (group.Methods.Any(m => m.Name is "TargetMethod" or "TargetMethods")) deferred.Add($"Dynamic patch: {group.Name}");
        continue;
    }
    if (!gameNames.Contains(Scope(targetType))) continue;
    var targetDefinition = targetType.Resolve();
    var matches = targetDefinition.Methods.Where(m => m.Name == methodName && (!explicitSignature || m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(argumentTypes))).ToArray();
    if (matches.Length != 1) { Error($"Harmony target {targetType.FullName}.{methodName}: {matches.Length} matches ({group.Name})"); continue; }
    patches++;
    var original = matches[0];
    foreach (var patch in group.Methods.Where(m => m.Name is "Prefix" or "Postfix" or "Finalizer" || m.CustomAttributes.Any(a => a.AttributeType.FullName is "HarmonyLib.HarmonyPrefix" or "HarmonyLib.HarmonyPostfix" or "HarmonyLib.HarmonyFinalizer")))
    foreach (var parameter in patch.Parameters)
    {
        if (parameter.Name == "__result")
        {
            if (Signature(parameter.ParameterType) != Signature(original.ReturnType)) Error($"Wrong __result in {patch.FullName}");
        }
        else if (parameter.Name.StartsWith("___"))
        {
            var field = targetDefinition.Fields.SingleOrDefault(f => f.Name == parameter.Name[3..]);
            if (field == null || Signature(field.FieldType) != Signature(parameter.ParameterType)) Error($"Wrong field injection {parameter.Name} in {patch.FullName}");
        }
        else if (!parameter.Name.StartsWith("__"))
        {
            var originalParameter = original.Parameters.SingleOrDefault(p => p.Name == parameter.Name);
            if (originalParameter == null || Signature(originalParameter.ParameterType) != Signature(parameter.ParameterType)) Error($"Wrong argument injection {parameter.Name} in {patch.FullName}");
        }
    }
}

Console.WriteLine($"Original game: {args[2]}");
Console.WriteLine($"Resolved {checkedReferences} game member instructions ({privateReferences} non-public, runtime access still requires testing); checked {patches} Harmony targets and injections.");
foreach (var item in deferred) Console.WriteLine($"DEFERRED {item}");
foreach (var item in errors) Console.WriteLine($"FAIL {item}");
Console.WriteLine($"Failures: {errors.Count}");
Environment.ExitCode = errors.Count == 0 ? 0 : 1;
