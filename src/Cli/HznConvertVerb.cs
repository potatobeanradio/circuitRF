using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Diagnostics;

namespace CircuitRF.Cli;

/// <summary>
/// HZN-6 R-hzn6-2 — <c>convert &lt;file.hzn&gt; --design D -o &lt;cell folder&gt;</c>.
///
/// <para>Argument checks, refusals and reporting only. Every decision — the design, the cell, the materials, the STEP files —
/// is <see cref="HznConvert.Convert"/>'s, the function the Convert to .c3d dialog calls (a source scan holds it). The target is a
/// cell folder: it is created when absent and its 3D view is written when it exists, and a file-shaped target is refused rather
/// than guessed at.</para>
/// </summary>
internal static class HznConvertVerb
{
    /// <summary>The flags this source takes. They belong to it alone: any other source refuses them.</summary>
    internal sealed class Flags
    {
        public string? Design;
        public bool AddMaterials, ReplaceView;
        public bool Any => Design is not null || AddMaterials || ReplaceView;
    }

    /// <summary>True when <paramref name="path"/> is a Hertzon project, by extension or by content.</summary>
    internal static bool IsSource(string path) =>
        File.Exists(path) && (string.Equals(Path.GetExtension(path), HznCli.Extension, StringComparison.OrdinalIgnoreCase)
                              || HznCli.IsHertzon(path));

    internal static Diagnostic FlagsWithoutHertzon() => Diagnostic.Create(
        "convert.hzn.flags-without-hzn", DiagnosticSeverity.Error,
        "--design, --add-materials and --replace belong to a Hertzon project (.hzn) source.");

    internal static Diagnostic TargetNotACellFolder(string output) => Diagnostic.Create(
        "convert.hzn.target", DiagnosticSeverity.Error,
        "A Hertzon design converts into a cell folder, and '{output}' is shaped like a file. Name the cell folder: -o <workspace>/<cell>.",
        ("output", output));

    // A cell name may hold a dot; only the extensions of circuitRF's own and Hertzon's documents read as a file path.
    private static readonly string[] FileExtensions = [".c3d", ".hzn", ".clay", ".csch", ".csym", ".cem", ".ccell"];

    internal static int Run(string input, string? output, bool toGiven, Flags flags)
    {
        if (output is null)
        {
            JsonRun.Report(CliDiagnostics.ConvertOutputRequired());
            return 1;
        }
        if (toGiven || (!Directory.Exists(output) && FileExtensions.Contains(Path.GetExtension(output), StringComparer.OrdinalIgnoreCase)))
            return JsonRun.Fail(TargetNotACellFolder(output));

        Console.Error.WriteLine("[circuitRF] hertzon -> 3d cell");
        var result = HznConvert.Convert(new HznConvertRequest(input, flags.Design, output, flags.AddMaterials, flags.ReplaceView),
                                        GeometryKernel.Shared);
        foreach (var d in result.Findings)
        {
            Console.Error.WriteLine((d.Severity == DiagnosticSeverity.Warning ? "warning: " : "note: ") + d.Render());
            JsonRun.Note(d);
        }
        if (result.Refusal is { } refusal) return JsonRun.Fail(refusal);

        if (result.AddedMaterials.Count > 0)
            Console.Error.WriteLine($"[circuitRF] {result.AddedMaterials.Count} material(s) added to {result.TechnologyPath}");
        Console.Error.WriteLine($"[circuitRF] wrote design '{result.Design}' to {result.ViewPath}" +
                                (result.ViewReplaced ? " (replaced)" : "") + (result.CellCreated ? " in a new cell" : ""));
        Console.WriteLine(result.CellDir);
        JsonRun.AddOutput("cell", result.CellDir!);
        Console.WriteLine(result.ViewPath);
        JsonRun.AddOutput("3d", result.ViewPath!);
        foreach (string step in result.StepFiles) { Console.WriteLine(step); JsonRun.AddOutput("step", step); }
        if (result.TechnologyPath is { } tech) JsonRun.AddOutput("ctech", tech);
        return 0;
    }
}
