using System.Collections.Generic;

namespace DataForge;

internal static class DataForgeConsoleCommands
{
    private const string WriteFullCommandName = "dataforge:full";
    private const string WriteReferenceCommandName = "dataforge:refer";
    private static readonly List<string> FullTabOptions = new()
    {
        "item",
        "recipe",
        "effect",
        "piece",
        "all"
    };
    private static readonly List<string> ReferenceTabOptions = new()
    {
        "item",
        "recipe",
        "effect",
        "piece",
        "pieceCategory",
        "material",
        "all"
    };
    private static bool _registered;

    private delegate bool TryRegenerateReference(
        out string path,
        out bool changed,
        out string error);

    internal static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        new Terminal.ConsoleCommand(
            WriteFullCommandName,
            "Write DataForge full scaffold YAML files with explicit defaults. Usage: dataforge:full [item|recipe|effect|piece|all]",
            WriteFullScaffoldFiles,
            optionsFetcher: GetFullTabOptions,
            onlyServer: true,
            remoteCommand: true,
            onlyAdmin: true);
        new Terminal.ConsoleCommand(
            WriteReferenceCommandName,
            "Regenerate DataForge compact reference files. Usage: dataforge:refer [item|recipe|effect|piece|pieceCategory|material|all]",
            WriteReferenceFiles,
            optionsFetcher: GetReferenceTabOptions,
            onlyServer: true,
            remoteCommand: true,
            onlyAdmin: true);
    }

    private static List<string> GetFullTabOptions()
    {
        return FullTabOptions;
    }

    private static List<string> GetReferenceTabOptions()
    {
        return ReferenceTabOptions;
    }

    private static void WriteFullScaffoldFiles(Terminal.ConsoleEventArgs args)
    {
        if (!TryParseScope(args, out bool includeItem, out bool includeRecipe, out bool includeEffect, out bool includePiece))
        {
            return;
        }

        if (includeItem)
        {
            if (ItemOverrideManager.TryWriteFullScaffoldConfigurationFile(out string itemPath, out string itemError))
            {
                args.Context?.AddString($"Wrote item full scaffold to {itemPath}");
            }
            else
            {
                args.Context?.AddString(itemError);
            }
        }

        if (includeRecipe)
        {
            if (RecipeOverrideManager.TryWriteFullScaffoldConfigurationFile(out string recipePath, out string recipeError))
            {
                args.Context?.AddString($"Wrote recipe full scaffold to {recipePath}");
            }
            else
            {
                args.Context?.AddString(recipeError);
            }
        }

        if (includeEffect)
        {
            if (StatusEffectOverrideManager.TryWriteFullScaffoldConfigurationFile(out string effectPath, out string effectError))
            {
                args.Context?.AddString($"Wrote effect full scaffold to {effectPath}");
            }
            else
            {
                args.Context?.AddString(effectError);
            }
        }

        if (includePiece)
        {
            if (PieceOverrideManager.TryWriteFullScaffoldConfigurationFile(out string piecePath, out string pieceError))
            {
                args.Context?.AddString($"Wrote piece full scaffold to {piecePath}");
            }
            else
            {
                args.Context?.AddString(pieceError);
            }
        }
    }

    private static void WriteReferenceFiles(Terminal.ConsoleEventArgs args)
    {
        string scope = args.Length >= 2 ? (args[1] ?? "").Trim().ToLowerInvariant() : "all";
        if (scope.Length == 0)
        {
            scope = "all";
        }

        switch (scope)
        {
            case "all":
                WriteReferenceResult(args, "item", ItemOverrideManager.TryRegenerateReferenceFile);
                WriteReferenceResult(args, "recipe", RecipeOverrideManager.TryRegenerateReferenceFile);
                WriteReferenceResult(args, "effect", StatusEffectOverrideManager.TryRegenerateReferenceFile);
                WriteReferenceResult(args, "piece", PieceOverrideManager.TryRegenerateReferenceFile);
                WriteReferenceResult(args, "piece category", PieceOverrideManager.TryRegeneratePieceCategoryReferenceFile);
                WriteReferenceResult(args, "material", MaterialReferenceWriter.TryRegenerateReferenceFile);
                return;
            case "item":
            case "items":
                WriteReferenceResult(args, "item", ItemOverrideManager.TryRegenerateReferenceFile);
                return;
            case "recipe":
            case "recipes":
                WriteReferenceResult(args, "recipe", RecipeOverrideManager.TryRegenerateReferenceFile);
                return;
            case "effect":
            case "effects":
                WriteReferenceResult(args, "effect", StatusEffectOverrideManager.TryRegenerateReferenceFile);
                return;
            case "piece":
            case "pieces":
                WriteReferenceResult(args, "piece", PieceOverrideManager.TryRegenerateReferenceFile);
                return;
            case "piececategory":
            case "piece-category":
            case "category":
            case "categories":
                WriteReferenceResult(args, "piece category", PieceOverrideManager.TryRegeneratePieceCategoryReferenceFile);
                return;
            case "material":
            case "materials":
                WriteReferenceResult(args, "material", MaterialReferenceWriter.TryRegenerateReferenceFile);
                return;
            default:
                args.Context?.AddString($"Syntax: {WriteReferenceCommandName} [item|recipe|effect|piece|pieceCategory|material|all]");
                return;
        }
    }

    private static void WriteReferenceResult(
        Terminal.ConsoleEventArgs args,
        string label,
        TryRegenerateReference regenerate)
    {
        if (!regenerate(out string path, out bool changed, out string error))
        {
            args.Context?.AddString(error);
            return;
        }

        args.Context?.AddString(changed
            ? $"Wrote {label} reference to {path}"
            : $"{label} reference is already up to date at {path}");
    }

    private static bool TryParseScope(
        Terminal.ConsoleEventArgs args,
        out bool includeItem,
        out bool includeRecipe,
        out bool includeEffect,
        out bool includePiece)
    {
        string scope = args.Length >= 2 ? (args[1] ?? "").Trim().ToLowerInvariant() : "all";
        if (scope.Length == 0)
        {
            scope = "all";
        }

        switch (scope)
        {
            case "all":
                includeItem = true;
                includeRecipe = true;
                includeEffect = true;
                includePiece = true;
                return true;
            case "item":
            case "items":
                includeItem = true;
                includeRecipe = false;
                includeEffect = false;
                includePiece = false;
                return true;
            case "recipe":
            case "recipes":
                includeItem = false;
                includeRecipe = true;
                includeEffect = false;
                includePiece = false;
                return true;
            case "effect":
            case "effects":
                includeItem = false;
                includeRecipe = false;
                includeEffect = true;
                includePiece = false;
                return true;
            case "piece":
            case "pieces":
                includeItem = false;
                includeRecipe = false;
                includeEffect = false;
                includePiece = true;
                return true;
            default:
                includeItem = false;
                includeRecipe = false;
                includeEffect = false;
                includePiece = false;
                args.Context?.AddString($"Syntax: {WriteFullCommandName} [item|recipe|effect|piece|all]");
                return false;
        }
    }
}
