// HZN-2 R-hzn2-1 — one `.hzn` 3D design turned into a `C3dDocument` held in memory.
//
// Opening, converting, running on circuitRF's EMerge and placing as an instance all start here. The translation
// either returns a document with its in-memory technology and a PROVENANCE MAP (brief 5 saves through it), or
// it returns the refusal: every item circuitRF cannot represent, grouped by kind, with the objects that use each
// (brief-hzn-0 D3). Nothing opens half a design, so the walk keeps going after the first unsupported item.
//
// Clean room (brief-hzn-0 D1): what each property means comes from Hertzon's documentation, its example files and
// `hertzon-cli describe`; src/Design/RESOLVED.md §HZN records each meaning and the experiment behind it.

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>One step of a Hertzon object's history, as the translation read it.</summary>
public sealed record HznStepOrigin(int Index, string Kind, bool Suppressed, bool Applied);

/// <summary>Where a <c>.c3d</c> object came from: the Hertzon object, each of its steps (the suppressed ones too, so
/// brief 5 can write them back), and the objects its booleans consumed.</summary>
public sealed record HznObjectOrigin(string HznObject, IReadOnlyList<HznStepOrigin> Steps, IReadOnlyList<string> Consumed,
                                     string? RenamedFrom);

/// <summary>A <c>.c3d</c> port and the excitation (and sheet object) it came from.</summary>
public sealed record HznPortOrigin(int Number, string Excitation, string? SheetObject);

/// <summary>A <c>.c3d</c> face boundary and the boundary, face reference or object it came from.</summary>
public sealed record HznBoundaryOrigin(string Boundary, string Object, string Face, int? FaceIndex);

/// <summary>A <c>.c3d</c> variable and the Hertzon variable it came from (<see cref="Project"/> true: a project variable,
/// which a <c>.c3d</c> holds under a reserved prefix).</summary>
public sealed record HznVariableOrigin(string C3dName, string HznName, bool Project);

/// <summary>A setting a setup or object states that circuitRF's run does not use, with its value: brief 7 reports it.</summary>
public sealed record HznUnusedSetting(string Owner, string Key, string Value);

/// <summary>Everything a save needs to find its way back (brief 5).</summary>
public sealed class HznProvenance
{
    /// <summary>By <c>.c3d</c> object name — top-level objects and the tools folded into them.</summary>
    public Dictionary<string, HznObjectOrigin> Objects { get; } = new(StringComparer.Ordinal);
    public List<HznPortOrigin> Ports { get; } = [];
    public List<HznBoundaryOrigin> FaceBoundaries { get; } = [];
    /// <summary>Boundaries and objects that became the setup's air box faces: (boundary, face of the air box).</summary>
    public List<(string Boundary, string AirBoxFace)> AirBoxFaces { get; } = [];
    public List<HznVariableOrigin> Variables { get; } = [];
    /// <summary><c>.c3d</c> setup name → the Hertzon setup (or <c>setup/sweep</c>) it came from.</summary>
    public Dictionary<string, string> Setups { get; } = new(StringComparer.Ordinal);
    /// <summary>The <c>CreateRegion</c> object that became the air box, if any.</summary>
    public string? RegionObject { get; set; }
    public List<HznUnusedSetting> Unused { get; } = [];
}

/// <summary>What <see cref="HznToC3d.Translate"/> produced.</summary>
public sealed record C3dTranslation(string Design, C3dDocument? Document, Technology? Technology, HznProvenance Provenance,
                                    IReadOnlyList<HznRefusalItem> Refusals, IReadOnlyList<string> Notes, Diagnostic? Error)
{
    public bool Ok => Document is not null;

    /// <summary>The refusal as one diagnostic, or null when the design translated.</summary>
    public Diagnostic? Refusal => Error ?? (Refusals.Count > 0 ? HznTranslationDiagnostics.Refused(Design, Refusals) : null);
}

public static class HznToC3d
{
    /// <summary>The database units per micrometre a translated document uses (1 DBU = 1 nm).</summary>
    public const int DbuPerMicron = 1000;

    /// <summary>
    /// Translates the 3D design <paramref name="designName"/> of <paramref name="project"/>. Reads no file. Resolving a
    /// face named by index past a primitive needs the geometry worker: <paramref name="kernel"/> (the shared one when
    /// null) is asked only then.
    /// </summary>
    public static C3dTranslation Translate(HznProject project, string designName, GeometryKernel? kernel = null)
    {
        var provenance = new HznProvenance();
        var design = project.Designs.FirstOrDefault(d => string.Equals(d.Name, designName, StringComparison.Ordinal));
        if (design is null)
            return new(designName, null, null, provenance, [], [],
                       HznTranslationDiagnostics.NoSuchDesign(designName, [.. project.Designs.Select(d => d.Name ?? "")]));
        if (design.Kind == HznDesignKind.Circuit)
            return new(designName, null, null, provenance, [], [], HznTranslationDiagnostics.NotThreeD(designName));
        return new HznTranslator(project, design, provenance, kernel).Run();
    }

    /// <summary>The names of a project's 3D designs.</summary>
    public static IReadOnlyList<string> ThreeDDesigns(HznProject project) =>
        [.. project.Designs.Where(d => d.Kind == HznDesignKind.ThreeD).Select(d => d.Name ?? "")];
}
