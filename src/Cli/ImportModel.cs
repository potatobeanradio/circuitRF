// `circuitrf import model <model folder> --into <workspace>` — Copy to Workspace, headlessly (brief vendorlib-12 R-vl12-4).
//
// THERE IS NO COPY HERE. The copy, the reference rewrite and the CopiedFrom record are VendorModelAuthoring.CopyToWorkspace
// in src/Design, the function the GUI's Copy to Workspace… calls — `Authoring.cs`' rule. What is here is argument
// parsing, refusals and reporting.
//
// A noun of its own on `import`, not `import part`: `import part` is ComponentImport's contract — a symbol or footprint
// file READ into a new cell, its layers reconciled against a technology, with --cell/--variant/--tech/--add-layers
// meaning things a model folder does not have. A model folder is already a cell; copying it is a different operation,
// and bending ComponentImport to take one would give five flags that silently mean nothing. `import part` handed a
// model folder refuses and names this spelling.
//
// The GUI's follow-up question — point this instance, or every one, at the copy — has no headless spelling: a headless
// caller rewrites the reference in the .csch, because the format is the contract.

using CircuitRF.Design.VendorLibraries.ModelLibrary;

namespace CircuitRF.Cli;

public static class ImportModel
{
    public const string UsageText = "       circuitrf import model <model folder> --into <workspace-or-dir>";

    public static int Run(string[] args)
    {
        string? source = null, into = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--into" when i + 1 < args.Length: into = args[++i]; break;
                default:
                    if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImportUnknownOption(args[i])); return Usage(); }
                    if (source is not null) { JsonRun.Report(CliDiagnostics.ImportMultipleSources()); return Usage(); }
                    source = args[i];
                    break;
            }
        }
        if (source is null) { JsonRun.Report(CliDiagnostics.ImportSourceRequired()); return Usage(); }
        JsonRun.InputPath = source;
        if (!Directory.Exists(source)) return JsonRun.Fail(CliDiagnostics.ImportSourceNotFound(source));
        if (!ModelFolder.Is(source))
            return JsonRun.Fail(CliDiagnostics.ImportRefused(
                $"'{source}' is not a model folder (it holds no {ModelFolder.PartFileName})."));
        if (into is null) { JsonRun.Report(CliDiagnostics.ImportIntoRequired()); return Usage(); }

        // `--into` as `import part` reads it: a workspace root, a folder inside one, or its .cws.
        string parentDir = Path.GetFullPath(into);
        if (File.Exists(parentDir) && Path.GetFileName(parentDir).Equals(".cws", StringComparison.OrdinalIgnoreCase))
            parentDir = Path.GetDirectoryName(parentDir)!;
        if (!Directory.Exists(parentDir)) return JsonRun.Fail(CliDiagnostics.NewParentNotFound(parentDir));

        var result = VendorModelAuthoring.CopyToWorkspace(source, parentDir);
        if (!result.Succeeded) return JsonRun.Fail(CliDiagnostics.ImportRefused(result.Refusal ?? result.Message));

        Console.WriteLine(result.CellDir);
        JsonRun.AddOutput("cell", result.CellDir!);
        Console.Error.WriteLine($"[circuitRF] {result.Message}");
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage:" + UsageText[6..]);
        return 1;
    }
}
