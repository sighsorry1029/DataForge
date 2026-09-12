using System.IO;

internal static class Program
{
    private static void Main(string[] args) => TranspilerChecks.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
}
