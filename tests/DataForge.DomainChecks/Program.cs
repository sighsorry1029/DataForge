using System;
using System.IO;
using System.Reflection;

string modPath = Path.GetFullPath(args[0]);
string managed = Path.GetFullPath(args[1]);
string core = Path.GetFullPath(args[2]);
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
{
    string name = new AssemblyName(request.Name).Name!;
    foreach (string directory in new[] { Path.GetDirectoryName(modPath)!, managed, core })
    {
        string candidate = Path.Combine(directory, name + ".dll");
        if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
    }
    return null;
};
DomainChecks.Run(Assembly.LoadFrom(modPath), Assembly.LoadFrom(Path.Combine(managed, "assembly_valheim.dll")));
if (args.Length > 4)
    OptionalModChecks.Run(Assembly.LoadFrom(modPath), Assembly.LoadFrom(Path.Combine(managed, "assembly_valheim.dll")),
        Path.GetFullPath(args[3]), Path.GetFullPath(args[4]));
