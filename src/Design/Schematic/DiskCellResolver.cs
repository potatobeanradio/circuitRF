using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// <b>The headless half of hierarchy</b> — an <see cref="ICellResolver"/> that descends into a cell
/// instance by reading its cell folder off disk, for every caller that has no editor session to ask.
///
/// <para><b>Why it exists (2026-09-15).</b> <see cref="NetExtractor.Extract"/> takes the resolver as
/// an optional argument and treats a null one as "flat caller — skip silently", which is the right
/// answer for a caller extracting a single schematic that cannot contain a cell instance and the
/// wrong one for everything else. Both CLI extraction sites passed null. The result was not a
/// failure: a design whose device lives in a sub-cell netlisted, checked clean and RAN, as the
/// passive network left behind once the device was dropped. A power amplifier came back at
/// −72 dB gain with <c>Converged = 1</c> on every point, and nothing anywhere said a component had
/// been left out. <c>circuitrf check</c> reported zero errors on the same design, because it
/// extracted the same way.</para>
///
/// <para><b>It is the GUI's own descent, not a second one.</b> <c>WorkspaceViewModel.Resolve</c> is
/// this function plus one thing this cannot have: memory-else-disk, so an unsaved edit in an open
/// tab is what the run sees. Everything else — <see cref="HierarchyResolver.ResolvePrimaryPath"/>,
/// the cell folder two levels up from the primary, the <c>.ccell</c> parameter interface, and the
/// absolute folder as the cell KEY — is shared, and the two are compared directly by
/// <c>tests/Ui.Tests/Cli/CliHierarchyExtractionTests.cs</c>. A headless resolver that resolved
/// differently from the window's would be a second product, which is the failure
/// <c>brief-automation-3-authoring-verbs.md</c> R-aut-2 is about.</para>
///
/// <para><b>Stateless and cheap.</b> A cell instantiated twenty times is read twenty times; the
/// extractor's own <c>CellScope</c> is what stops the elaborated library gaining twenty copies. A
/// cache here would have to be invalidated by something, and there is nothing in a one-shot process
/// to invalidate it with.</para>
/// </summary>
public sealed class DiskCellResolver : ICellResolver
{
    /// <summary>The shared instance. Nothing here is mutable, so one is enough.</summary>
    public static readonly DiskCellResolver Instance = new();

    /// <inheritdoc/>
    public CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel)
    {
        if (HierarchyResolver.ResolvePrimaryPath(cellInstance, containingModel) is not { } primaryPath)
            return null;

        SchematicEditModel schematic;
        try
        {
            var (model, _, _) = SchematicPersistence.LoadFromFile(primaryPath);
            schematic = model;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or System.Text.Json.JsonException)
        {
            // Unreadable is unresolvable. The extractor's own conflict note names the instance and
            // the cell, which is what a caller can act on; swallowing the reader's message here and
            // reporting nothing would be the very failure this class was written to remove.
            return null;
        }

        // SchematicDirectory is what a NESTED cell instance's own relative CellRef resolves against.
        // Set exactly as Open and Push-In set it — without it the descent stops one level down, and
        // stops silently, because an unresolvable reference is skipped rather than reported.
        schematic.SchematicDirectory = Path.GetDirectoryName(primaryPath);
        // Carried down so a loose cell reached from a loose top is looked for in the same workspace.
        // Inert for a cell inside a workspace, which is where a host lookup almost always lands it.
        schematic.HostWorkspaceRoot = containingModel.HostWorkspaceRoot;

        // …/<cell>/schematic/<file>.csch → the cell folder is two levels up.
        string cellDir  = Path.GetDirectoryName(Path.GetDirectoryName(primaryPath))!;
        string cellName = Path.GetFileName(cellDir);

        return new CellResolution(cellName, schematic, ParametersOf(cellDir), Path.GetFullPath(cellDir), CellCorners.Of(cellDir));
    }

    /// <summary>The cell's declared parameter interface, from its <c>.ccell</c>.</summary>
    /// <remarks>A malformed or absent <c>.ccell</c> yields no declared parameters rather than a
    /// refusal — the instance's own overrides still apply, which is exactly what the window does with
    /// the same file.</remarks>
    internal static IReadOnlyList<ParameterDeclaration> ParametersOf(string cellDir)
    {
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return [];

        try
        {
            return CellPersistence.LoadFromFile(ccellPath).Parameters
                .Select(p => new ParameterDeclaration(
                    p.Name,
                    p.DefaultExpression,
                    string.IsNullOrEmpty(p.Unit) ? null : p.Unit,
                    hidden: !p.ShowOnSchematic))
                .ToList();
        }
        catch { return []; }
    }
}
