// .snp back-annotation into the schematic (brief-L7b §5, R-cpl-11/12/13/14).
//
// This is the half that makes L7b visible to a user rather than an engine improvement: an EM-derived
// .snp drops into a schematic and an HB testbench runs against it.
//
// R-cpl-11 — ONE action, and it places or updates an ORDINARY SnP component. Not a new component
// type, not a new analysis kind: set `File` and `NumPorts` on the SnP that already exists, exactly as
// §10.8's co-simulation story assumes. ComponentTypeRegistry.DefaultParameters(SymbolKind.Snp, n)
// already declares everything needed, so there is nothing to add to the component model.
//
// Framework-free by rule (R-em-1): nothing under src/Ui/Layout/Em/ references Avalonia or SkiaSharp.
// This file returns IUiCommands and never executes them — the same contract
// SchematicToLayoutGenerator.Run and LayoutToSchematicGenerator.Run already keep.

using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>What back-annotation did, on the same terms the schematic↔layout generators report.</summary>
/// <param name="Command">
/// The undoable change, or null when the schematic already says exactly this — a re-run that changed
/// nothing must not dirty the document (R-cpl-12's idempotency, taken all the way).
/// </param>
/// <param name="ComponentName">The SnP component placed or updated.</param>
/// <param name="Created">True when a component was placed, false when an existing one was updated.</param>
/// <param name="StoredRef">The value written into the component's <c>File</c> parameter (R-cpl-13).</param>
/// <param name="Notes">Anything the user should be told — an external reference, a stale result.</param>
public sealed record EmBackAnnotationResult(
    IUiCommand?           Command,
    string                ComponentName,
    bool                  Created,
    string                StoredRef,
    IReadOnlyList<string> Notes)
{
    public bool NothingChanged => Command is null;
}

/// <summary>§5 — place-or-update the <c>SnP</c> component that reads an EM setup's <c>.snp</c>.</summary>
public static class EmBackAnnotation
{
    /// <summary>Where a freshly-created SnP lands when the caller has no better idea.</summary>
    private const double DefaultX = 0, DefaultY = 0;

    /// <summary>
    /// The deterministic instance name for a setup. <b>This is half of R-cpl-12's stable key.</b> A
    /// re-run must find the component it created last time, and the setup's own name is the only
    /// identity that survives the <c>.snp</c> being written to a DIFFERENT path — which happens for
    /// real: adding a second conductor turns an <c>.s2p</c> into an <c>.s4p</c>, so a path-only key
    /// would silently place a second component beside the first.
    /// </summary>
    public static string ComponentNameFor(string setupName)
    {
        var sb = new System.Text.StringBuilder("EM_");
        foreach (char c in setupName)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        string s = sb.ToString();
        return s.Length > 3 ? s : "EM_1";     // a setup named entirely of punctuation still gets a name
    }

    /// <summary>
    /// Place or update the <c>SnP</c> component that reads <paramref name="snpAbsolutePath"/>.
    ///
    /// <para><b>R-cpl-12 — idempotent, keyed on something stable.</b> Matching is two steps, the same
    /// confident-then-conservative shape <c>KitPaletteMerge</c> already uses: first the deterministic
    /// name this action mints (survives the file path changing), then any SnP already pointing at
    /// this exact file (survives the user renaming the component). Only if neither matches is a
    /// component created.</para>
    ///
    /// <para><b>R-cpl-13 — the stored path follows <see cref="WorkspaceRefs"/>.</b> Workspace-relative
    /// inside the workspace, absolute outside, separators normalised to <c>/</c>. That is also what
    /// the RUN path needs: <c>NetExtractor</c> emits <c>File</c> verbatim into <c>netlist.cnl</c>,
    /// which is written to the workspace root, and <c>CnlReader</c> resolves a relative SnP path
    /// against that file's own directory — so workspace-relative is exactly right, and an absolute
    /// path would be what fails to travel.</para>
    /// </summary>
    /// <param name="portCount">
    /// The SOLVED port count, not a sniff. The kernel already knows it, and taking it from there
    /// means a freshly-created component draws the right number of pins before anything has read the
    /// file off disk.
    /// </param>
    /// <param name="workspaceRootDir">Null when no workspace is open — the ref is then absolute, and
    /// said so in the notes rather than silently stored in a form that will not travel.</param>
    /// <param name="stalenessNote">
    /// R-cpl-14: the R-em-20 staleness warning, when the run produced one. Once a schematic points at
    /// the <c>.snp</c>, that warning stops being about a file nobody references and starts telling
    /// the user their SIMULATION RESULTS were computed from a cross-section that no longer exists —
    /// so it is surfaced here too, on the schematic side, not only on the EM panel.
    /// </param>
    public static EmBackAnnotationResult Annotate(
        SchematicEditModel schematic,
        string             snpAbsolutePath,
        int                portCount,
        string             setupName,
        string?            workspaceRootDir,
        string?            stalenessNote = null,
        double             x = DefaultX,
        double             y = DefaultY)
    {
        ArgumentNullException.ThrowIfNull(schematic);
        ArgumentException.ThrowIfNullOrEmpty(snpAbsolutePath);

        var notes = new List<string>();
        string storedRef = WorkspaceRefs.ToStoredRef(snpAbsolutePath, workspaceRootDir);
        string wanted    = ComponentNameFor(setupName);

        if (WorkspaceRefs.IsExternal(storedRef, workspaceRootDir))
            notes.Add(
                $"The s-parameter file '{storedRef}' is outside this workspace, so it is stored as an " +
                "absolute path and will not travel if the workspace is shared or moved. Point the EM " +
                "setup's output inside the workspace to make the reference portable.");
        else if (workspaceRootDir is null)
            notes.Add(
                $"No workspace is open, so '{storedRef}' is stored as an absolute path. It will " +
                "resolve on this machine only.");

        if (stalenessNote is { Length: > 0 })
            notes.Add(stalenessNote);

        var existing = FindExisting(schematic, wanted, snpAbsolutePath, workspaceRootDir);

        if (existing is not null)
        {
            var fileParam  = existing.Parameters.FirstOrDefault(p => p.Name == "File");
            var portsParam = existing.Parameters.FirstOrDefault(p => p.Name == "NumPorts");

            bool fileSame  = fileParam?.Expression == storedRef;
            bool portsSame = portsParam is null || portsParam.Expression == portCount.ToString();

            if (fileSame && portsSame)
            {
                notes.Add($"'{existing.InstanceName}' already reads '{storedRef}' — nothing to update.");
                return new EmBackAnnotationResult(null, existing.InstanceName, false, storedRef, notes);
            }

            return new EmBackAnnotationResult(
                new SetSnpReferenceCommand(schematic, existing, storedRef, portCount),
                existing.InstanceName, Created: false, storedRef, notes);
        }

        // Create. The name we minted is preferred; if something else already owns it, fall back to
        // the ordinary auto-naming rather than colliding.
        string name = schematic.Components.Any(c =>
                          string.Equals(c.InstanceName, wanted, StringComparison.Ordinal))
            ? SchematicEditModel.NextAvailableName(schematic.Components, "SNP")
            : wanted;

        var comp = new EditableComponent
        {
            Symbol       = SymbolKind.Snp,
            InstanceName = name,
            X            = x,
            Y            = y,
        };

        // R-cpl-11: an ORDINARY SnP — its own declared defaults, with File and NumPorts filled in.
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Snp, portCount))
            comp.Parameters.Add(new EditableParameter
            {
                Name            = dp.Name,
                Expression      = dp.Name switch
                {
                    "File"     => storedRef,
                    "NumPorts" => portCount.ToString(),
                    _          => dp.Expression,
                },
                Unit            = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic,
                Dimension       = dp.Dimension,
            });

        return new EmBackAnnotationResult(
            new PlaceComponentCommand(schematic, comp), name, Created: true, storedRef, notes);
    }

    /// <summary>R-cpl-12's two-step match. Name first — it is the identity this action mints, and it
    /// is the one that survives the file path changing when the port count does.</summary>
    private static EditableComponent? FindExisting(
        SchematicEditModel schematic, string wantedName, string snpAbs, string? workspaceRootDir)
    {
        foreach (var c in schematic.Components)
            if (c.Symbol == SymbolKind.Snp &&
                string.Equals(c.InstanceName, wantedName, StringComparison.Ordinal))
                return c;

        string target = Normalise(snpAbs);
        foreach (var c in schematic.Components)
        {
            if (c.Symbol != SymbolKind.Snp) continue;
            if (c.Parameters.FirstOrDefault(p => p.Name == "File")?.Expression is not { Length: > 0 } f)
                continue;

            string abs = Path.IsPathRooted(f)
                ? f
                : workspaceRootDir is { Length: > 0 } root ? WorkspaceRefs.Resolve(f, root) : f;

            if (string.Equals(Normalise(abs), target, StringComparison.OrdinalIgnoreCase)) return c;
        }
        return null;
    }

    private static string Normalise(string p)
    {
        try { return Path.GetFullPath(p).Replace('\\', '/'); }
        catch { return p.Replace('\\', '/'); }
    }
}

/// <summary>
/// Sets an existing SnP's <c>File</c> and <c>NumPorts</c> together, undoably.
///
/// <para>Deliberately NOT <see cref="SetSnpFileCommand"/>: that command re-SNIFFS the port count off
/// disk, resolving a relative path against the SCHEMATIC's directory — while the engine resolves the
/// same string against the workspace root (<c>netlist.cnl</c>'s own directory). For a schematic in a
/// sub-folder those two bases differ, so the sniff can quietly fail and leave <c>NumPorts</c> at its
/// previous value. Back-annotation does not need to sniff anything: the kernel just solved the
/// problem and knows the port count exactly.</para>
/// </summary>
internal sealed class SetSnpReferenceCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableParameter? _file, _ports;
    private readonly string _newFile, _oldFile, _newPorts, _oldPorts;

    public string Description => "Back-annotate EM s-parameters";

    public SetSnpReferenceCommand(
        SchematicEditModel model, EditableComponent comp, string storedRef, int portCount)
    {
        _model    = model;
        _file     = comp.Parameters.FirstOrDefault(p => p.Name == "File");
        _ports    = comp.Parameters.FirstOrDefault(p => p.Name == "NumPorts");
        _oldFile  = _file?.Expression  ?? "";
        _oldPorts = _ports?.Expression ?? "";
        _newFile  = storedRef;
        _newPorts = portCount.ToString();
    }

    public void Execute() => Apply(_newFile, _newPorts);
    public void Undo()    => Apply(_oldFile, _oldPorts);

    private void Apply(string file, string ports)
    {
        if (_file  is not null) _file.Expression  = file;
        if (_ports is not null) _ports.Expression = ports;
        _model.NotifyChanged();
    }
}
