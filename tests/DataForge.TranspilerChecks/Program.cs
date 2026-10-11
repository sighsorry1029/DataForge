using System.IO;

internal static class Program
{
    private static void Main(string[] args)
    {
        string mod = Path.GetFullPath(args[0]);
        TranspilerChecks.Run(mod, Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        FireplaceChecks.Run(mod, args.Length > 3 && args[3] != "-" ? Path.GetFullPath(args[3]) : null);
        if (args.Length > 4) VneiHistoryChecks.Run(mod, Path.GetFullPath(args[4]));
    }
}
