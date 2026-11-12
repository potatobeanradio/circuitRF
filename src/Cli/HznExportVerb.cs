using CircuitRF.Design.Cells;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Diagnostics;

namespace CircuitRF.Cli;

/// <summary>
/// HZN-4 R-hzn4-4 — <c>circuitrf convert pkg.c3d -o out.hzn</c> and <c>convert pkg.c3d --into project.hzn</c>: a 3D view written as a
/// Hertzon design, in a project of its own or appended to an existing one. What File ▸ Export ▸ Hertzon Project… writes, written in a batch.
///
/// <para><b>This file decides nothing about the file.</b> What is in it is <see cref="HznExport"/>'s — the function the dialog calls — and
/// what a design is made of is <see cref="C3dToHzn"/>'s. What is here is argument checks, the cell-folder lookup, and the report (a source
/// scan holds it).</para>
/// </summary>
internal static class HznExportVerb
{
    public static int Export(string input, string? output, string? into, string? designName, string? workspace)
    {
        Console.Error.WriteLine("[circuitRF] 3d -> Hertzon project");
        if (output is not null && into is not null) return JsonRun.Fail(HznExportVerbDiagnostics.OutputAndInto());
        if (output is null && into is null) return JsonRun.Fail(CliDiagnostics.ConvertOutputRequired());

        string source = Path.GetFullPath(input);
        if (Directory.Exists(source) && DocumentKinds.LooksLikeCellFolder(source))
        {
            if (C3dHierarchy.ViewFile(source, C3dInstanceView.ThreeD) is not { } view)
                return JsonRun.Fail(HznExportVerbDiagnostics.NoThreeDView(source));
            source = view;
        }
        var kind = DocumentKinds.Classify(source);
        if (kind != DocumentKind.ThreeD) return JsonRun.Fail(HznExportVerbDiagnostics.NotAThreeDView(input, DocumentKinds.Name(kind)));

        string? cws = workspace is not null ? Path.GetFullPath(workspace) : DocumentKinds.AncestorCws(source);
        var design = HznExport.FromFile(source, designName, cws);
        var outcome = into is null ? HznExport.ToNewFile(design, output!) : HznExport.IntoExisting(design, Path.GetFullPath(into));

        foreach (string note in outcome.Notes)
        {
            Console.Error.WriteLine($"note: {note}");
            JsonRun.Note(CliDiagnostics.ConvertNote(note));
        }
        if (outcome.Error is { } error) return JsonRun.Fail(error);
        Console.Error.WriteLine($"[circuitRF] design '{outcome.Design}' written");
        Console.WriteLine(outcome.Path);
        JsonRun.AddOutput("hzn", outcome.Path!);
        return 0;
    }
}

/// <summary>The sentences <see cref="HznExportVerb"/> refuses with.</summary>
internal static class HznExportVerbDiagnostics
{
    public static Diagnostic OutputAndInto() => Diagnostic.Create(
        "convert.hzn.output-and-into", DiagnosticSeverity.Error,
        "A Hertzon export is either a new project (-o out.hzn) or a design added to one (--into project.hzn), not both.");

    public static Diagnostic DesignNameWithoutHzn() => Diagnostic.Create(
        "convert.hzn.design-name", DiagnosticSeverity.Error,
        "--design-name names the design a Hertzon export writes; it needs a .hzn target (-o out.hzn or --into project.hzn).");

    public static Diagnostic NotAThreeDView(string path, string kind) => Diagnostic.Create(
        "convert.hzn.source", DiagnosticSeverity.Error,
        "'{path}' is {kind}; a Hertzon design is written from a 3D view (.c3d) or a cell that has one.",
        ("path", path), ("kind", kind));

    public static Diagnostic NoThreeDView(string cellDir) => Diagnostic.Create(
        "convert.hzn.no-3d-view", DiagnosticSeverity.Error,
        "Cell '{cell}' has no 3D view to write as a Hertzon design.",
        ("cell", Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
}
