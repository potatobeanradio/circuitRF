// brief-em3d-42 R-em3d42-3/-4 — a .c3d elaborated: its objects and every instance below it, as the neutral
// problem's solids, sheets and materials in metres, with a provenance map back to the document.
//
// THE ONE RULE OF THE SERIES (overview §0): what the editor shows is what the solver gets. The editor will
// draw THIS, and the problem assembly (C3dProblemAssembly) solves THIS plus a setup. Elaboration is
// independent of any setup — the physics it needs (the sheet rule's top frequency, σ(T)'s temperature)
// comes in through C3dElaborationOptions, and with none a conductor with thickness is a solid.
//
// INSTANCES (R-em3d42-3c). A 3D view elaborates recursively; a layout view goes through Em3dLayoutSolids
// with the INSTANCE options and the layout's OWN technology (resolved from the layout's own path, never
// the parent's). ExternalWorkspaceGate is not consulted: that gate exists because a layout hierarchy
// matches layers by number, and nothing here matches by layer — every instance becomes metres and named
// materials before its parent sees it (overview §1i). The bottom of a layout's stackup sits at the
// instance's z.
//
// NAMES. An object inside an instance is `<instance>/<object>`, nested for depth; an array element is
// `<instance>[i,j,k]/<object>` (brief 49's ports name sub-cell conductors this way). Objects come first in
// construction order, then instances in theirs, each instance's content keeping its own relative order.
//
// MATERIALS (R-em3d42-3e). Each object's material resolves in its own document's technology. Equal values
// under one name merge; different values are kept as `<name>@<technology>` with a note listing both —
// a silent merge would give one of them the wrong conductivity, and nothing would look wrong. The
// technology is named by its .ctech file's stem (its display name may hold spaces and commas).
//
// BOND WIRES (brief-em3d-50). A drawn Wire resolves AFTER its document's objects and instances, because its ends land
// on conductors anywhere below it — a die pad in `U1`, a lead in the parent (C3dWires: the pad lookup, then the same
// Em3dWires.Resolve a .wBond's wires use). A wire that lands on no pad is refused by name and end, and the rest of the
// document still elaborates, so the editor can draw it and flag it (WireRefusals).
//
// KERNEL OBJECTS (brief-em3d-64 R-em3d64-3). A Boolean, Fillet, Chamfer or Step object is resolved, turned into the
// canonical tree (GeometryKernelTree), built by the geometry worker and lowered to ONE ordinary Em3dSolid whose primitive
// is an Em3dShapeSolid — material and role its Blank's. A build that fails is THAT object's refusal, never the
// document's, and never an undo: the rest elaborates and the edit stays. A DISABLED operation is as if it were not
// there: its operands elaborate as independent objects at its place in construction order, with no worker call. A
// document with no kernel object makes no worker call at all — the kernel is not even asked whether it is there.
//
// CACHING (R-em3d42-4), because the editor calls this on every edit. An elaborator instance keeps a
// per-OBJECT cache keyed by the object's serialized form, its world transform and its document's scale,
// and a per-CHILD cache keyed by (file, file stamp, view, technology stamp) — brief 28's rule: a file
// re-written at the same path is not the same file. ObjectsElaborated and ChildrenElaborated count
// misses, which is what gate 7 reads.

using System.Globalization;
using System.Text;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using WireMaterial = CircuitRF.WBond.WireMaterial;

namespace CircuitRF.Design.ThreeD;

/// <summary>The physics elaboration needs from a setup, or its defaults with none.</summary>
/// <param name="FMaxHz">The top frequency, for the sheet rule inside a layout instance; null makes every
/// conductor with thickness a solid.</param>
/// <param name="TempC">The temperature σ(T) is evaluated at.</param>
public sealed record C3dElaborationOptions(double? FMaxHz = null, double TempC = EmSetup.DefaultOperatingTempC)
{
    /// <summary>brief-em3d-51 — <c>explain --set</c>: names bound over the top document's scope before anything resolves.</summary>
    public IReadOnlyList<(string Name, string Expr)>? Sets { get; init; }

    /// <summary>brief-dfr28-3 — the run's setup's <see cref="EmSetup.RobustMeshing"/>, for every placed layout. Off (as drawn)
    /// for the editor's picture.</summary>
    public bool RobustMeshing { get; init; }

    /// <summary>brief-sar-2 R-sar2-3 — the one frequency every dispersive material (a tissue, a phantom liquid) is evaluated at
    /// before lowering, so each solver sees plain εr and σ. Null: the record's plain numbers (an editor preview, a thermal run).</summary>
    public double? MaterialFrequencyHz { get; init; }

    /// <summary>What <see cref="MaterialFrequencyHz"/> is, for the run's one note: <c>the sweep's centre</c>.</summary>
    public string? MaterialFrequencyBasis { get; init; }

    /// <summary>brief-em3d-51 — the top document's cell, when the caller holds one the disk does not yet say (the editor's
    /// preview of a drag that writes a parameter's default). Null: read from the document's own cell folder.</summary>
    public C3dCell? Cell { get; init; }

    /// <summary>A picture, never a solve: a placed layout with wires and no conductor artwork, or a wire end over no
    /// conductor, is drawn (<see cref="Em3dLayoutSolidsOptions.PadsOptional"/>) with what a solve would refuse on as a
    /// warning. Without it a wBond pushed from a schematic to a layout made its whole instance "not found".</summary>
    public bool PadsOptional { get; init; }
}

/// <summary>Where one object of the elaborated problem came from (R-em3d42-3a).</summary>
/// <param name="InstancePath">The instance path, <c>U1/U3</c> or <c>U1[0,1,0]</c>; empty for the document's own objects.</param>
/// <param name="DocumentPath">The file the object is in — the <c>.c3d</c>, or the placed <c>.clay</c>.</param>
/// <param name="ObjectName">Its name in that file (a layout's solid name for a layout instance).</param>
/// <param name="FaceNames">Its faces' names, indexed by the neutral primitive's face numbering.</param>
public sealed record C3dProvenance(string InstancePath, string DocumentPath, string ObjectName, IReadOnlyList<string> FaceNames)
{
    /// <summary>
    /// brief-em3d-44 R-em3d44-4 — whether an integer point of the object's own document lands on an integer DBU
    /// point of the TOP document: every placement from it to the top is integral (translations and quarter
    /// turns) and every document on the way has the top's DBU per µm. A snap to such an object's corner is an
    /// exact DBU point; any other is metres, rounded only when used.
    /// </summary>
    public bool Exact { get; init; } = true;

    /// <summary>brief-em3d-44 R-em3d44-3b — for an object inside an instance, the element's transform (its
    /// document's metres to world metres); null for the document's own objects. Two objects of one child
    /// under transforms with the same rotation are the same mesh moved by the difference of translations.</summary>
    public C3dTransform? Element { get; init; }

    /// <summary>The document's top-level object this solid was elaborated from: itself for an ordinary object, the Boolean for
    /// a Tool it keeps (<c>KeepTools</c>) or a disabled operation's Tool, which are drawn as objects of their own and move,
    /// rotate and duplicate with it. Null where no top-level object applies (a wire, a layout instance's part).</summary>
    public string? TopObject { get; init; }

    /// <summary>brief-em3d-92 — the transparency, percent, of the top-level object this was elaborated from (an operation's, for
    /// its result and its kept Tools; a wire's for each of its solids); null for its kind's default, and for a layout's part.</summary>
    public int? Transparency { get; init; }

    /// <summary>brief-em3d-92 — the opacity the instances it sits in multiply onto it: the product of each one's
    /// (<see cref="C3dTransparency.Opacity"/>), 1 for the document's own objects.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>brief-em3d-105 — the appearance the top-level object this was elaborated from states (an operation's, for its
    /// result and its kept Tools; a wire's for each of its solids); null for none, and for a layout's part.</summary>
    public TechAppearance? Appearance { get; init; }

    /// <summary>brief-em3d-105 — the appearances the instances it sits in state, <b>innermost first</b> (the resolver's order);
    /// empty for the document's own objects.</summary>
    public IReadOnlyList<Appearance.AppearanceInstanceStatement> InstanceAppearances { get; init; } = [];
}

/// <summary>brief-em3d-66 — a preview's answer: the tree it was asked for, the shape as elaboration would lower it, the build
/// (its solid count is the result's piece count) and how many faces it has.</summary>
public sealed record C3dShapePreview(GeometryKernelTree Tree, Em3dShapeSolid Solid, GeometryKernelBuild Build, int Faces);

/// <summary>brief-em3d-64 R-em3d64-6b — one kernel object's build as <c>explain</c> reports it.</summary>
/// <param name="Operands">The operand tree, one line per node, indented two spaces per level.</param>
/// <param name="Built">True when the worker was asked; false when a cache answered.</param>
/// <param name="MinRadiusM">The smallest radius of curvature on any face or edge, metres; null when every one is flat.</param>
public sealed record C3dKernelBuild(string Name, string Kind, IReadOnlyList<string> Operands, bool Built, int Faces, int Edges,
                                    double? MinRadiusM, IReadOnlyList<string> Notes, string? Refusal)
{
    /// <summary>brief-em3d-66 — how many solids the result is: a lid cut in two is 2 pieces and still one object.</summary>
    public int Solids { get; init; }
}

/// <summary>brief-em3d-69 R-em3d69-2c — one placed element of an instance: its path (<c>U1</c>, <c>U1[0,1,0]</c>, <c>U1/U3</c>), its
/// document's metres to world metres, the placed file and its cell's name, and whether it is a layout. What STEP export's
/// <i>As assembly</i> builds its sub-assemblies from; the objects inside are those whose provenance names this path.</summary>
public sealed record C3dInstanceFrame(string Path, C3dTransform World, string DocumentPath, string CellName, bool Layout);

/// <summary>One step of the walk <c>explain</c> reports (R-em3d42-6): what, and how it was decided.</summary>
public sealed record C3dWalkStep(string Subject, string Detail);

/// <summary>A <c>.c3d</c>, elaborated. The geometry is in construction order, metres.</summary>
public sealed record C3dElaboration(
    IReadOnlyList<Em3dSolid>                      Solids,
    IReadOnlyList<Em3dSheet>                      Sheets,
    IReadOnlyList<Em3dMaterial>                   Materials,
    IReadOnlyDictionary<string, C3dProvenance>    Provenance,
    IReadOnlyList<string>                         Notes,
    IReadOnlyList<string>                         Refusals)
{
    /// <summary>No refusal — or brief-em3d-93, none but those raised by content that is not modelled, which no run refuses for
    /// (<see cref="NotModelledRefusals"/>; <see cref="Refusals"/> still lists them, for the editor to show).</summary>
    public bool Ok => Refusals.Count == 0 || Refusals.All(NotModelledRefusals.Contains);

    /// <summary>Warnings a layout instance's own build raised (a foot overhanging its pad…), and each object the solver
    /// ignores for having no material (3D editor bugs round 2).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Each conductor's nets, by object name: a layout instance's nets, and a drawn conductor's own
    /// name (a terminal names a drawn conductor by its name).</summary>
    public IReadOnlyDictionary<string, HashSet<string>> ObjectNets { get; init; } = new Dictionary<string, HashSet<string>>();

    /// <summary>brief-dfr28-5 — each layout instance's via that is on a net of its own and passes copper of another, by
    /// object name: what a static solve refuses (<see cref="Em3dGenerator.ViaNetRefusal"/>).</summary>
    public IReadOnlyDictionary<string, string> ViaNetConflicts { get; init; } = new Dictionary<string, string>();

    /// <summary>The conductors on a layout instance's ground-reference stackup entries.</summary>
    public IReadOnlyList<string> GroundBandObjects { get; init; } = [];

    /// <summary>Where each material's values resolved from, by final name.</summary>
    public IReadOnlyDictionary<string, string> MaterialSources { get; init; } = new Dictionary<string, string>();

    /// <summary>brief-em3d-76 — each solid's material record by name: the technology it was built from (the document's, or a
    /// placed layout's own) and the material's name THERE — the elaborated name may be qualified where two technologies
    /// define one name differently. What a thermal run reads k from, since an instance's materials need not be the
    /// document technology's.</summary>
    public IReadOnlyDictionary<string, (Technology Technology, string Material)> SolidMaterials { get; init; }
        = new Dictionary<string, (Technology, string)>();

    /// <summary>brief-em3d-94 — each solid's and sheet's material by name: the file of the technology it was built from (the
    /// document's, or a placed cell's own; null when that technology came from no file) and the material's name THERE. Where
    /// Edit Material… opens it.</summary>
    public IReadOnlyDictionary<string, (string? TechnologyPath, string Material)> MaterialOrigins { get; init; }
        = new Dictionary<string, (string?, string)>();

    /// <summary>What each object is, by name — a layout instance's origins, prefixed; a drawn object's by its role.</summary>
    public IReadOnlyDictionary<string, Em3dObjectOrigin> Origins { get; init; } = new Dictionary<string, Em3dObjectOrigin>();

    /// <summary>A layout instance's bond-wire reports, renamed into the parent, and every drawn wire's (brief-em3d-50).</summary>
    public IReadOnlyList<Em3dWireReport> Wires { get; init; } = [];

    /// <summary>brief-em3d-50 — each drawn wire that did not resolve, by its elaborated name: the refusal, which is also in
    /// <see cref="Refusals"/>. What the editor flags in its tree and draws in red.</summary>
    public IReadOnlyDictionary<string, string> WireRefusals { get; init; } = new Dictionary<string, string>();

    /// <summary>brief-em3d-50 — each drawn wire's result, by elaborated name: its pads and process values, for
    /// <c>explain</c> and the Wire tool's readout.</summary>
    public IReadOnlyDictionary<string, C3dWireResult> DrawnWires { get; init; } = new Dictionary<string, C3dWireResult>();

    /// <summary>brief-em3d-64 R-em3d64-3d — each kernel object that did not build (or could not, with no kernel), by
    /// elaborated name: the refusal, which is also in <see cref="Refusals"/>. What the editor marks in its tree.</summary>
    public IReadOnlyDictionary<string, string> KernelRefusals { get; init; } = new Dictionary<string, string>();

    /// <summary>brief-em3d-64 R-em3d64-6b — each kernel object's build, for <c>explain</c>: its operands, whether the
    /// build came from a cache, its face and edge counts, its smallest radius and the kernel's notes.</summary>
    public IReadOnlyList<C3dKernelBuild> KernelBuilds { get; init; } = [];

    /// <summary>brief-em3d-69 — every placed instance element that resolved, parents before their children.</summary>
    public IReadOnlyList<C3dInstanceFrame> Instances { get; init; } = [];

    /// <summary>brief-em3d-48 R-em3d48-6b — the instances whose cell or view resolved to nothing or could not be read, by
    /// instance path: what the editor draws as a dashed box with the cell's name.</summary>
    public IReadOnlyList<(string InstancePath, string CellRef)> Unresolved { get; init; } = [];

    /// <summary>Why an <see cref="Unresolved"/> instance is not drawn, in a few words, by instance path, for its label: a cell
    /// that could not be found is the case with no entry. A placed layout that was found and could not be built said
    /// "not found" as well, which sent the user looking for a file that was there.</summary>
    public IReadOnlyDictionary<string, string> UnresolvedWhy { get; init; } = new Dictionary<string, string>();

    /// <summary>3D editor bugs round 1 — the objects with no material, or one their technology does not define: none reaches a
    /// solver, but each is lowered all the same, under the name it would have had, so the editor can draw it (as a
    /// wireframe) and keep it selectable and editable. Material is empty; order 0. Round 2: one with NO material is a
    /// warning and the solver ignores it (<see cref="C3dElaborator.NoMaterialWarning"/>); one whose material the technology
    /// does not define is still a refusal.</summary>
    public IReadOnlyList<Em3dSolid> UnassignedSolids { get; init; } = [];
    public IReadOnlyList<Em3dSheet> UnassignedSheets { get; init; } = [];

    /// <summary>brief-sar-8b R-sar8b-2 — the conductors that are patches of a cylinder (<see cref="C3dCylinderSheet"/>), in construction
    /// order among the sheets. Kept apart from <see cref="Sheets"/>, which every consumer reads as planar polygons.</summary>
    public IReadOnlyList<Em3dCylinderSheet> CylinderSheets { get; init; } = [];

    /// <summary>brief-em3d-93 R-em3d93-2 — every solid, sheet and wire (by elaborated name) that is NOT MODELLED: its object's,
    /// its wire's or an enclosing instance's <c>Model</c> is false. Each is still in <see cref="Solids"/> and <see cref="Sheets"/>,
    /// because the editor draws, picks and edits it; a solve sees the elaboration <see cref="C3dModelled.Filter"/> returns.</summary>
    public IReadOnlySet<string> NotModelled { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>brief-em3d-93 — the subset of <see cref="Refusals"/> raised while elaborating content that is not modelled (a
    /// lid that does not build, an instance whose cell is missing): the editor shows them, and a solve, which leaves that
    /// content out, does not refuse for them.</summary>
    public IReadOnlyList<string> NotModelledRefusals { get; init; } = [];

    /// <summary>brief-em3d-101 R-em3d101-4a — every image sheet's image, by elaborated name: its file resolved against the
    /// <c>.c3d</c> that holds the sheet (an instance's content against the CHILD document, R-em3d101-5c) and where its corners
    /// are. Drawing only — nothing a solve reads; the scene draws the sheet with it.</summary>
    public IReadOnlyDictionary<string, C3dPlacedImage> Images { get; init; } = new Dictionary<string, C3dPlacedImage>();

    /// <summary>brief-em3d-101 R-em3d101-8 — every face image, on the elaborated object it was found on (an instance's content's
    /// resolved against the CHILD document): what the scene draws over the face. Drawing only.</summary>
    public IReadOnlyList<C3dFaceImageUse> FaceImages { get; init; } = [];

    /// <summary>The walk: instances resolved, units converted, materials merged, objects lowered.</summary>
    public IReadOnlyList<C3dWalkStep> WalkInstances { get; init; } = [];
    public IReadOnlyList<C3dWalkStep> WalkUnits { get; init; } = [];
    public IReadOnlyList<C3dWalkStep> WalkMaterials { get; init; } = [];
    public IReadOnlyList<C3dWalkStep> WalkLowering { get; init; } = [];

    /// <summary>brief-em3d-51 — the top document's names and fields, resolved before any geometry: what explain reports per
    /// name, what the editor's Variables panel and Properties show. Null only when elaboration did not start.</summary>
    public C3dResolution? Resolution { get; init; }

    /// <summary>
    /// brief-em3d-87 R-em3d87-1 — every file this elaboration was built from, as full paths, each once, sorted: the
    /// <c>.c3d</c> and its <c>.ccell</c>, each placed <c>.clay</c> with the sub-cells it flattens and its paired <c>.wBond</c>,
    /// each nested <c>.c3d</c> and its <c>.ccell</c>, every technology and each material library it looks through, each STEP
    /// file, and the assembly rules a drawn wire reads. Recorded where each is RESOLVED, not where it is read, so a cache hit
    /// names it as surely as a miss. What a run's input manifest hashes (<see cref="C3dRunInputs"/>).
    /// </summary>
    public IReadOnlyList<string> FilesRead { get; init; } = [];

    /// <summary>The document's own technology, and where it resolved.</summary>
    public Technology? Technology { get; init; }
    public string? TechnologyPath { get; init; }

    /// <summary>The elaborated content's bound, metres; null when there is nothing in it.</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1)? Extent()
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        void Grow((double, double, double, double, double, double) b)
        {
            x0 = Math.Min(x0, b.Item1); y0 = Math.Min(y0, b.Item2); z0 = Math.Min(z0, b.Item3);
            x1 = Math.Max(x1, b.Item4); y1 = Math.Max(y1, b.Item5); z1 = Math.Max(z1, b.Item6);
        }
        foreach (var s in Solids) Grow(Em3dProblem.Bounds(s.Primitive));
        foreach (var s in Sheets) Grow(s.WorldBounds());
        foreach (var s in CylinderSheets) Grow(s.WorldBounds());
        return double.IsInfinity(x0) ? null : (x0, y0, z0, x1, y1, z1);
    }

    /// <summary>brief-em3d-93 — the bound of what a solve has: <see cref="Extent"/> less the content that is not modelled. What the
    /// setup's air box is padded around, in the editor as in the run (<see cref="C3dModelled.Filter"/>).</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1)? ModelledExtent()
        => NotModelled.Count == 0 ? Extent()
         : (this with { Solids = [.. Solids.Where(s => !NotModelled.Contains(s.Name))], Sheets = [.. Sheets.Where(s => !NotModelled.Contains(s.Name))],
                        CylinderSheets = [.. CylinderSheets.Where(s => !NotModelled.Contains(s.Name))] }).Extent();

    /// <summary>What the editor frames: <see cref="Extent"/> and the <see cref="UnassignedSolids"/> and
    /// <see cref="UnassignedSheets"/> it draws beside what a solver gets.</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1)? DisplayExtent()
    {
        var e = Extent();
        foreach (var b in UnassignedSolids.Select(s => Em3dProblem.Bounds(s.Primitive)).Concat(UnassignedSheets.Select(s => s.WorldBounds())))
            e = e is not { } x ? b
                : (Math.Min(x.X0, b.Item1), Math.Min(x.Y0, b.Item2), Math.Min(x.Z0, b.Item3),
                   Math.Max(x.X1, b.Item4), Math.Max(x.Y1, b.Item5), Math.Max(x.Z1, b.Item6));
        return e;
    }
}

/// <summary>
/// Elaborates <c>.c3d</c> documents. Keep one per open document: its caches are what make re-elaboration
/// after one edit cost one object (R-em3d42-4).
/// </summary>
public sealed class C3dElaborator(TechnologyCache? technologies = null, GeometryKernel? kernel = null)
{
    /// <summary>3D editor bugs round 2 — the warning an object with no material raises: the solver ignores it. The same
    /// words as <see cref="C3dDiagnostics.NoMaterial"/>, so <c>check</c> says it once.</summary>
    public static string NoMaterialWarning(string name) => $"'{name}' has no material, so the solver ignores it.";

    private readonly TechnologyCache _tech = technologies ?? new TechnologyCache();
    private readonly Dictionary<string, C3dLowered?> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _children = new(StringComparer.Ordinal);

    // brief-em3d-48 R-em3d48-3a — a layout instance's solids LOWERED under each element's transform, kept from one
    // elaboration to the next: an unchanged element hands back the very primitives it did last time, so the scene's
    // tessellation cache (keyed by primitive) hits and an array of a layout child is tessellated once, even when a column
    // is added. What one elaboration did not use is dropped at its end, as the tessellation cache drops its own.
    private Dictionary<(object Child, string Transform), (C3dLowered[] Solids, C3dSheetGeometry[] Sheets)> _layoutKept = [];
    private Dictionary<(object Child, string Transform), (C3dLowered[] Solids, C3dSheetGeometry[] Sheets)> _layoutUsed = [];

    /// <summary>Objects lowered because the per-object cache missed.</summary>
    public long ObjectsElaborated { get; private set; }

    /// <summary>brief-em3d-64 R-em3d64-3e — trees handed to the geometry kernel because the per-tree cache missed. An edit
    /// to one boolean raises it by one; an unrelated edit, its undo, and re-elaborating an unchanged document by zero.</summary>
    public long KernelTreesBuilt { get; private set; }

    /// <summary>The kernel this elaborator builds with: the application's, unless a caller (a test) gave its own. Only
    /// read when a document holds a kernel object.</summary>
    public GeometryKernel Kernel => kernel ?? GeometryKernel.Shared;

    private readonly Dictionary<string, KernelLowered> _kernelSolids = new(StringComparer.Ordinal);

    /// <summary>Child views read and built because the per-child cache missed.</summary>
    public long ChildrenElaborated { get; private set; }

    /// <summary>brief-em3d-51 R-em3d51-5b — child 3D views RESOLVED because no earlier instance had the same override
    /// values: two instances with equal overrides share one.</summary>
    public long ChildrenResolved { get; private set; }

    private readonly Dictionary<string, (C3dDocument Doc, C3dResolution Res)> _resolvedChildren = new(StringComparer.Ordinal);

    /// <summary>One elaboration with no cache kept.</summary>
    public static C3dElaboration ElaborateOnce(C3dDocument document, string path, string? workspaceCws,
                                               C3dElaborationOptions? options = null, GeometryKernel? kernel = null)
        => new C3dElaborator(null, kernel).Elaborate(document, path, workspaceCws, options);

    /// <summary>
    /// <paramref name="document"/>, at <paramref name="path"/>, elaborated. <paramref name="workspaceCws"/> is
    /// the technology walk's fallback when the path has no ancestor workspace.
    /// </summary>
    public C3dElaboration Elaborate(C3dDocument document, string path, string? workspaceCws, C3dElaborationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        var run = new Run(this, options ?? new C3dElaborationOptions(), workspaceCws);
        var result = run.Go(document, Path.GetFullPath(path));
        (_layoutKept, _layoutUsed) = (_layoutUsed, _layoutKept);
        _layoutUsed.Clear();
        return result;
    }

    // ── caches ────────────────────────────────────────────────────────────────────────────────────

    private C3dLowered? LowerCached(C3dObject obj, C3dTransform world, int dbuPerMicron)
    {
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{dbuPerMicron}|{world.M00:R},{world.M01:R},{world.M02:R},{world.M10:R},{world.M11:R},{world.M12:R},{world.M20:R},{world.M21:R},{world.M22:R},{world.Tx:R},{world.Ty:R},{world.Tz:R}|") +
            C3dPersistence.SerializeResolved(obj);
        if (_objects.TryGetValue(key, out var hit)) return hit;
        ObjectsElaborated++;
        return _objects[key] = C3dLowering.Lower(obj, world, dbuPerMicron);
    }

    /// <summary>A kernel object's build, lowered — or its refusal. Kept by the tree's hash, which is the resolved inputs'.</summary>
    private sealed record KernelLowered(C3dLowered? Lowered, string? Refusal, IReadOnlyList<string> Notes, bool Built, int Edges, double? MinRadiusM,
                                        bool Empty = false, int Solids = 1)
    {
        /// <summary>brief-em3d-67 — the kernel's refusal, for the caller to word in the operation's terms.</summary>
        public GeometryKernelException? Failure { get; init; }
    }

    /// <summary>
    /// brief-em3d-64 R-em3d64-3a — <paramref name="tree"/> built by the kernel and lowered to an <see cref="Em3dShapeSolid"/>:
    /// build, face table, edges and the display tessellation, in metres. From this elaborator's cache when the tree has been
    /// lowered before (<see cref="KernelTreesBuilt"/> counts the misses); the kernel's own cache sits under that.
    /// </summary>
    private KernelLowered KernelCached(GeometryKernelTree tree, string name)
    {
        if (_kernelSolids.TryGetValue(tree.Hash, out var hit)) return hit;
        KernelTreesBuilt++;
        var k = Kernel;
        long before = k.RequestsSent;
        KernelLowered made;
        try
        {
            var (solid, build, faces, edges) = ShapeSolid(k, tree, name);
            var radii = faces.Select(f => f.MinRadius).Concat(edges.Select(e => e.MinRadius)).Where(r => r > 0).ToList();
            // brief-em3d-66 R-em3d66-2f — a build that holds no solid (an intersection of disjoint solids) is this object's
            // refusal, worded by the caller, which knows the operation; never an empty solid handed on to a solver.
            made = build.Solids == 0
                ? new KernelLowered(null, $"'{name}' is empty: the operation leaves no solid.", build.Notes, k.RequestsSent > before, 0, null, Empty: true, Solids: 0)
                : new KernelLowered(new C3dLowered(solid, null, [.. faces.Select(f => f.Name)], KindKernel), null, build.Notes,
                                    k.RequestsSent > before, edges.Count, radii.Count > 0 ? radii.Min() * 1e-6 : null, Solids: build.Solids);
        }
        catch (GeometryKernelException e)
        {
            // brief-em3d-66 — the worker's own "leaves nothing" (build.empty) is worded by the caller, as an empty build is.
            made = new KernelLowered(null, e.Message, [], k.RequestsSent > before, 0, null, Empty: e.Code == EmptyCode, Solids: 0) { Failure = e };
        }
        return _kernelSolids[tree.Hash] = made;
    }

    /// <summary>
    /// brief-em3d-64 R-em3d64-3a — <paramref name="tree"/> built by <paramref name="k"/> and lowered to the neutral problem's
    /// <see cref="Em3dShapeSolid"/>, named <paramref name="name"/>: the B-rep, the face table and edges in metres, the display
    /// tessellation, and (brief-em3d-65) the hook openEMS re-tessellates it through. Not cached: the elaboration caches it.
    /// </summary>
    /// <exception cref="GeometryKernelException">The kernel refused, crashed or is absent.</exception>
    public static (Em3dShapeSolid Solid, GeometryKernelBuild Build, IReadOnlyList<GeometryKernelFace> Faces, IReadOnlyList<GeometryKernelEdge> Edges)
        ShapeSolid(GeometryKernel k, GeometryKernelTree tree, string name)
        => ShapeSolid(k.Model, k, tree, name);

    /// <summary>
    /// brief-em3d-66 R-em3d66-2g — <see cref="ShapeSolid(GeometryKernel, GeometryKernelTree, string)"/> asking its four questions
    /// of <paramref name="shapes"/>: the preview session's inside a preview, so the answers a commit's elaboration asks for —
    /// the same questions of the same tree — are already in the cache the two sessions share, and the commit makes no call.
    /// </summary>
    public static (Em3dShapeSolid Solid, GeometryKernelBuild Build, IReadOnlyList<GeometryKernelFace> Faces, IReadOnlyList<GeometryKernelEdge> Edges)
        ShapeSolid(GeometryKernelShapes shapes, GeometryKernel k, GeometryKernelTree tree, string name)
    {
        var build = shapes.Build(tree);
        var faces = shapes.Faces(tree);
        double diag = 0;
        if (faces.Count > 0)
        {
            double x0 = faces.Min(f => f.Box[0]), y0 = faces.Min(f => f.Box[1]), z0 = faces.Min(f => f.Box[2]);
            double x1 = faces.Max(f => f.Box[3]), y1 = faces.Max(f => f.Box[4]), z1 = faces.Max(f => f.Box[5]);
            diag = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0));
        }
        // Relative to the object's size (overview §1j): a thousandth of its diagonal, never below a nanometre.
        double linearUm = Math.Max(diag * DisplayRelativeDeflection, 1e-3);
        var mesh = shapes.Tessellate(tree, linearUm, DisplayAngularRad);
        var edges = shapes.Edges(tree, linearUm);
        const double M = 1e-6;
        var display = Metres(mesh, name);
        var triangles = display.Triangles;
        var first = new int[faces.Count];
        var count = new int[faces.Count];
        Array.Fill(first, -1);
        for (int t = 0; t < triangles.Count; t++)
        {
            int f = triangles[t].Face;
            if (f < 0 || f >= faces.Count) continue;
            if (first[f] < 0) first[f] = t;
            count[f]++;
        }
        var shapeFaces = faces.Select((f, i) => new Em3dShapeFace(f.Name, f.Kind,
            (f.Box[0] * M, f.Box[1] * M, f.Box[2] * M, f.Box[3] * M, f.Box[4] * M, f.Box[5] * M),
            f.MinRadius * M, Math.Max(first[i], 0), count[i])).ToList();
        static Point3? P(double[]? v, int at = 0) => v is { } a && a.Length >= at + 3 ? new Point3(a[at] * M, a[at + 1] * M, a[at + 2] * M) : null;
        static Point3? D(double[] v, int at) => v.Length >= at + 3 ? new Point3(v[at], v[at + 1], v[at + 2]) : null;
        var shapeEdges = edges.Select(e => new Em3dShapeEdge(e.Name, e.FaceA, e.FaceB, e.Kind, e.MinRadius * M,
            [.. Enumerable.Range(0, e.Polyline.Length / 3).Select(i => new Point3(e.Polyline[3 * i] * M, e.Polyline[3 * i + 1] * M, e.Polyline[3 * i + 2] * M))])
        {
            // brief-em3d-67 — what snapping and the tangent chain read: exact from the curve, not the polyline.
            Closed = e.Closed, LengthM = e.Length * M, Mid = P(e.Mid), Centre = P(e.Centre), RadiusM = e.Radius * M,
            Tangents = D(e.Tangents, 0) is { } t0 && D(e.Tangents, 3) is { } t1 ? (t0, t1) : null,
        }).ToList();
        var solid = new Em3dShapeSolid(build.Brep, build.BrepHash, display, shapeFaces, shapeEdges)
        {
            DisplayDeflectionM = linearUm * M,
            // brief-em3d-65 R-em3d65-3a — openEMS asks again, at its own grid's deflection, through the same kernel.
            Tessellator = (linearM, angularRad) => Metres(k.Tessellate(tree, linearM / M, angularRad), name),
        };
        return (solid, build, faces, edges);
    }

    /// <summary>A kernel tessellation (micrometres) in metres, each triangle carrying its face's index.</summary>
    private static Em3dTriangleMesh Metres(GeometryKernelMesh mesh, string name)
    {
        const double M = 1e-6;
        var vertices = new Point3[mesh.Vertices.Length / 3];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new Point3(mesh.Vertices[3 * i] * M, mesh.Vertices[3 * i + 1] * M, mesh.Vertices[3 * i + 2] * M);
        var triangles = new Em3dTriangle[mesh.TriangleFace.Length];
        for (int t = 0; t < triangles.Length; t++)
            triangles[t] = new Em3dTriangle((int)mesh.Triangles[3 * t], (int)mesh.Triangles[3 * t + 1], (int)mesh.Triangles[3 * t + 2], name,
                                            (int)mesh.TriangleFace[t]);
        return new Em3dTriangleMesh(vertices, triangles);
    }

    /// <summary>The worker's refusal code for an operation that leaves no solid (brief-em3d-66).</summary>
    public const string EmptyCode = "build.empty";

    /// <summary>The display tessellation's angular deflection, radians.</summary>
    public const double DisplayAngularRad = 0.5;

    /// <summary>The display tessellation's linear deflection as a fraction of the object's diagonal (overview §1j) — also
    /// what STEP import counts a part's display triangles at (brief-em3d-68 R-em3d68-4c).</summary>
    public const double DisplayRelativeDeflection = 1e-3;

    /// <summary>
    /// brief-em3d-66 R-em3d66-2g — the tree elaboration hands the kernel for TOP-LEVEL object <paramref name="index"/> of
    /// <paramref name="document"/>: a copy resolved exactly as <see cref="Elaborate"/> resolves it, the object's placement
    /// under the identity. The Boolean panel previews this tree, so its hash is the committed document's.
    /// </summary>
    public static GeometryKernelTree KernelTreeOf(C3dDocument document, int index, string path, C3dCell? cell = null)
    {
        var copy = C3dPersistence.Deserialize(C3dPersistence.Serialize(document));
        C3dResolver.Resolve(copy, cell ?? C3dCell.Of(Path.GetFullPath(path)));
        return GeometryKernelTree.From(copy.Objects[index], copy.DbuPerMicron, C3dTransform.Identity, Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    /// <summary>
    /// brief-em3d-66 R-em3d66-2e — <paramref name="tree"/>'s preview: the shape elaboration would lower it to, asked of the
    /// kernel's preview session, the newest request superseding the older (null when superseded). A refusal is thrown.
    /// </summary>
    public static Task<C3dShapePreview?> PreviewShape(GeometryKernel k, GeometryKernelTree tree, string name)
        => k.RequestPreview<C3dShapePreview>((shapes, superseded) =>
        {
            var (solid, build, faces, _) = ShapeSolid(shapes, k, tree, name);
            return superseded() ? null : new C3dShapePreview(tree, solid, build, faces.Count);
        });

    /// <summary>The lowering table's row a kernel object takes.</summary>
    public const string KindKernel = "kernel-solid";

    private static string Stamp(string? path)
    {
        if (path is null || !File.Exists(path)) return "-";
        var info = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{info.LastWriteTimeUtc.Ticks}:{info.Length}");
    }

    /// <summary>A child 3D view: its document and technology.</summary>
    private sealed record Child3D(C3dDocument Document, TechResolution Tech, string? Refusal);

    /// <summary>A child layout view: its solids through its own technology.</summary>
    private sealed record ChildLayout(Em3dLayoutSolidsResult? Solids, TechResolution Tech, string TechName, string? Refusal,
                                      int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        /// <summary>brief-em3d-87 — the sub-cells its flatten reads and its paired <c>.wBond</c>.</summary>
        public IReadOnlyList<string> Files { get; init; } = [];

        /// <summary><see cref="Files"/>' stamps when it was built.</summary>
        public string FilesStamp { get; init; } = "";
    }

    private static string FilesStamp(IReadOnlyList<string> files) => string.Join("|", files.Select(Stamp));

    private Child3D Child3DCached(string path, string? fallbackCws)
    {
        // The technology stamp needs the document read first; a document read is what the file stamp guards.
        string docKey = "3d|" + path + "|" + Stamp(path);
        if (_children.TryGetValue(docKey, out var hit) && hit is Child3D c)
        {
            if (Stamp(c.Tech.ResolvedPath) == TechStampOf(docKey) && Current(c.Tech)) return c;
        }
        ChildrenElaborated++;
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(path); }
        catch (Exception e)
        {
            var bad = new Child3D(new C3dDocument(), new TechResolution(null, null, TechResolutionSource.None, []), e.Message);
            _children[docKey] = bad;
            return bad;
        }
        var (tech, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, path, fallbackCws, _tech);
        var made = new Child3D(doc, tech, null);
        _children[docKey] = made;
        _children["techstamp|" + docKey] = Stamp(tech.ResolvedPath);
        return made;
    }

    /// <summary>
    /// brief-em3d-53 R-em3d53-1f — whether a cached child's technology is still the one the cache hands out. A live
    /// (unsaved) technology edit and a material-library edit replace the cached INSTANCE without touching the
    /// file, so the file stamp alone would keep an instance built on the old values.
    /// </summary>
    private bool Current(TechResolution t)
    {
        if (t.ResolvedPath is null || t.Tech is null) return true;
        try { return ReferenceEquals(_tech.Get(t.ResolvedPath), t.Tech); }
        catch { return false; }
    }

    private string TechStampOf(string docKey) => _children.TryGetValue("techstamp|" + docKey, out var s) ? (string)s : "-";

    /// <summary>
    /// brief-em3d-51 R-em3d51-5b — a child 3D view resolved under one set of override VALUES: a copy of the shared document
    /// with its numbers written, kept by (file, file stamp, .ccell stamp, the values). Two instances whose overrides are
    /// equal share one, however their override expressions were spelled.
    /// </summary>
    private (C3dDocument Doc, C3dResolution Res) ResolvedChild(string path, Child3D child, C3dCell cell, IReadOnlyDictionary<string, C3dOverride> overrides)
    {
        string key = "3d|" + path + "|" + Stamp(path) + "|" + Stamp(cell.CcellPath) + "|" +
                     string.Join(";", overrides.OrderBy(o => o.Key, StringComparer.Ordinal)
                                               .Select(o => $"{o.Key}={o.Value.Value}:{o.Value.Unit}"));
        if (_resolvedChildren.TryGetValue(key, out var hit)) return hit;
        ChildrenResolved++;
        var copy = C3dPersistence.Deserialize(C3dPersistence.Serialize(child.Document));
        var res = C3dResolver.Resolve(copy, cell, overrides);
        return _resolvedChildren[key] = (copy, res);
    }

    private ChildLayout ChildLayoutCached(string path, string? fallbackCws, C3dElaborationOptions options)
    {
        // The paired .wBond is in the key: wire edits are saved to it alone, so the .clay's stamp did not move and the
        // view kept drawing the old wires. The sub-cells the flatten read are checked on a hit, for the same reason.
        string key = string.Create(CultureInfo.InvariantCulture,
            $"layout|{path}|{Stamp(path)}|{Stamp(WBondCell.FindFor(path))}|{options.FMaxHz:R}|{options.TempC:R}|{options.RobustMeshing}|{options.PadsOptional}");
        if (_children.TryGetValue(key, out var hit) && hit is ChildLayout c && Stamp(c.Tech.ResolvedPath) == TechStampOf(key)
            && Current(c.Tech) && c.FilesStamp == FilesStamp(c.Files))
            return c;
        ChildrenElaborated++;
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(path); }
        catch (Exception e)
        {
            var bad = new ChildLayout(null, new TechResolution(null, null, TechResolutionSource.None, []), "", e.Message);
            _children[key] = bad;
            return bad;
        }
        var (tech, _) = TechnologyResolver.ResolveForDocument(view.TechRef, path, fallbackCws, _tech);
        IReadOnlyList<string> files = [.. LayoutFlatten.FilesRead(view, path), .. WBondCell.FindFor(path) is { } wb ? [wb] : Array.Empty<string>()];
        ChildLayout made;
        if (tech.Tech is not { } t)
            made = new ChildLayout(null, tech, "", "its technology does not resolve" +
                                   (tech.Diagnostics.Count > 0 ? ": " + string.Join(" ", tech.Diagnostics) : ".")) { Files = files };
        else
        {
            var solids = Em3dLayoutSolids.From(new EmLayoutSource(path, view, t, view.DbuPerMicron), t, null,
                                               new Em3dLayoutSolidsOptions(options.FMaxHz, options.TempC, Instance: true)
                                               { RobustMeshing = options.RobustMeshing, PadsOptional = options.PadsOptional });
            made = new ChildLayout(solids, tech, TechName(tech), solids.Refusal, view.DbuPerMicron) { Files = files };
        }
        made = made with { FilesStamp = FilesStamp(made.Files) };
        _children[key] = made;
        _children["techstamp|" + key] = Stamp(tech.ResolvedPath);
        return made;
    }

    /// <summary>A layout child's solids and sheets under <paramref name="t"/>: kept from the last elaboration when the same
    /// child was placed under the same transform.</summary>
    private (C3dLowered[] Solids, C3dSheetGeometry[] Sheets) LayoutLowered(object child, Em3dLayoutSolidsResult solids, C3dTransform t,
                                                                         Func<Em3dSolid, C3dLowered> lower)
    {
        var key = (child, string.Create(CultureInfo.InvariantCulture,
            $"{t.M00:R},{t.M01:R},{t.M02:R},{t.M10:R},{t.M11:R},{t.M12:R},{t.M20:R},{t.M21:R},{t.M22:R},{t.Tx:R},{t.Ty:R},{t.Tz:R}"));
        if (_layoutUsed.TryGetValue(key, out var hit) || _layoutKept.TryGetValue(key, out hit)) return _layoutUsed[key] = hit;
        (C3dLowered[] Solids, C3dSheetGeometry[] Sheets) made = (solids.Solids.Select(lower).ToArray(), solids.Sheets.Select(sh => C3dLowering.TransformSheet(C3dLowering.Geometry(sh), t)).ToArray());
        return _layoutUsed[key] = made;
    }

    private static string TechName(TechResolution t)
        => t.ResolvedPath is { } p ? Path.GetFileNameWithoutExtension(p) : t.Tech?.Name ?? "(no technology)";

    /// <summary>A technology material's values at <paramref name="tempC"/> — the one rule elaboration resolves a drawn
    /// object's material by (brief-em3d-48 compares a flattened object's against it).</summary>
    /// <remarks>brief-sar-2 — with <paramref name="frequencyHz"/>, a dispersive material is its model's εr and total σ there,
    /// with no loss tangent beside it (the σ already carries the dielectric loss); one its model cannot give a value for there
    /// keeps its plain numbers, and the elaborator refuses it before anything runs.</remarks>
    public static Em3dMaterial MaterialValues(TechMaterial m, double tempC, double? frequencyHz = null)
    {
        if (frequencyHz is { } f && MaterialDispersion.At(m, f, out _) is { } d)
            return new Em3dMaterial(m.Name, d.Epsr, null, 0, m.Mur ?? 1, d.SigmaSm);
        double sigma = 0;
        if (m.Sigma20 is { } s20)
            sigma = m.Alpha20 is { } a20 ? new WireMaterial(m.Name, s20, a20, 0).SigmaAt(tempC) : s20;
        return new Em3dMaterial(m.Name, m.Epsr ?? 1, m.EpsrTensor is { Length: 3 } t ? [.. t] : null, m.TanD ?? 0, m.Mur ?? 1, sigma);
    }

    // ── one elaboration ─────────────────────────────────────────────────────────────────────────────

    private sealed class Run(C3dElaborator owner, C3dElaborationOptions options, string? workspaceCws)
    {
        private readonly List<Em3dSolid> _solids = [];
        private readonly List<Em3dSheet> _sheets = [];
        private readonly List<Em3dSolid> _unassignedSolids = [];
        private readonly List<Em3dSheet> _unassignedSheets = [];
        private readonly List<Em3dCylinderSheet> _cylinderSheets = [];
        private readonly List<(int Index, string Tech, string Name)> _cylinderUses = [];
        private readonly Dictionary<string, C3dPlacedImage> _images = new(StringComparer.Ordinal);
        private readonly List<C3dFaceImageUse> _faceImages = [];
        private readonly Dictionary<string, C3dProvenance> _provenance = new(StringComparer.Ordinal);

        /// <summary>The top-level object being elaborated, for <see cref="C3dProvenance.TopObject"/>.</summary>
        private string? _topObject;

        /// <summary>brief-em3d-92 — the top-level object's transparency, and the opacity the instances on the way down to its
        /// document multiply onto it, for <see cref="C3dProvenance.Transparency"/> and <see cref="C3dProvenance.Opacity"/>.</summary>
        private int? _transparency;
        private double _opacity = 1;

        /// <summary>brief-em3d-105 — the top-level object's appearance, and the instances' on the way down to its document,
        /// innermost first, for <see cref="C3dProvenance.Appearance"/> and <see cref="C3dProvenance.InstanceAppearances"/>.</summary>
        private TechAppearance? _appearance;
        private IReadOnlyList<Appearance.AppearanceInstanceStatement> _instanceAppearances = [];

        /// <summary>brief-em3d-93 — true while elaborating content that is not modelled (its object's, its wire's or an enclosing
        /// instance's Model is false): what is added then goes in <see cref="_notModelled"/>, and the refusals it raises in
        /// <see cref="_notModelledRefusals"/> too.</summary>
        private bool _off;
        private readonly HashSet<string> _notModelled = new(StringComparer.Ordinal);
        private readonly List<string> _notModelledRefusals = [];

        /// <summary>Runs <paramref name="body"/> with <see cref="_off"/> set when <paramref name="modelled"/> is false (or it was
        /// already), recording the refusals it raises as not-modelled content's.</summary>
        private void Modelled(bool modelled, Action body)
        {
            bool outer = _off;
            _off = outer || !modelled;
            int before = _refusals.Count;
            try { body(); }
            finally
            {
                if (_off && !outer) _notModelledRefusals.AddRange(_refusals.Skip(before));
                _off = outer;
            }
        }

        private void MarkOff(string name)
        {
            if (_off) _notModelled.Add(name);
        }
        private readonly List<string> _notes = [];
        private readonly List<string> _warnings = [];
        private readonly List<string> _refusals = [];
        /// <summary>brief-em3d-135 follow-up — the top document's items holding a field that did not resolve, left out of the
        /// build; and its wires whose only such field is a held loop height or span, built as they stand.</summary>
        private readonly HashSet<string> _broken = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unheld = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _nets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _viaNetConflicts = new(StringComparer.Ordinal);
        private readonly List<string> _groundBand = [];
        private readonly Dictionary<string, Em3dObjectOrigin> _origins = new(StringComparer.Ordinal);
        private readonly List<Em3dWireReport> _wires = [];
        private readonly Dictionary<string, string> _wireRefusals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, C3dWireResult> _drawnWires = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _kernelRefusals = new(StringComparer.Ordinal);
        private readonly List<C3dKernelBuild> _kernelBuilds = [];
        private GeometryKernelCapability? _capability;
        private readonly List<(string Name, string Source)> _builtInWireValues = [];
        private readonly List<C3dWalkStep> _walkInstances = [], _walkUnits = [], _walkLowering = [];
        private readonly List<(string, string)> _unresolved = [];
        private readonly Dictionary<string, string> _unresolvedWhy = new(StringComparer.Ordinal);
        private readonly List<C3dInstanceFrame> _instances = [];
        private int _order;
        private int _polylines;
        private int _topDbu;
        private readonly SortedSet<string> _ignored = new(StringComparer.Ordinal);
        private bool _anyLayoutInstance;
        private readonly SortedSet<string> _files = new(StringComparer.Ordinal);

        /// <summary>brief-em3d-87 — <paramref name="file"/> into <see cref="C3dElaboration.FilesRead"/>, when it is a file.</summary>
        private void Read(string? file)
        {
            if (file is { Length: > 0 } && Path.IsPathRooted(file) && File.Exists(file)) _files.Add(Path.GetFullPath(file));
        }

        /// <summary>A technology's file and every material library it looks through (a shipped one is no file).</summary>
        private void ReadTech(TechResolution t)
        {
            Read(t.ResolvedPath);
            foreach (string lib in t.Tech?.ResolvedLibraryPaths ?? []) Read(lib);
        }

        // Materials: every (technology, name) with its values, and each object's key, renamed at the end.
        private readonly List<(string Tech, string Name, Em3dMaterial Values, string Source)> _materials = [];
        private readonly List<(int Index, bool Sheet, string Tech, string Name)> _uses = [];

        /// <summary>brief-em3d-76 — each technology a use names, by that name: what a solid's own material record is read from.</summary>
        private readonly Dictionary<string, Technology> _techObjects = new(StringComparer.Ordinal);

        /// <summary>brief-em3d-94 — the file each of those technologies resolved from, by the same name.</summary>
        private readonly Dictionary<string, string> _techPaths = new(StringComparer.Ordinal);

        private void Know(TechResolution tech, string techName)
        {
            if (tech.Tech is { } techObject) _techObjects.TryAdd(techName, techObject);
            if (tech.ResolvedPath is { } resolved) _techPaths.TryAdd(techName, resolved);
        }

        public C3dElaboration Go(C3dDocument doc, string path)
        {
            var (tech, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, path, workspaceCws, owner._tech);
            foreach (string d in tech.Diagnostics) _notes.Add(d);
            _topDbu = doc.DbuPerMicron;
            var cell = options.Cell ?? C3dCell.Of(path);
            Read(path);
            Read(cell.CcellPath);
            ReadTech(tech);

            // brief-em3d-51 R-em3d51-5a — every expression resolves before any geometry is built, and a document whose names or
            // fields do not resolve is refused with the engine's message, so nothing runs from it (Ok is false). What resolved
            // IS built — the owner's rule, a best attempt at drawing what works (brief-em3d-135 follow-up): a mistyped name in
            // one wire's span blanked the whole drawing, so the one place the mistake was could not be seen either. An item
            // holding a field that did not resolve is left out (its numbers are not its expression's), except a wire whose only
            // such fields are its held loop height or span: its points are numbers of their own, so it is drawn as it stands.
            var resolution = C3dResolver.Resolve(doc, cell, null, options.Sets);
            _refusals.AddRange(resolution.Errors);
            _warnings.AddRange(resolution.Warnings);
            _notes.AddRange(resolution.Notes);
            _notes.AddRange(resolution.Infos);
            foreach (var f in resolution.FieldErrors)
                if (f.Path is nameof(C3dWire.LoopHeight) or nameof(C3dWire.Span) && doc.Objects.FirstOrDefault(o => o.Name == f.Item) is C3dWire)
                    _unheld.Add(f.Item);
                else _broken.Add(f.Item);
            Document(doc, resolution, path, tech, C3dTransform.Identity, "", [(CellOf(path), "", path)], exact: true);

            if (_polylines > 0)
                _notes.Add($"{_polylines} polyline(s) are construction geometry and are not in the 3D problem.");
            if (_ignored.Count > 0 || _anyLayoutInstance)
            {
                var parts = _ignored.Append(_anyLayoutInstance ? "a layout's .cem solve region" : null).OfType<string>().ToList();
                string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
                _notes.Add($"A placed cell contributes geometry only (overview §1k): {list} " +
                           $"{(parts.Count == 1 ? "is" : "are")} not part of its instance. Only the parent says where a signal enters.");
            }

            if (_builtInWireValues.Count > 0)
                _notes.Add($"No {string.Join(", ", _builtInWireValues.Select(v => v.Source).Distinct())} is stated on " +
                           $"{string.Join(", ", _builtInWireValues.Select(v => v.Name).Distinct().Select(n => $"'{n}'"))} or in the " +
                           "workspace's assembly rules, so the built-in starting value was used. It is a first guess, not assembly " +
                           "data: set it on the wire's end, or in the .wasm the workspace's DefaultAssemblyRef names.");

            // brief-sar-2 R-sar2-3 — one line: the frequency every dispersive material was evaluated at, and what each became.
            if (_dispersive.Count > 0 && options.MaterialFrequencyHz is { } mf)
                _notes.Add($"Frequency-dependent materials are evaluated once, at {MaterialDispersion.Hz(mf)}" +
                           (options.MaterialFrequencyBasis is { Length: > 0 } basis ? $" ({basis})" : "") +
                           ", and the solver sees those values at every frequency: " +
                           string.Join("; ", _dispersive.Select(d => string.Create(CultureInfo.InvariantCulture,
                               $"{d.Key} εr {d.Value.Epsr:G4}, σ {d.Value.Sigma:G4} S/m"))) + ".");

            var (materials, sources, walkMaterials) = Materials();
            var solids = new List<Em3dSolid>(_solids);
            var sheets = new List<Em3dSheet>(_sheets);
            var records = new Dictionary<string, (Technology Technology, string Material)>(StringComparer.Ordinal);
            var origins = new Dictionary<string, (string? TechnologyPath, string Material)>(StringComparer.Ordinal);
            foreach (var (index, isSheet, t, n) in _uses)
            {
                string final = Final(t, n);
                origins[isSheet ? sheets[index].Name : solids[index].Name] = (_techPaths.GetValueOrDefault(t), n);
                if (isSheet) sheets[index] = sheets[index] with { Material = final };
                else
                {
                    solids[index] = solids[index] with { Material = final };
                    if (_techObjects.TryGetValue(t, out var techObject)) records[solids[index].Name] = (techObject, n);
                }
            }
            var cylinderSheets = new List<Em3dCylinderSheet>(_cylinderSheets);
            foreach (var (index, t, n) in _cylinderUses)
            {
                origins[cylinderSheets[index].Name] = (_techPaths.GetValueOrDefault(t), n);
                cylinderSheets[index] = cylinderSheets[index] with { Material = Final(t, n) };
            }

            return new C3dElaboration(solids, sheets, materials, _provenance, _notes, _refusals)
            {
                Warnings = _warnings,
                Unresolved = _unresolved,
                UnresolvedWhy = _unresolvedWhy,
                Instances = _instances,
                ObjectNets = _nets,
                ViaNetConflicts = _viaNetConflicts,
                GroundBandObjects = _groundBand,
                MaterialSources = sources,
                SolidMaterials = records,
                MaterialOrigins = origins,
                Origins = _origins,
                Wires = _wires,
                WireRefusals = _wireRefusals,
                KernelRefusals = _kernelRefusals,
                KernelBuilds = _kernelBuilds,
                DrawnWires = _drawnWires,
                WalkInstances = _walkInstances,
                WalkUnits = _walkUnits,
                WalkMaterials = walkMaterials,
                WalkLowering = _walkLowering,
                UnassignedSolids = _unassignedSolids,
                UnassignedSheets = _unassignedSheets,
                CylinderSheets = cylinderSheets,
                NotModelled = _notModelled,
                NotModelledRefusals = _notModelledRefusals,
                Images = _images,
                FaceImages = _faceImages,
                Technology = tech.Tech,
                TechnologyPath = tech.ResolvedPath,
                Resolution = resolution,
                FilesRead = [.. _files],
            };
        }

        /// <summary>A document's objects and instances under <paramref name="world"/> (metres).</summary>
        private void Document(C3dDocument doc, C3dResolution resolution, string path, TechResolution tech, C3dTransform world, string prefix,
                              List<(string Cell, string Instance, string Path)> stack, bool exact)
        {
            string techName = TechName(tech);
            Know(tech, techName);
            string unit = LayoutUnits.AsciiSuffix(doc.DisplayUnit);
            _walkUnits.Add(new C3dWalkStep(prefix.Length == 0 ? Path.GetFileName(path) : prefix.TrimEnd('/'),
                $"{doc.DbuPerMicron} DBU per µm (1 DBU = {C3dLowering.Metres(1, doc.DbuPerMicron).ToString("R", CultureInfo.InvariantCulture)} m), " +
                $"displayed in {unit}; converted to metres through the exact decimal path, then placed"));
            if (prefix.Length > 0)
            {
                if (doc.Ports.Count > 0) _ignored.Add("its ports");
                if (doc.Setups.Count > 0) _ignored.Add("its setups");
                if (doc.FaceBoundaries.Count > 0) _ignored.Add("its face boundaries");
            }

            string docDir = Path.GetDirectoryName(path)!;
            void Steps(C3dObject o)
            {
                if (o is C3dStep st && st.File is { Length: > 0 } f) Read(Path.GetFullPath(Path.Combine(docDir, f)));
                foreach (var (_, operand) in C3dOperands.Of(o)) Steps(operand);
            }
            foreach (var obj in doc.Objects)
            {
                Steps(obj);
                if (obj is C3dPolyline) { _polylines++; continue; }
                if (obj is C3dWire) continue;                       // after the instances: see Wires
                if (prefix.Length == 0 && _broken.Contains(obj.Name)) continue;
                _topObject = obj.Name;
                _transparency = obj.Transparency;
                _appearance = obj.Appearance;
                Modelled(obj.Model, () => Object(obj, prefix + obj.Name, world, doc, tech, prefix, path, exact));
                // brief-em3d-101 — a top-level image sheet's picture, resolved against THIS document (an operand's is not drawn).
                if (obj is C3dSheet { Image: { } img } sheet && C3dImages.Resolve(path, img.Path) is { } file
                    && C3dImages.Placed(sheet, file, world, doc.DbuPerMicron) is { } placed)
                    _images[prefix + obj.Name] = placed;
                // brief-em3d-101 Phase B — its face images, carried for its result (an operation's) at the top level.
                if (obj.FaceImages is { Count: > 0 } faces)
                    for (int k = 0; k < faces.Count; k++)
                        _faceImages.Add(new C3dFaceImageUse(prefix + obj.Name, k, faces[k], C3dImages.Resolve(path, faces[k].Image.Path),
                                                            C3dLowering.Metres(1, doc.DbuPerMicron), _opacity));
                _topObject = null;
                _transparency = null;
                _appearance = null;
            }

            string baseDir = Path.GetDirectoryName(path)!;
            foreach (var inst in doc.Instances)
                if (prefix.Length > 0 || !_broken.Contains(inst.Name))
                    Modelled(inst.Model, () => Instance(doc, resolution, inst, baseDir, world, prefix, stack, exact));
            if (doc.Objects.Any(o => o is C3dWire)) Wires(doc, path, tech, world, prefix);
        }

        /// <summary>
        /// One object of a document under <paramref name="world"/> (metres), named <paramref name="name"/>: a managed object
        /// as before; a kernel object built by the worker (R-em3d64-3a); a DISABLED operation as its operands, each an
        /// ordinary object at this place in construction order and carried by the operation's placement (R-em3d64-4).
        /// </summary>
        private void Object(C3dObject obj, string name, C3dTransform world, C3dDocument doc, TechResolution tech, string prefix,
                            string path, bool exact)
        {
            if (obj is C3dOperation { Enabled: false } off)
            {
                // The object inside takes the operation's name; a Tool keeps its own (R-em3d64-1d).
                var carried = C3dLowering.InMetres(off.Placement.ToTransform(), doc.DbuPerMicron).Then(world);
                bool integral = exact && off.Placement.ToTransform().IsIntegral;
                foreach (var (_, operand) in C3dOperands.Of(off))
                {
                    bool inner = ReferenceEquals(operand, C3dOperands.Inner(off));
                    Object(operand, inner ? name : prefix + operand.Name, carried, doc, tech, prefix, path, integral);
                }
                return;
            }
            if (C3dOperands.IsKernel(obj)) { KernelObject(obj, name, world, doc, tech, prefix, path, exact); return; }
            // brief-sar-3 R-sar3-2 — a phantom is its layers, outermost first, so the innermost wins (C3dPhantoms).
            if (obj is C3dPhantom phantom)
            {
                if (C3dPhantoms.ShapeFault(phantom) is { } fault) { _refusals.Add($"The phantom '{name}' {fault}."); return; }
                int first = _solids.Count;
                foreach (var (layer, solid) in C3dPhantoms.Layers(phantom))
                    Managed(solid, C3dPhantoms.LayerName(name, layer.Name), world, doc, tech, prefix, path, exact);
                // brief-sar-8 — a voxelized phantom's layers share one lattice, a corner on its centre in the world.
                if (phantom.Voxel > 0 && _solids.Count > first)
                {
                    var frame = C3dLowering.InMetres(phantom.Placement.ToTransform(), doc.DbuPerMicron).Then(world);
                    double M(long v) => C3dLowering.Metres(v, doc.DbuPerMicron);
                    var lattice = new Em3dVoxelLattice(name, C3dLowering.Apply(frame, new Point3(M(phantom.Centre.X), M(phantom.Centre.Y), M(phantom.Centre.Z))),
                                                       M(phantom.Voxel));
                    for (int i = first; i < _solids.Count; i++) _solids[i] = _solids[i] with { Voxel = lattice };
                    _notes.Add($"The phantom '{name}' is given to openEMS in {FdtdGrid.FormatLength(lattice.PitchM)} voxels, each the innermost " +
                               "layer holding its centre; Palace and the thermal solver mesh its smooth layers.");
                }
                return;
            }
            // brief-sar-11 — a voxel body is its tissues, a mesh its surface: each read from its own file beside the document.
            if (obj is C3dVoxelBody body) { VoxelBody(body, name, world, doc, tech, prefix, path, exact); return; }
            if (obj is C3dMesh mesh) { MeshSolid(mesh, name, world, doc, tech, prefix, path, exact); return; }
            Managed(obj, name, world, doc, tech, prefix, path, exact);
        }

        /// <summary>
        /// brief-sar-11 R-sar11-1/-3 — a voxel body: one dielectric solid per mapped label row, named as a phantom's layer is, each
        /// holding the voxels its label carries (<see cref="Em3dSolid.Labels"/>) and drawn by its decimated surface. A row whose
        /// material the technology lacks is a refusal, as an object's is.
        /// </summary>
        private void VoxelBody(C3dVoxelBody body, string name, C3dTransform world, C3dDocument doc, TechResolution tech, string prefix,
                               string path, bool exact)
        {
            Read(C3dVoxelBodies.FileOf(body, path));
            var placed = C3dLowering.InMetres(body.Placement.ToTransform(), doc.DbuPerMicron).Then(world);
            var (resolved, refusal) = C3dVoxelBodies.Resolve(body, name, path, placed, doc.DbuPerMicron);
            if (resolved is null) { _refusals.Add(refusal!); return; }
            _notes.AddRange(resolved.Notes);
            string techName = TechName(tech);
            Know(tech, techName);
            int first = _solids.Count;
            foreach (var t in resolved.Tissues)
            {
                string tissue = C3dPhantoms.LayerName(name, t.Row.Name);
                if (C3dProblemAssembly.ObjectMaterial(tech.Tech, t.Row.Material!) is not { } material)
                {
                    _refusals.Add($"'{tissue}' is made of '{t.Row.Material}', which its technology ({techName}) does not define.");
                    continue;
                }
                int before = _solids.Count;
                Add(body, tissue, new C3dLowered(t.Drawing, null, C3dMesh.FaceNameList, "voxel-tissue"), material, Em3dRole.Dielectric,
                    world, tech, prefix, path, exact);
                if (_solids.Count > before) _solids[^1] = _solids[^1] with { Labels = resolved.Volume, Label = t.Entry };
            }
            if (_solids.Count > first)
            {
                var v = resolved.Volume;
                string step = v.GridStepM > 0 ? $"cells of at most {FdtdGrid.FormatLength(v.GridStepM)}, each the voxel its centre lies in"
                                              : "a grid line on every voxel boundary";
                _notes.Add($"The voxel body '{name}' is {v.Nx} × {v.Ny} × {v.Nz} voxels in {resolved.Tissues.Count} tissue(s), given to openEMS as one " +
                           $"DiscMaterial with {step}; Palace and the thermal solver do not run it.");
            }
        }

        /// <summary>brief-sar-11 R-sar11-4 — a per-tissue surface: its file read, checked and placed, as a solid with no B-rep.</summary>
        private void MeshSolid(C3dMesh mesh, string name, C3dTransform world, C3dDocument doc, TechResolution tech, string prefix,
                               string path, bool exact)
        {
            Read(C3dMeshes.FileOf(mesh, path));
            var placed = C3dLowering.InMetres(mesh.Placement.ToTransform(), doc.DbuPerMicron).Then(world);
            var (lowered, refusal) = C3dMeshes.Lower(mesh, name, path, placed);
            if (lowered is null) { _refusals.Add(refusal!); return; }
            string techName = TechName(tech);
            Know(tech, techName);
            if (mesh.Material is not { Length: > 0 } matName)
            {
                if (!_off) _warnings.Add(NoMaterialWarning(name));
                _unassignedSolids.Add(new Em3dSolid(name, "", Em3dRole.Dielectric, lowered.Solid!, 0));
                _provenance[name] = new C3dProvenance(prefix.TrimEnd('/'), path, name[prefix.Length..], lowered.FaceNames)
                {
                    TopObject = _topObject, Transparency = _transparency, Opacity = _opacity, Appearance = _appearance,
                    InstanceAppearances = _instanceAppearances, Exact = exact && mesh.Placement.ToTransform().IsIntegral,
                    Element = prefix.Length > 0 ? world : null,
                };
                return;
            }
            if (C3dProblemAssembly.ObjectMaterial(tech.Tech, matName) is not { } material)
            {
                _refusals.Add($"'{name}' is made of '{matName}', which its technology ({techName}) does not define.");
                return;
            }
            if (Role(mesh.Role, material, name) is not { } role) return;
            Add(mesh, name, lowered, material, role, world, tech, prefix, path, exact);
        }

        private void Managed(C3dObject obj, string name, C3dTransform world, C3dDocument doc, TechResolution tech, string prefix,
                             string path, bool exact)
        {
            string techName = TechName(tech);
            Know(tech, techName);
            if (obj.Material is not { Length: > 0 } matName)
            {
                // 3D editor bugs round 2 — an object with no material yet is IGNORED by the solver, not a refusal: a
                // half-finished design still runs. One naming a material the technology lacks stays a refusal below — a
                // broken reference, not an unassigned object. brief-em3d-101 R-em3d101-1d — and only what a solve sees is warned
                // of: a not-modelled object (an image sheet, as it is placed) is left out of every run whatever its material.
                if (!_off) _warnings.Add(NoMaterialWarning(name));
                Unassigned(obj, name, world, doc.DbuPerMicron, prefix, path, exact);
                return;
            }
            if (C3dProblemAssembly.ObjectMaterial(tech.Tech, matName) is not { } material)
            {
                _refusals.Add($"'{name}' is made of '{matName}', which its technology ({techName}) does not define.");
                Unassigned(obj, name, world, doc.DbuPerMicron, prefix, path, exact);
                return;
            }
            if (Role(obj.Role, material, name) is not { } role) return;

            var lowered = owner.LowerCached(obj, world, doc.DbuPerMicron);
            if (lowered is null) return;
            Add(obj, name, lowered, material, role, world, tech, prefix, path, exact);
        }

        /// <summary>A lowered object into the problem: its solid or sheet, material, provenance, origin and net.</summary>
        private void Add(C3dObject obj, string name, C3dLowered lowered, TechMaterial material, Em3dRole role, C3dTransform world,
                         TechResolution tech, string prefix, string path, bool exact)
        {
            string techName = TechName(tech);
            Know(tech, techName);
            var values = Resolve(material, out string source);
            string key = Register(techName, material.Name, values, TechSource(tech, techName, material.Name) + source);

            if (lowered.Refusal is { } cannot)
            {
                _refusals.Add(cannot);
                return;
            }
            if (lowered.CylinderSheet is { } patch)
            {
                // brief-sar-8b R-sar8b-2 — a sheet on a cylinder: a conductor, as every sheet is.
                if (role != Em3dRole.Conductor)
                {
                    _refusals.Add($"Sheet '{name}' is made of '{material.Name}', a {role.ToString().ToLowerInvariant()}: a sheet is a " +
                                  "conductor thin enough to be a surface. Give it a conducting material.");
                    return;
                }
                _cylinderUses.Add((_cylinderSheets.Count, techName, material.Name));
                _cylinderSheets.Add(new Em3dCylinderSheet(name, key, patch, ++_order));
            }
            else if (lowered.Sheet is { } g)
            {
                if (role != Em3dRole.Conductor)
                {
                    _refusals.Add($"Sheet '{name}' is made of '{material.Name}', a {role.ToString().ToLowerInvariant()}: a sheet is a " +
                                  "conductor thin enough to be a surface, so a sheet of anything else has no meaning. Make it a " +
                                  "solid, or give it a conducting material.");
                    return;
                }
                double t = (obj as C3dSheet)?.ThicknessUm is { } um ? um * 1e-6 : 0;
                _uses.Add((_sheets.Count, true, techName, material.Name));
                _sheets.Add(new Em3dSheet(name, key, g.Outline, g.Holes, g.Z, t, ++_order) { Frame = g.Frame });
            }
            else
            {
                _uses.Add((_solids.Count, false, techName, material.Name));
                _solids.Add(new Em3dSolid(name, key, role, lowered.Solid!, ++_order));
            }
            MarkOff(name);
            _provenance[name] = new C3dProvenance(prefix.TrimEnd('/'), path, name[prefix.Length..], lowered.FaceNames)
            {
                TopObject = _topObject,
                Transparency = _transparency,
                Opacity = _opacity,
                Appearance = _appearance,
                InstanceAppearances = _instanceAppearances,
                Exact = exact && obj.Placement.ToTransform().IsIntegral,
                Element = prefix.Length > 0 ? world : null,
            };
            _walkLowering.Add(new C3dWalkStep(name, lowered.Kind));
            _origins[name] = new Em3dObjectOrigin(role switch
            {
                Em3dRole.Conductor => Em3dObjectKind.Conductor, Em3dRole.Air => Em3dObjectKind.Air, _ => Em3dObjectKind.Body,
            }, null, obj.DrawingLayer, null);         // the layer only colours it; no stackup entry, so nothing treats it as board
            if (role == Em3dRole.Conductor) Net(name, name);
        }

        /// <summary>
        /// brief-em3d-64 R-em3d64-3 — a Boolean, Fillet, Chamfer or Step object, built by the geometry kernel: the Blank's
        /// material and role; with no kernel, or a build that fails, THIS object's refusal and nothing drawn. A kept Tool
        /// follows the result in construction order (R-em3d64-3c).
        /// </summary>
        private void KernelObject(C3dObject obj, string name, C3dTransform world, C3dDocument doc, TechResolution tech, string prefix,
                                  string path, bool exact)
        {
            string label = $"'{name}' ({C3dOperands.Article(obj)})";
            _capability ??= owner.Kernel.Capability;
            if (!_capability.Available)
            {
                Kernel(name, GeometryKernel.NeedsKernel(label, _capability), obj, null);
                KeptTools(obj, world, doc, tech, prefix, path, exact);
                return;
            }

            // A tree the kernel could not mean (no Blank, a sheet as a Tool) is this object's refusal, in validation's words.
            if (C3dValidation.OperationFindings(obj, name) is [var first, ..])
            {
                Kernel(name, first.Render(), obj, null);
                return;
            }

            string techName = TechName(tech);
            Know(tech, techName);
            string? matName = C3dValidation.EffectiveMaterial(obj);
            TechMaterial? material = matName is { Length: > 0 } ? C3dProblemAssembly.ObjectMaterial(tech.Tech, matName) : null;
            if (matName is { Length: > 0 } && material is null)
                _refusals.Add($"'{name}' is made of '{matName}', which its technology ({techName}) does not define.");

            var worldUm = world with { Tx = world.Tx * 1e6, Ty = world.Ty * 1e6, Tz = world.Tz * 1e6 };
            var tree = GeometryKernelTree.From(obj, doc.DbuPerMicron, worldUm, Path.GetDirectoryName(path));
            var built = owner.KernelCached(tree, name);
            if (built.Empty) built = built with { Refusal = C3dBooleans.EmptyResult(obj, name) };
            // brief-em3d-67 R-em3d67-5d / -6c — a fillet's or chamfer's refusal names the edge, in the user's terms.
            else if (built.Failure is { } failure && C3dFillets.Worded(failure, obj, name, doc.DisplayUnit, doc.DbuPerMicron) is { } said)
                built = built with { Refusal = said };
            Kernel(name, built.Refusal, obj, built);
            if (built.Lowered is not { } lowered) { KeptTools(obj, world, doc, tech, prefix, path, exact); return; }

            if (obj is C3dBoolean { Op: C3dBooleanOp.Unite } unite && material is not null)
            {
                // D11 — a Unite of different materials is allowed; the result is the Blank's, and says so.
                var replaced = unite.Tools.Select(t => (t.Name, Material: C3dValidation.EffectiveMaterial(t)))
                                          .Where(t => t.Material is { Length: > 0 } m && m != material.Name).ToList();
                if (replaced.Count > 0)
                    _notes.Add($"'{name}' unites objects of different materials, and the result is made of its Blank's, '{material.Name}': " +
                               string.Join(", ", replaced.Select(t => $"'{t.Name}' ({t.Material})")) + $" {(replaced.Count == 1 ? "is" : "are")} " +
                               $"'{material.Name}' in the result.");
            }

            if (matName is not { Length: > 0 })
            {
                if (!_off) _warnings.Add(NoMaterialWarning(name));
                _unassignedSolids.Add(new Em3dSolid(name, "", Em3dRole.Dielectric, lowered.Solid!, 0));
                _provenance[name] = new C3dProvenance(prefix.TrimEnd('/'), path, name[prefix.Length..], lowered.FaceNames)
                {
                    TopObject = _topObject,
                    Transparency = _transparency,
                    Opacity = _opacity,
                    Appearance = _appearance,
                    InstanceAppearances = _instanceAppearances,
                    Exact = exact && obj.Placement.ToTransform().IsIntegral,
                    Element = prefix.Length > 0 ? world : null,
                };
            }
            else if (material is not null && Role(C3dValidation.EffectiveRole(obj), material, name) is { } role)
                Add(obj, name, Operands(obj, lowered, world, doc.DbuPerMicron, prefix), material, role, world, tech, prefix, path, exact);
            KeptTools(obj, world, doc, tech, prefix, path, exact);
        }

        /// <summary>
        /// brief-em3d-120 R-em3d120-4b — a Subtract whose Blank and Tools are plain objects carries them, lowered exactly as
        /// they would be on their own and placed where the kernel placed them, so a cylindrical openEMS grid can cut a bore by
        /// priority instead of tessellating it. A hint on the kernel solid; anything else is returned as it was.
        /// </summary>
        private static C3dLowered Operands(C3dObject obj, C3dLowered lowered, C3dTransform world, int dbuPerMicron, string prefix)
        {
            if (obj is not C3dBoolean { Op: C3dBooleanOp.Subtract, Enabled: true, Blank: { } blank } b || lowered.Solid is not Em3dShapeSolid k ||
                blank is C3dOperation || b.Tools.Count == 0 || b.Tools.Any(t => t is C3dOperation))
                return lowered;
            var carried = C3dLowering.InMetres(obj.Placement.ToTransform(), dbuPerMicron).Then(world);
            if (C3dLowering.Lower(blank, carried, dbuPerMicron)?.Solid is not { } blankPrim) return lowered;
            var tools = new List<Em3dOperandTool>();
            foreach (var t in b.Tools)
            {
                if (C3dLowering.Lower(t, carried, dbuPerMicron)?.Solid is not { } toolPrim) return lowered;
                tools.Add(new Em3dOperandTool(t.Name, toolPrim, b.KeepTools ? prefix + t.Name : null));
            }
            return lowered with { Solid = k with { Operands = new Em3dOperands(blankPrim, tools) } };
        }

        /// <summary>
        /// 3D editor round 5 — a DISABLED Boolean inside an enabled operation is as if it were not there (R-em3d64-4), as at
        /// the top level: the kernel tree carries its Blank in its place (GeometryKernelTree.WriteNode), and each of its Tools
        /// is its own object here, right after the result, carried by every placement on the way down. Nested deeper than
        /// the first disabled one too — a disabled Boolean's Blank may itself hold one.
        /// </summary>
        private void NestedTools(C3dObject node, C3dTransform parent, C3dDocument doc, TechResolution tech, string prefix,
                                 string path, bool exact)
        {
            if (node is not C3dOperation op) return;
            var frame = C3dLowering.InMetres(op.Placement.ToTransform(), doc.DbuPerMicron).Then(parent);
            bool integral = exact && op.Placement.ToTransform().IsIntegral;
            if (op.Enabled)
            {
                // brief-em3d-70 — an enabled Subtract with KeepTools nested inside another operation keeps its Tools too
                // (a bore kept as fill in a housing that is then united with a flange); only the top level did before.
                List<C3dObject> kept = op is C3dBoolean { Op: C3dBooleanOp.Subtract, KeepTools: true } k ? k.Tools : [];
                foreach (var tool in kept) Object(tool, prefix + tool.Name, frame, doc, tech, prefix, path, integral);
                foreach (var (_, operand) in C3dOperands.Of(op))
                    if (!kept.Contains(operand)) NestedTools(operand, frame, doc, tech, prefix, path, integral);
                return;
            }
            // A Tool is a whole object in its own right: Object elaborates anything nested inside it.
            if (op is C3dBoolean b)
                foreach (var tool in b.Tools) Object(tool, prefix + tool.Name, frame, doc, tech, prefix, path, integral);
            if (C3dOperands.Inner(op) is { } inner) NestedTools(inner, frame, doc, tech, prefix, path, integral);
        }

        /// <summary>R-em3d64-3c — a Subtract's Tools with <c>KeepTools</c>: each its own object right after the result. 3D editor
        /// round 5: and then the Tools of every disabled Boolean inside it (<see cref="NestedTools"/>) — a kept Tool's own
        /// are its <see cref="Object"/>'s to elaborate, so they are not walked twice.</summary>
        private void KeptTools(C3dObject obj, C3dTransform world, C3dDocument doc, TechResolution tech, string prefix, string path, bool exact)
        {
            var carried = C3dLowering.InMetres(obj.Placement.ToTransform(), doc.DbuPerMicron).Then(world);
            bool integral = exact && obj.Placement.ToTransform().IsIntegral;
            List<C3dObject> kept = obj is C3dBoolean { Op: C3dBooleanOp.Subtract, KeepTools: true } b ? b.Tools : [];
            foreach (var tool in kept) Object(tool, prefix + tool.Name, carried, doc, tech, prefix, path, integral);
            foreach (var (_, operand) in C3dOperands.Of(obj))
                if (!kept.Contains(operand)) NestedTools(operand, carried, doc, tech, prefix, path, integral);
        }

        /// <summary>A kernel object's refusal (when it has one) and its report for <c>explain</c>.</summary>
        private void Kernel(string name, string? refusal, C3dObject obj, KernelLowered? built)
        {
            if (refusal is not null)
            {
                _kernelRefusals[name] = refusal;
                _refusals.Add(refusal);
            }
            var faces = built?.Lowered?.Solid is Em3dShapeSolid k ? k.Faces.Count : 0;
            _kernelBuilds.Add(new C3dKernelBuild(name, C3dObject.KindOf(obj), OperandTree(obj, name), built?.Built ?? false, faces,
                                                 built?.Edges ?? 0, built?.MinRadiusM, built?.Notes ?? [], refusal)
                              { Solids = built?.Lowered is null ? 0 : built.Solids });
            if (built?.Lowered is { } l) _walkLowering.Add(new C3dWalkStep(name, $"{l.Kind} ({C3dObject.KindOf(obj)}, built by the geometry kernel)"));
        }

        /// <summary>The operand tree, one line per node: <c>Boolean Subtract 'lid'</c>, then <c>  Blank: Box</c> …</summary>
        private static List<string> OperandTree(C3dObject obj, string name)
        {
            var lines = new List<string>();
            void Walk(C3dObject o, string label, int depth)
            {
                string what = o switch
                {
                    C3dBoolean b => $"Boolean {b.Op}",
                    C3dFillet f => $"Fillet of {string.Join(", ", f.Edges)}",
                    C3dChamfer c => $"Chamfer of {string.Join(", ", c.Edges)}",
                    C3dStep st => $"Step part {st.Part}{(st.Solid is { } k ? $", solid {k}" : "")} of {st.File}{(st.Unit is { Length: > 0 } u ? $" (in {u})" : "")}",
                    _ => C3dObject.KindOf(o),
                };
                string state = o is C3dOperation { Enabled: false } ? " (disabled)" : "";
                string material = o.Material is { Length: > 0 } m ? $", {m}" : "";
                lines.Add($"{new string(' ', 2 * depth)}{label}{what}{state}{material}");
                foreach (var (prefix, child) in C3dOperands.Of(o))
                    Walk(child, (ReferenceEquals(child, C3dOperands.Inner(o)) ? prefix.TrimEnd('.') : $"Tool '{child.Name}'") + ": ", depth + 1);
            }
            Walk(obj, $"'{name}': ", 0);
            return lines;
        }

        /// <summary>3D editor bugs round 1 — an object refused for its material, lowered for the editor alone (see
        /// <see cref="C3dElaboration.UnassignedSolids"/>): it takes no order, no net and no material key, but it keeps its
        /// provenance, which is what the editor's picking, faces and edits read.</summary>
        private void Unassigned(C3dObject obj, string name, C3dTransform world, int dbuPerMicron, string prefix, string path, bool exact)
        {
            if (owner.LowerCached(obj, world, dbuPerMicron) is not { } lowered) return;
            // brief-sar-8b — a cylindrical sheet with no material is not drawn: it has no planar outline to draw as a wireframe.
            if (lowered.CylinderSheet is not null || lowered.Refusal is not null) { }
            else if (lowered.Sheet is { } g)
            {
                double t = (obj as C3dSheet)?.ThicknessUm is { } um ? um * 1e-6 : 0;
                _unassignedSheets.Add(new Em3dSheet(name, "", g.Outline, g.Holes, g.Z, t, 0) { Frame = g.Frame });
            }
            else _unassignedSolids.Add(new Em3dSolid(name, "", Em3dRole.Dielectric, lowered.Solid!, 0));
            _provenance[name] = new C3dProvenance(prefix.TrimEnd('/'), path, name[prefix.Length..], lowered.FaceNames)
            {
                TopObject = _topObject,
                Transparency = _transparency,
                Opacity = _opacity,
                Appearance = _appearance,
                InstanceAppearances = _instanceAppearances,
                Exact = exact && obj.Placement.ToTransform().IsIntegral,
                Element = prefix.Length > 0 ? world : null,
            };
        }

        /// <summary>brief-em3d-50 — the document's drawn wires, landed on the conductors of this document and everything
        /// below it (never a sibling's: a wire in a child lands inside that child).</summary>
        private void Wires(C3dDocument doc, string path, TechResolution tech, C3dTransform world, string prefix)
        {
            string techName = TechName(tech);
            Know(tech, techName);
            var workspace = new WireBondWorkspace(path, WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(path)) ?? workspaceCws);
            // brief-em3d-135 — a held loop height or span shapes the drawn wire before its elements are copied from it.
            var held = new Dictionary<C3dWire, string>();
            bool Top(string name, HashSet<string> set) => prefix.Length == 0 && set.Contains(name);
            var drawnWires = doc.Objects.OfType<C3dWire>().Where(src => !Top(src.Name, _broken)).ToList();
            foreach (var src in drawnWires)
                if (!Top(src.Name, _unheld) && C3dWires.Hold(src, prefix + src.Name, doc.DbuPerMicron, workspace) is { } why) held[src] = why;
            var elements = drawnWires.SelectMany(src => C3dWires.Elements(src).Select(e => (Source: src, e.Wire))).ToList();
            var wireNames = new HashSet<string>(elements.Select(e => prefix + e.Wire.Name), StringComparer.Ordinal);
            var pads = C3dWires.Pads(_solids, _sheets, prefix, wireNames);
            double tol = C3dLowering.Metres(1, _topDbu);
            if (elements.Count > 0) Read(workspace.WorkspaceRules().ResolvedPath);
            // 3D editor round 4 — a wire array is one wire per element, each resolved and refused on its own (w1[2]).
            foreach (var (drawn, w) in elements)
                Modelled(drawn.Model, () => Wire(drawn, w));
            void Wire(C3dWire drawn, C3dWire w)
            {
                string name = prefix + w.Name;
                string matName = C3dWires.MaterialOf(w);
                string? refusal = null;
                TechMaterial? material = tech.Tech?.FindMaterial(matName);
                if (material is null)
                {
                    var metals = tech.Tech?.ResolvedMaterials.Where(m => m.Sigma20 is not null).Select(m => m.Name).ToList() ?? [];
                    refusal = $"Wire '{name}' is made of '{matName}', which its technology ({techName}) does not define. Known metals: " +
                              $"{(metals.Count == 0 ? "none" : string.Join(", ", metals))}. A 3D model does not substitute a metal.";
                }
                else if (material.Sigma20 is null)
                    refusal = $"Wire '{name}' is made of '{matName}', which technology '{techName}' defines with no conductivity " +
                              "(Sigma20), so it is not a metal.";
                else if (w.Role is { } role && role != Em3dRole.Conductor)
                    refusal = $"Wire '{name}' has the role {role}; a wire is a conductor.";
                refusal ??= held.GetValueOrDefault(drawn);
                C3dWireResult? result = null;
                if (refusal is null)
                {
                    result = C3dWires.Resolve(w, name, world, doc.DbuPerMicron, pads, tol, workspace, LayoutUnits.AsciiSuffix(doc.DisplayUnit));
                    _drawnWires[name] = result;
                    refusal = result.Refusal;
                }
                if (refusal is not null || result?.Resolution is not { Sweep: { } sweep } r)
                {
                    refusal ??= $"Wire '{name}' could not be built.";
                    _wireRefusals[name] = refusal;
                    _refusals.Add(refusal);
                    return;
                }
                _warnings.AddRange(r.Warnings);
                var values = Resolve(material!, out string source);
                string key = Register(techName, material!.Name, values, TechSource(tech, techName, material!.Name) + source);
                void Solid(string solidName, Em3dPrimitive primitive)
                {
                    _uses.Add((_solids.Count, false, techName, material.Name));
                    _solids.Add(new Em3dSolid(solidName, key, Em3dRole.Conductor, primitive, ++_order));
                    _provenance[solidName] = new C3dProvenance(prefix.TrimEnd('/'), path, drawn.Name, [])
                    {
                        Exact = false, Element = prefix.Length > 0 ? world : null, Transparency = drawn.Transparency, Opacity = _opacity,
                        Appearance = drawn.Appearance, InstanceAppearances = _instanceAppearances,
                    };
                    _origins[solidName] = new Em3dObjectOrigin(Em3dObjectKind.Wire, null, null, null);
                    Net(solidName, name);
                    MarkOff(solidName);
                }
                Solid(name, sweep);
                foreach (var (ballName, ball) in r.Balls) Solid(ballName, ball);
                _walkLowering.Add(new C3dWalkStep(name, $"wire: {r.Report!.Section} sweep of {sweep.Rings.Count} sections from " +
                                                        $"'{result.StartPad!.Name}' to '{result.EndPad!.Name}'"));
                _wires.Add(r.Report with { Material = material.Name });
                foreach (var (v, what) in new[] { (result.StartProcess!, w.Start), (result.EndProcess!, w.End) })
                {
                    if (what.Style == WBond.BondStyle.Wedge && v.FootLength.Source == WireBondValueSource.BuiltIn)
                        _builtInWireValues.Add((name, "wedge-foot length"));
                    if (what.Style == WBond.BondStyle.Ball && v.BallDiameter.Source == WireBondValueSource.BuiltIn)
                        _builtInWireValues.Add((name, "ball diameter and height"));
                }
            }
        }

        /// <summary>One instance: resolved, then elaborated once per array element.</summary>
        private void Instance(C3dDocument doc, C3dResolution resolution, C3dInstance inst, string baseDir, C3dTransform world, string prefix,
                              List<(string Cell, string Instance, string Path)> stack, bool exact)
        {
            string instPath = prefix + inst.Name;
            var viewType = inst.View == C3dInstanceView.Layout ? ViewType.Layout : ViewType.ThreeD;
            string noun = CellFolder.ViewNoun(viewType);
            string? cellDir = ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir, out _, out bool exists);
            if (cellDir is null || !exists)
            {
                _unresolved.Add((instPath, inst.CellRef));
                _refusals.Add($"Instance '{instPath}' places cell '{inst.CellRef}', which resolves to nothing" +
                              (cellDir is null ? " (the reference cannot be worked out from here)." : $" (tried {cellDir})."));
                return;
            }
            var primary = CellFolder.ResolvePrimary(cellDir, viewType);
            if (primary.ResolvedName is not { } file)
            {
                _unresolved.Add((instPath, inst.CellRef));
                _refusals.Add($"Instance '{instPath}' places the {noun} of cell '{Path.GetFileName(cellDir)}', which has " +
                              $"{(primary.State == PrimaryState.NoView ? $"no {noun}" : $"no primary {noun}")} " +
                              $"(tried {CellFolder.SubFolderPath(cellDir, viewType)}).");
                return;
            }
            string viewPath = Path.Combine(CellFolder.SubFolderPath(cellDir, viewType), file);

            var counts = inst.Array?.Counts ?? [1, 1, 1];
            if (counts.Count != 3 || counts.Any(n => n < 1))
            {
                _refusals.Add($"Instance '{instPath}' is an array of [{string.Join(", ", counts)}]; an array states three counts, each at least 1.");
                return;
            }
            var pitch = inst.Array?.Pitch ?? default;
            bool isArray = inst.Array is not null && counts.Any(n => n > 1);

            if (viewType == ViewType.ThreeD)
            {
                if (stack.FindIndex(s => string.Equals(s.Path, viewPath, StringComparison.OrdinalIgnoreCase)) is int at && at >= 0)
                {
                    var cycle = new StringBuilder();
                    for (int k = at; k < stack.Count; k++)
                        cycle.Append(k == at ? "" : " → ").Append(stack[k].Cell).Append(k + 1 < stack.Count ? "/" + stack[k + 1].Instance : "/" + inst.Name);
                    cycle.Append(" → ").Append(stack[at].Cell);
                    _refusals.Add($"The 3D view reaches itself through its instances: {cycle}. A cell cannot contain itself.");
                    return;
                }
                var child = owner.Child3DCached(viewPath, workspaceCws);
                Read(viewPath);
                ReadTech(child.Tech);
                _walkInstances.Add(new C3dWalkStep(instPath,
                    $"cell folder {cellDir} (from '{inst.CellRef}'); {noun} {viewPath} ({primary.State}); technology " +
                    $"{child.Tech.ResolvedPath ?? "(none)"} ({child.Tech.Source}, resolved from the 3D view's own path)" +
                    (isArray ? $"; array {counts[0]}×{counts[1]}×{counts[2]}" : "")));
                if (child.Refusal is { } why)
                {
                    _unresolved.Add((instPath, inst.CellRef));
                    _refusals.Add($"Instance '{instPath}' places '{viewPath}', which cannot be read: {why}");
                    return;
                }
                // brief-em3d-51 R-em3d51-2e — the overrides, evaluated in THIS document's scope, then the child resolved under
                // their values (shared with every other instance whose values are the same).
                var childCell = C3dCell.OfFolder(cellDir);
                Read(childCell.CcellPath);
                var overrideErrors = new List<string>();
                var overrides = resolution.OverridesFor(inst, instPath, childCell, child.Document.Variables, overrideErrors);
                if (overrideErrors.Count > 0) { _refusals.AddRange(overrideErrors); return; }
                var (resolvedDoc, childRes) = owner.ResolvedChild(viewPath, child, childCell, overrides);
                if (!childRes.Ok)
                {
                    foreach (string e in childRes.Errors) _refusals.Add($"Instance '{instPath}' ({viewPath}): {e}");
                    return;
                }
                foreach (string w in childRes.Warnings) _warnings.Add($"{instPath}: {w}");
                foreach (string n in childRes.Notes) _notes.Add($"{instPath}: {n}");
                var next = new List<(string, string, string)>(stack) { (CellOf(viewPath), inst.Name, viewPath) };
                double outer = _opacity;
                // brief-em3d-92 — the instance's transparency multiplies onto everything it places, at every depth.
                if (inst.Transparency is { } it) _opacity *= C3dTransparency.Opacity(Math.Clamp(it, 0, C3dTransparency.Max));
                var outerLooks = _instanceAppearances;
                try
                {
                    foreach (var (ijk, w, integral) in Elements(doc, inst, counts, pitch, world))
                    {
                        string elementPath = prefix + inst.Name + (isArray ? ijk : "");
                        // brief-em3d-105 — the instance's appearance goes in front of the outer ones: the innermost wins.
                        if (inst.Appearance is { } look) _instanceAppearances = [new Appearance.AppearanceInstanceStatement(prefix + inst.Name, look), .. outerLooks];
                        _instances.Add(new C3dInstanceFrame(elementPath, w, viewPath, Path.GetFileName(cellDir), Layout: false));
                        Document(resolvedDoc, childRes, viewPath, child.Tech, w, elementPath + "/", next,
                                 exact && integral && resolvedDoc.DbuPerMicron == _topDbu);
                    }
                }
                finally { (_opacity, _instanceAppearances) = (outer, outerLooks); }
                return;
            }

            _anyLayoutInstance = true;
            var layout = owner.ChildLayoutCached(viewPath, workspaceCws, options);
            Read(viewPath);
            ReadTech(layout.Tech);
            foreach (string f in layout.Files) Read(f);
            _walkInstances.Add(new C3dWalkStep(instPath,
                $"cell folder {cellDir} (from '{inst.CellRef}'); {noun} {viewPath} ({primary.State}); technology " +
                $"{layout.Tech.ResolvedPath ?? "(none)"} ({layout.Tech.Source}, resolved from the layout's own path — the " +
                $"layout's technology, never the parent's)" + (isArray ? $"; array {counts[0]}×{counts[1]}×{counts[2]}" : "")));
            if (layout.Refusal is { } refusal || layout.Solids is not { } solids)
            {
                _unresolved.Add((instPath, inst.CellRef));
                // Issue #35 — the label is all the canvas says, so the commonest cause is named on it.
                _unresolvedWhy[instPath] = layout.Solids?.RefusedOnWires == true ? "invalid wBond geometry"
                                         : layout.Tech.Tech is null ? "its layout has no technology"
                                         : "cannot be drawn in 3D";
                _refusals.Add($"Instance '{instPath}' places layout '{viewPath}', which cannot be put in 3D: {layout.Refusal}");
                return;
            }
            if (solids.Notes.Count > 0 || solids.Warnings.Count > 0)
            {
                foreach (string n in solids.Notes) _notes.Add($"{instPath}: {n}");
                foreach (string w in solids.Warnings) _warnings.Add($"{instPath}: {w}");
            }
            if (solids.PadRefusal is { } padRefusal && !solids.Warnings.Any(w => w.Contains(padRefusal, StringComparison.Ordinal)))
                _warnings.Add($"{instPath}: {padRefusal}");
            if (LayoutHasPortShapes(viewPath)) _ignored.Add("a layout's port shapes");
            double outerOpacity = _opacity;
            if (inst.Transparency is { } lt) _opacity *= C3dTransparency.Opacity(Math.Clamp(lt, 0, C3dTransparency.Max));
            var outerAppearances = _instanceAppearances;
            foreach (var (ijk, w, integral) in Elements(doc, inst, counts, pitch, world))
            {
                string elementPath = prefix + inst.Name + (isArray ? ijk : "");
                if (inst.Appearance is { } look) _instanceAppearances = [new Appearance.AppearanceInstanceStatement(prefix + inst.Name, look), .. outerAppearances];
                _instances.Add(new C3dInstanceFrame(elementPath, w, viewPath, Path.GetFileName(cellDir), Layout: true));
                Know(layout.Tech, layout.TechName);
                Layout(layout, solids, layout.TechName, viewPath, elementPath, w,
                       exact && integral && layout.DbuPerMicron == _topDbu);
            }
            (_opacity, _instanceAppearances) = (outerOpacity, outerAppearances);
        }

        /// <summary>Each array element's transform: the placement's rotation and mirror, and its origin moved
        /// by the pitch in the PARENT's frame (LayoutInstanceTransform.ArrayCellOrigin's rule), then the world.</summary>
        private static IEnumerable<(string Ijk, C3dTransform World, bool Integral)> Elements(C3dDocument parent, C3dInstance inst,
                                                                                IReadOnlyList<int> counts, C3dPoint3 pitch,
                                                                                C3dTransform world)
        {
            var placement = inst.Placement.ToTransform();
            for (int k = 0; k < counts[2]; k++)
                for (int j = 0; j < counts[1]; j++)
                    for (int i = 0; i < counts[0]; i++)
                    {
                        var t = placement with
                        {
                            Tx = placement.Tx + (double)i * pitch.X,
                            Ty = placement.Ty + (double)j * pitch.Y,
                            Tz = placement.Tz + (double)k * pitch.Z,
                        };
                        yield return ($"[{i},{j},{k}]", C3dLowering.InMetres(t, parent.DbuPerMicron).Then(world), t.IsIntegral);
                    }
        }

        /// <summary>A layout instance's solids under <paramref name="world"/>, its stack's bottom at the instance's z.</summary>
        private void Layout(object child, Em3dLayoutSolidsResult solids, string techName, string viewPath, string instPath, C3dTransform world,
                            bool exact)
        {
            var t = C3dTransform.Identity with { Tz = -solids.StackBottomM };
            t = t.Then(world);
            var (loweredSolids, loweredSheets) = owner.LayoutLowered(child, solids, t,
                s => C3dLowering.Transform(s.Primitive, FaceNamesOf(s.Primitive), t, KindOf(s.Primitive)));
            string prefix = instPath + "/";
            int baseOrder = _order;
            int maxOrder = 0;
            var materialKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in solids.Materials)
                materialKey[m.Name] = Register(techName, m.Name, m, solids.MaterialSources.GetValueOrDefault(m.Name) ?? $"technology '{techName}'");

            for (int si = 0; si < solids.Solids.Count; si++)
            {
                var s = solids.Solids[si];
                var lowered = loweredSolids[si];
                string name = prefix + s.Name;
                _uses.Add((_solids.Count, false, techName, s.Material));
                _solids.Add(new Em3dSolid(name, materialKey[s.Material], s.Role, lowered.Solid!, baseOrder + s.Order));
                maxOrder = Math.Max(maxOrder, s.Order);
                _provenance[name] = new C3dProvenance(instPath, viewPath, s.Name, lowered.FaceNames) { Exact = exact, Element = t, Opacity = _opacity, InstanceAppearances = _instanceAppearances };
                _walkLowering.Add(new C3dWalkStep(name, $"{lowered.Kind} (from the layout)"));
                MarkOff(name);
            }
            for (int hi = 0; hi < solids.Sheets.Count; hi++)
            {
                var sh = solids.Sheets[hi];
                var g = loweredSheets[hi];
                string name = prefix + sh.Name;
                _uses.Add((_sheets.Count, true, techName, sh.Material));
                _sheets.Add(new Em3dSheet(name, materialKey[sh.Material], g.Outline, g.Holes, g.Z, sh.ThicknessM, baseOrder + sh.Order)
                            { Frame = g.Frame });
                maxOrder = Math.Max(maxOrder, sh.Order);
                _provenance[name] = new C3dProvenance(instPath, viewPath, sh.Name, []) { Exact = exact, Element = t, Opacity = _opacity, InstanceAppearances = _instanceAppearances };
                MarkOff(name);
                _walkLowering.Add(new C3dWalkStep(name, $"{(g.Frame is null ? C3dLowering.KindSheet : C3dLowering.KindFramedSheet)} (from the layout)"));
            }
            _order = baseOrder + maxOrder;

            foreach (var (obj, nets) in solids.ObjectNets)
                foreach (string n in nets) Net(prefix + obj, n);
            foreach (var (obj, conflict) in solids.ViaNetConflicts) _viaNetConflicts[prefix + obj] = conflict;
            _groundBand.AddRange(solids.GroundBandObjects.Select(o => prefix + o));
            foreach (var (obj, origin) in solids.Origins) _origins[prefix + obj] = origin;
            // A pad top is a height in the layout's own stack; the thermal chain and its contact patches read it as a WORLD height,
            // so it moves with the pads. Only a placement that keeps z up has one (a tilted pad has no "top"): that one keeps it.
            double PadTop(double z) => t.M20 == 0 && t.M21 == 0 ? t.M22 * z + t.Tz : z;
            foreach (var w in solids.Wires)
            {
                MarkOff(prefix + w.Name);
                _wires.Add(w with
                {
                    Name = prefix + w.Name,
                    Start = w.Start with { Pad = prefix + w.Start.Pad, PadTopM = PadTop(w.Start.PadTopM) },
                    End = w.End with { Pad = prefix + w.End.Pad, PadTopM = PadTop(w.End.PadTopM) },
                });
            }
        }

        private static IReadOnlyList<string> FaceNamesOf(Em3dPrimitive p) => p switch
        {
            Em3dBox => Em3dTessellation.BoxFaces,
            Em3dExtrudedPolygon e => ["bottom", "top", .. Enumerable.Range(0, e.Outline.Count).Select(k => $"side{k}"),
                                      .. e.Holes.SelectMany((h, i) => Enumerable.Range(0, h.Count).Select(k => $"hole{i}.side{k}"))],
            Em3dCylinder => ["bottom", "top", "side"],
            _ => [],
        };

        private static string KindOf(Em3dPrimitive p) => p switch
        {
            Em3dBox => C3dLowering.KindBox, Em3dExtrudedPolygon => C3dLowering.KindExtrusion,
            Em3dCylinder => C3dLowering.KindCylinder, _ => C3dLowering.KindPolyhedron,
        };

        private static bool LayoutHasPortShapes(string clayPath)
        {
            try { return LayoutPersistence.LoadFromFile(clayPath).Shapes.Any(s => s is LabelShape { IsPort: true }); }
            catch { return false; }
        }

        /// <summary>
        /// R-em3d42-3e — the object's role: an explicit Role wins; otherwise a material with σ and no εr is a
        /// conductor, one with εr a dielectric, and <c>Air</c> is air. σ AND εr with no Role is refused: whether a
        /// lossy dielectric is a conductor is exactly the call the user must make.
        /// </summary>
        private Em3dRole? Role(Em3dRole? stated, TechMaterial m, string name)
        {
            if (stated is { } s) return s;
            // brief-em3d-53 §4 — the material's own answer comes from the one rule the editor's Role column shows.
            switch (C3dMaterialRole.Implied(m))
            {
                case C3dImpliedRole.Air:        return Em3dRole.Air;
                case C3dImpliedRole.Conductor:  return Em3dRole.Conductor;
                case C3dImpliedRole.Dielectric: return Em3dRole.Dielectric;
                case C3dImpliedRole.Ambiguous:
                    _refusals.Add($"'{name}' is made of '{m.Name}', which states both a conductivity and a permittivity, so it could be " +
                                  "a conductor or a lossy dielectric. Say which with the object's Role.");
                    return null;
                default:
                    _refusals.Add($"'{name}' is made of '{m.Name}', which states neither a conductivity nor a permittivity, so nothing says " +
                                  "what it is. Give the material σ₂₀ or εr, or give the object a Role.");
                    return null;
            }
        }

        /// <summary>brief-em3d-53 R-em3d53-6 — the FILE a material came from: the technology's own list, one of its
        /// libraries, or built-in free space (Air, Vacuum) when the technology defines neither.</summary>
        private static string TechSource(TechResolution tech, string techName, string material)
            => tech.Tech?.FindMaterial(material) is null ? "built in (free space)"
             : tech.Tech.LibrarySourceOf(material) is { } lib
                ? $"technology '{techName}' via library '{MaterialLibraries.Display(lib)}'"
                : $"technology '{techName}' Materials";

        /// <summary>A technology material's values at the operating temperature — the generator's body rule.</summary>
        private Em3dMaterial Resolve(TechMaterial m, out string note)
        {
            if (MaterialDispersion.IsDispersive(m) && options.MaterialFrequencyHz is { } f)
            {
                var at = MaterialDispersion.At(m, f, out string? refusal);
                if (refusal is not null && _dispersionRefused.Add(m.Name)) _refusals.Add(refusal);
                var v = MaterialValues(m, options.TempC, f);
                if (at is not null) _dispersive[m.Name] = (v.Epsr, v.SigmaSm);
                note = "";   // the frequency is the run's one note (below) and explain's row, not every source string
                return v;
            }
            note = m.Sigma20 is not null && m.Alpha20 is null && !MaterialDispersion.IsDispersive(m) ? " (σ₂₀, no α₂₀)" : "";
            return MaterialValues(m, options.TempC);
        }

        /// <summary>brief-sar-2 — each dispersive material evaluated in this elaboration, for the run's one note.</summary>
        private readonly SortedDictionary<string, (double Epsr, double Sigma)> _dispersive = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dispersionRefused = new(StringComparer.OrdinalIgnoreCase);

        private string Register(string tech, string name, Em3dMaterial values, string source)
        {
            if (!_materials.Any(x => x.Tech == tech && x.Name == name))
                _materials.Add((tech, name, values with { Name = name }, source));
            return name;
        }

        private readonly Dictionary<(string Tech, string Name), string> _final = [];
        private string Final(string tech, string name) => _final.GetValueOrDefault((tech, name), name);

        /// <summary>R-em3d42-3e — merge equal values under one name, qualify different ones with their technology.</summary>
        private (List<Em3dMaterial>, Dictionary<string, string>, List<C3dWalkStep>) Materials()
        {
            var list = new List<Em3dMaterial>();
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            var walk = new List<C3dWalkStep>();
            foreach (var group in _materials.GroupBy(m => m.Name, StringComparer.Ordinal))
            {
                var sets = new List<(Em3dMaterial Values, List<string> Techs, string Source, List<string> Sources)>();
                foreach (var m in group)
                {
                    int i = sets.FindIndex(s => Same(s.Values, m.Values));
                    if (i >= 0) { sets[i].Techs.Add(m.Tech); if (!sets[i].Sources.Contains(m.Source)) sets[i].Sources.Add(m.Source); }
                    else sets.Add((m.Values, [m.Tech], m.Source, [m.Source]));
                }
                if (sets.Count == 1)
                {
                    list.Add(sets[0].Values);
                    sources[group.Key] = sets[0].Source;
                    // brief-em3d-53 R-em3d53-7 — the step names the FILE that answered, and for a merged name every
                    // file that agreed.
                    walk.Add(new C3dWalkStep(group.Key, sets[0].Techs.Count > 1
                        ? $"equal in {string.Join(" and ", sets[0].Techs.Select(t => $"'{t}'"))}, merged ({string.Join("; ", sets[0].Sources)})"
                        : $"from '{sets[0].Techs[0]}' ({sets[0].Source})"));
                    continue;
                }
                var described = new List<string>();
                foreach (var (values, techs, source, _) in sets)
                {
                    string q = $"{group.Key}@{techs[0]}";
                    foreach (string t in techs) _final[(t, group.Key)] = q;
                    list.Add(values with { Name = q });
                    sources[q] = source;
                    described.Add($"'{q}' ({Describe(values)})");
                    walk.Add(new C3dWalkStep(q, $"'{group.Key}' in {string.Join(" and ", techs.Select(t => $"'{t}'"))}, kept apart"));
                }
                _notes.Add($"Material '{group.Key}' has different values in {sets.Count} technologies, so each is kept under its own " +
                           $"name: {string.Join(", ", described)}.");
            }
            return (list, sources, walk);

            static bool Same(Em3dMaterial a, Em3dMaterial b) =>
                a.Epsr == b.Epsr && a.TanD == b.TanD && a.Mur == b.Mur && a.SigmaSm == b.SigmaSm &&
                (a.EpsrTensor ?? []).SequenceEqual(b.EpsrTensor ?? []);
            static string Describe(Em3dMaterial m) => string.Create(CultureInfo.InvariantCulture,
                $"εr {m.Epsr:G6}, tanδ {m.TanD:G6}, μr {m.Mur:G6}, σ {m.SigmaSm:G6} S/m");
        }

        private void Net(string obj, string net)
            => (_nets.TryGetValue(obj, out var set) ? set : _nets[obj] = new(StringComparer.Ordinal)).Add(net);

        /// <summary>The cell a view belongs to: its folder's name, above the view's own sub-folder.</summary>
        private static string CellOf(string viewPath)
        {
            string? dir = Path.GetDirectoryName(viewPath);
            return dir is not null && Path.GetFileName(dir) is var sub &&
                   string.Equals(sub, CellFolder.SubFolderName(ViewType.ThreeD), StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(Path.GetDirectoryName(dir)) ?? Path.GetFileNameWithoutExtension(viewPath)
                : Path.GetFileNameWithoutExtension(viewPath);
        }
    }
}
