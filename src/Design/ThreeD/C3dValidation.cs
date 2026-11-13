// brief-em3d-41 R-em3d41-2f — what is wrong with a 3D view that reads.
//
// ALL of it, not the first (R-em3d3-1c's rule): a list that stopped at the first defect would make a
// user fix a document one refusal at a time. One finding per defect — a duplicated name is one
// finding however many objects share it, an open polyhedron is one finding naming every bad edge —
// so "five defects report five" is a statement about the document and not about the validator's loop.
//
// This is the ONE validator: `circuitrf check` reports exactly these, and the editor (brief 43) shows
// exactly these on open. A rule living only in `check` would pass a document headlessly that the
// application then refuses (cli.md's `check` rule).

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.ThreeD;

public static class C3dValidation
{
    /// <summary>The name no object may have: the air box's faces are <c>airbox/xmin</c> ….</summary>
    public const string ReservedName = "airbox";

    /// <summary>How far a vertex may sit off its face's plane, in DBU.</summary>
    public const double PlanarityToleranceDbu = 1.0;

    /// <summary>
    /// Every problem <paramref name="doc"/> has.
    /// </summary>
    /// <param name="isKnownMaterial">
    /// Whether the document's technology defines a material name, or null when no technology resolved —
    /// in which case materials are not checked here at all, and the caller says why (a warning per
    /// object for a missing technology would bury the one finding that matters).
    /// </param>
    /// <param name="unresolved">brief-em3d-51 — items whose expression fields did not resolve: their geometry is not checked,
    /// because an unresolved size reads as zero and the resolver has already said why.</param>
    /// <param name="documentPath">brief-em3d-64 — the <c>.c3d</c>'s own path, which a Step object's <c>File</c> is relative to;
    /// null skips the file checks (a document that has not been saved has no folder to look in).</param>
    public static IReadOnlyList<Diagnostic> Validate(C3dDocument doc, Func<string, bool>? isKnownMaterial = null,
                                                     IReadOnlySet<string>? unresolved = null, string? documentPath = null)
    {
        var found = new List<Diagnostic>();

        Names(doc, found);
        found.AddRange(C3dGroups.Findings(doc));

        foreach (var o in doc.Objects)
        {
            if (o is C3dPhantom phantom)
            {
                // brief-sar-3 R-sar3-2 — a phantom's materials are its layers'.
                foreach (var layer in phantom.Layers)
                {
                    string layerName = C3dPhantoms.LayerName(o.Name, layer.Name);
                    if (string.IsNullOrWhiteSpace(layer.Material)) { if (o.Model) found.Add(C3dDiagnostics.NoMaterial(layerName)); }
                    else if (isKnownMaterial is not null && !isKnownMaterial(layer.Material))
                        found.Add(C3dDiagnostics.UnknownMaterial(layerName, layer.Material));
                }
            }
            else if (o is C3dVoxelBody body)
            {
                // brief-sar-11 — a voxel body's materials are its rows'; an Air row has none.
                foreach (var row in body.Labels.Where(r => !r.Air && r.Material is { Length: > 0 }))
                    if (isKnownMaterial is not null && !isKnownMaterial(row.Material!))
                        found.Add(C3dDiagnostics.UnknownMaterial(C3dPhantoms.LayerName(o.Name, row.Name), row.Material!));
            }
            else if (o is not C3dPolyline)
            {
                // brief-em3d-50: a wire's omitted material is wBond's default metal, which the technology must still define.
                // brief-em3d-64 R-em3d64-3b: an operation's material is its Blank's (or Target's), which is checked there.
                string? material = o is C3dWire w ? C3dWires.MaterialOf(w) : EffectiveMaterial(o);
                // brief-em3d-101 R-em3d101-1d — no material is a warning only for what a solve sees: a not-modelled object is left
                // out of every run whatever it is made of.
                if (string.IsNullOrWhiteSpace(material)) { if (o.Model) found.Add(C3dDiagnostics.NoMaterial(o.Name)); }
                else if (isKnownMaterial is not null && !isKnownMaterial(material))
                    found.Add(C3dDiagnostics.UnknownMaterial(o.Name, material));
            }

            // brief-em3d-92 — a transparency outside the range, and one stated where nothing can carry it.
            if (o.Transparency is { } t && !C3dTransparency.InRange(t)) found.Add(C3dDiagnostics.TransparencyRange($"'{o.Name}'", t));
            if (o is C3dPolyline { Transparency: not null }) found.Add(C3dDiagnostics.TransparencyOnPolyline(o.Name));
            foreach (var operand in C3dOperands.SelfAndDescendants(o).Skip(1).Where(d => d.Transparency is not null))
                found.Add(C3dDiagnostics.TransparencyOnOperand(o.Name, operand.Name));
            // brief-em3d-105 — an appearance's own faults (MaterialValidation's rule), and one stated where nothing can carry it.
            Appearance(o.Appearance, $"'{o.Name}'", $"'{o.Name}'", isKnownMaterial, found);
            if (o is C3dPolyline { Appearance: not null }) found.Add(C3dDiagnostics.AppearanceOnPolyline(o.Name));
            foreach (var operand in C3dOperands.SelfAndDescendants(o).Skip(1).Where(d => d.Appearance is not null))
                found.Add(C3dDiagnostics.AppearanceOnOperand(o.Name, operand.Name));
            // brief-em3d-93 — Model where nothing reads it: a polyline is never modelled, an operand is its result's
            if (o is C3dPolyline { Model: false }) found.Add(C3dDiagnostics.ModelOnPolyline(o.Name));
            foreach (var operand in C3dOperands.SelfAndDescendants(o).Skip(1).Where(d => !d.Model))
                found.Add(C3dDiagnostics.ModelOnOperand(o.Name, operand.Name));

            // brief-em3d-101 R-em3d101-6 — an image that cannot be drawn, and Locked where nothing reads it.
            if (o is C3dSheet sheet) Images(sheet, documentPath, found);
            if (o is C3dVoxelBody or C3dMesh) ImportedFile(o, documentPath, found);
            if (o.FaceImages is { Count: > 0 }) FaceImages(o, documentPath, found);

            if (unresolved?.Contains(o.Name) == true) { Unread(o.Unread, $"'{o.Name}'", found); continue; }
            Geometry(o, o.Name, found);
            if (C3dOperands.IsKernel(o) || C3dSheetBooleans.IsSheetBoolean(o)) Operation(o, o.Name, isKnownMaterial, documentPath, found);

            Unread(o.Unread, $"'{o.Name}'", found);
        }

        foreach (var i in doc.Instances)
        {
            if (string.IsNullOrWhiteSpace(i.CellRef)) found.Add(C3dDiagnostics.InstanceNoCell(i.Name));
            if (i.View == C3dInstanceView.Hertzon && string.IsNullOrWhiteSpace(i.Design)) found.Add(C3dDiagnostics.InstanceNoDesign(i.Name));
            if (i.Array is { } a && unresolved?.Contains(i.Name) != true && (a.Counts.Count != 3 || a.Counts.Any(n => n < 1)))
                found.Add(C3dDiagnostics.ArrayCounts(i.Name));
            if (i.Transparency is { } t && !C3dTransparency.InRange(t)) found.Add(C3dDiagnostics.TransparencyRange($"The instance '{i.Name}'", t));
            Appearance(i.Appearance, $"The instance '{i.Name}'", $"the instance '{i.Name}'", isKnownMaterial, found);
            Unread(i.Unread, $"The instance '{i.Name}'", found);
        }

        // brief-sar-3 R-sar3-3 — a lumped element states at least one value; what its gap touches is the run's to resolve.
        foreach (var le in doc.LumpedElements)
        {
            if (le.R is null && le.L is null && le.C is null) found.Add(C3dDiagnostics.LumpedElementEmpty(le.Name));
            Unread(le.Unread, $"The lumped element '{le.Name}'", found);
        }

        // brief-hzn-12 — the plane waves: named, uniquely, at most one enabled, each one a wave that can be solved
        var waveNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in doc.PlaneWaves)
        {
            if (string.IsNullOrWhiteSpace(w.Name)) found.Add(C3dDiagnostics.PlaneWave("A plane wave has no name; each is named, as a port is."));
            else if (!waveNames.Add(w.Name)) found.Add(C3dDiagnostics.PlaneWave($"Two plane waves are named '{w.Name}'; a plane wave's name is unique in its 3D view."));
            if (C3dPlaneWaves.ToSetup(w, doc.DbuPerMicron) is null || C3dPlaneWaves.ToSetup(w, doc.DbuPerMicron)!.Resolve(out _) is null)
                found.Add(C3dDiagnostics.PlaneWave($"Plane wave '{w.Name}' cannot be solved: {C3dPlaneWaves.Why(w, doc.DbuPerMicron)}"));
        }
        if (doc.PlaneWaves.Count(w => w.Enabled) > 1)
            found.Add(C3dDiagnostics.PlaneWave($"{doc.PlaneWaves.Count(w => w.Enabled)} plane waves are enabled; at most one drives the 3D view's setups. " +
                                               "Disable all but one."));

        Look(doc.Look, documentPath, found);
        Unread(doc.Unread, "The document", found);
        return found;
    }

    /// <summary>brief-em3d-110 — the Look block's findings alone (<see cref="Look"/>'s rules): what <c>render --look-set</c> holds an
    /// overridden Look to, so a value the file could not hold is refused in the file's own words.</summary>
    public static IReadOnlyList<Diagnostic> LookFaults(C3dLook? look, string? documentPath)
    {
        var found = new List<Diagnostic>();
        Look(look, documentPath, found);
        return found;
    }

    /// <summary>brief-em3d-106 R-em3d106-5 — the Look block: Exposure and Intensity in range, a Background spelling the view reads,
    /// an Environment that is a studio or a <c>.hdr</c>, and (with a document path) a <c>.hdr</c> that reads — a WARNING only, since
    /// the view then lights the scene with Studio and says so (R-em3d106-4d).</summary>
    private static void Look(C3dLook? look, string? documentPath, List<Diagnostic> found)
    {
        if (look is null) return;
        if (look.Exposure is { } ev && !(double.IsFinite(ev) && ev >= C3dLook.ExposureMin && ev <= C3dLook.ExposureMax))
            found.Add(C3dDiagnostics.LookRange(nameof(C3dLook.Exposure), ev, C3dLook.ExposureMin, C3dLook.ExposureMax));
        if (look.Intensity is { } k && !(double.IsFinite(k) && k >= C3dLook.IntensityMin && k <= C3dLook.IntensityMax))
            found.Add(C3dDiagnostics.LookRange(nameof(C3dLook.Intensity), k, C3dLook.IntensityMin, C3dLook.IntensityMax));
        if (look.Rotation is { } r && !double.IsFinite(r))
            found.Add(C3dDiagnostics.LookRange(nameof(C3dLook.Rotation), r, double.NegativeInfinity, double.PositiveInfinity));
        if (!look.TryBackground(out _, out _, out _)) found.Add(C3dDiagnostics.LookBackground(look.Background ?? ""));
        var (_, path) = look.EnvironmentOf(out bool known);
        if (!known) found.Add(C3dDiagnostics.LookEnvironment(look.Environment ?? ""));
        else if (path is not null && documentPath is not null)
        {
            string file = C3dLook.ResolvePath(path, documentPath);
            if (!File.Exists(file)) found.Add(C3dDiagnostics.LookHdrUnreadable(path, "the file does not exist"));
            else if (!RadianceHdr.Probe(file, out string? why)) found.Add(C3dDiagnostics.LookHdrUnreadable(path, why ?? "it does not read"));
        }
        // brief-em3d-109 — the field style is one of three words, the field opacity a percentage
        look.FieldStyleOf(out bool styleKnown);
        if (!styleKnown) found.Add(C3dDiagnostics.LookFieldStyle(look.FieldStyle ?? ""));
        if (look.FieldOpacity is { } fo && !(double.IsFinite(fo) && fo >= C3dLook.FieldOpacityMin && fo <= C3dLook.FieldOpacityMax))
            found.Add(C3dDiagnostics.LookRange(nameof(C3dLook.FieldOpacity), fo, C3dLook.FieldOpacityMin, C3dLook.FieldOpacityMax));
        // brief-em3d-107 — Shadows, AmbientOcclusion and Ground are booleans: the reader refuses any other value, so nothing is left here
        // brief-em3d-108 — the picture camera, when stated, must be one a picture can be taken from
        if (look.Camera is { } camera)
        {
            foreach (string fault in camera.Faults()) found.Add(C3dDiagnostics.LookCamera(fault));
            Unread(camera.Unread, "The Look's Camera", found);
        }
        Unread(look.Unread, "The Look", found);
    }

    /// <summary>brief-em3d-105 — an object's or an instance's appearance: each fault MaterialValidation finds in a material's (one
    /// rule, two places), a Like the technology does not define (only when one resolved), and its unread keys.</summary>
    private static void Appearance(Layout.TechAppearance? a, string what, string of, Func<string, bool>? isKnownMaterial, List<Diagnostic> found)
    {
        if (a is null) return;
        foreach (string fault in Layout.MaterialValidation.AppearanceFaults(a)) found.Add(C3dDiagnostics.AppearanceInvalid(what, fault));
        if (a.Like is { } like && isKnownMaterial is not null && !isKnownMaterial(like)) found.Add(C3dDiagnostics.AppearanceLikeUnknown(what, like));
        Unread(a.Unread, $"The appearance of {of}", found);
    }

    /// <summary>brief-em3d-101 — a sheet's image findings: its file, resolved against the document that holds it (only when the
    /// document has a path: an unsaved one has no folder to resolve in), and a <c>Locked</c> with no image.</summary>
    private static void Images(C3dSheet s, string? documentPath, List<Diagnostic> found)
    {
        if (s.Image is null)
        {
            if (s.Locked) found.Add(C3dDiagnostics.LockedWithoutImage(s.Name));
            return;
        }
        if (s.Image.Unread is { } unread) Unread(unread, $"The image of '{s.Name}'", found);
        if (documentPath is null) return;
        string? file = C3dImages.Resolve(documentPath, s.Image.Path);
        if (C3dImages.Problem(file) is { } why) found.Add(C3dDiagnostics.ImageUnreadable(s.Name, file ?? s.Image.Path, why));
    }

    /// <summary>brief-em3d-101 Phase B — an object's face images: one per face, a transparency in range, a file that reads. Whether
    /// each face is still there is the elaboration's to say (<see cref="C3dImages.FaceImageFindings"/>).</summary>
    private static void FaceImages(C3dObject o, string? documentPath, List<Diagnostic> found)
    {
        foreach (var g in o.FaceImages!.GroupBy(f => f.Face, StringComparer.Ordinal).Where(g => g.Count() > 1))
            found.Add(C3dDiagnostics.FaceImageTwice(o.Name, g.Key));
        foreach (var fi in o.FaceImages!)
        {
            string label = $"'{o.Name}/{fi.Face}'";
            if (fi.Transparency is { } t && !C3dTransparency.InRange(t)) found.Add(C3dDiagnostics.TransparencyRange($"The image on {label}", t));
            if (fi.Width is <= 0 || fi.Height is <= 0) found.Add(C3dDiagnostics.FaceImageSize(o.Name, fi.Face));
            Unread(fi.Unread, $"The image on {label}", found);
            Unread(fi.Image.Unread, $"The image on {label}", found);
            if (documentPath is null) continue;
            string? file = C3dImages.Resolve(documentPath, fi.Image.Path);
            if (C3dImages.Problem(file) is { } why) found.Add(C3dDiagnostics.ImageUnreadable($"{o.Name}/{fi.Face}", file ?? fi.Image.Path, why));
        }
    }

    private static void Geometry(C3dObject o, string label, List<Diagnostic> found)
    {
        // An operand without a name of its own is reported by where it sits (lid.Blank).
        if (o.Name.Length == 0) o = Labelled(o, label);
        switch (o)
        {
            case C3dBox b:        Box(b, found);        break;
            case C3dPrism p:      Prism(p, found);      break;
            case C3dCylinder c:   Cylinder(c, found);   break;
            case C3dSphere sp:    Sphere(sp, found);    break;
            case C3dEllipsoid el: Ellipsoid(el, found); break;
            case C3dPhantom ph:   Phantom(ph, found);   break;
            case C3dVoxelBody vb: VoxelBody(vb, found); break;
            case C3dMesh me:      Mesh(me, found);      break;
            case C3dSheet s:      Sheet(s, found);      break;
            case C3dCylinderSheet cs: CylinderSheet(cs, found); break;
            case C3dPolyline l:   Polyline(l, found);   break;
            case C3dPolyhedron h: Polyhedron(h, found); break;
            case C3dWire w:       Wire(w, found);       break;
        }
    }

    /// <summary>A shallow stand-in carrying <paramref name="label"/> as its name, so a finding about an unnamed operand
    /// says where it is. The document is not touched.</summary>
    private static C3dObject Labelled(C3dObject o, string label)
    {
        var copy = C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(o));
        copy.Name = label;
        return copy;
    }

    /// <summary>brief-em3d-64 R-em3d64-3b — the material a top-level object elaborates with: an operation's is its Blank's
    /// (or Target's), all the way down.</summary>
    public static string? EffectiveMaterial(C3dObject o) => o is C3dOperation && C3dOperands.Inner(o) is { } inner ? EffectiveMaterial(inner) : o.Material;

    /// <summary>The role an operation's result takes: its Blank's (or Target's).</summary>
    public static CircuitRF.Engine.Em3d.Em3dRole? EffectiveRole(C3dObject o) => o is C3dOperation && C3dOperands.Inner(o) is { } inner ? EffectiveRole(inner) : o.Role;

    // ── operations (brief-em3d-64 R-em3d64-6a) ─────────────────────────────────────────────────

    /// <summary>What is wrong with one operation's own shape — a missing Blank, Tool or Target, an operand that is not a
    /// solid, a named Blank — at any depth: what the elaborator refuses an operation with instead of handing the kernel a
    /// tree it cannot mean.</summary>
    public static IReadOnlyList<Diagnostic> OperationFindings(C3dObject o, string label)
    {
        var found = new List<Diagnostic>();
        Operation(o, label, null, null, found);
        return [.. found.Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    private static void Operation(C3dObject o, string label, Func<string, bool>? isKnownMaterial, string? documentPath, List<Diagnostic> found)
    {
        switch (o)
        {
            case C3dBoolean b:
                if (b.Blank is null) found.Add(C3dDiagnostics.OperationShape(label, "Boolean", "has no Blank; a boolean acts on exactly one"));
                if (b.Tools.Count == 0) found.Add(C3dDiagnostics.OperationShape(label, "Boolean", "has no Tools; it needs at least one"));
                break;
            case C3dFillet f:
                if (f.Target is null) found.Add(C3dDiagnostics.OperationShape(label, "Fillet", "has no Target"));
                if (f.Edges.Count == 0) found.Add(C3dDiagnostics.OperationShape(label, "Fillet", "names no Edges to round"));
                if (f.Radius <= 0 && C3dBindings.GetExpr(f, nameof(C3dFillet.Radius), 0) is null)
                    found.Add(C3dDiagnostics.OperationShape(label, "Fillet", "has a Radius that is not positive"));
                break;
            case C3dChamfer c:
                if (c.Target is null) found.Add(C3dDiagnostics.OperationShape(label, "Chamfer", "has no Target"));
                if (c.Edges.Count == 0) found.Add(C3dDiagnostics.OperationShape(label, "Chamfer", "names no Edges to cut"));
                if (c.Distance <= 0 && C3dBindings.GetExpr(c, nameof(C3dChamfer.Distance), 0) is null)
                    found.Add(C3dDiagnostics.OperationShape(label, "Chamfer", "has a Distance that is not positive"));
                if (c.Distance2 < 0) found.Add(C3dDiagnostics.OperationShape(label, "Chamfer", "has a Distance2 that is negative"));
                break;
            case C3dStep st:
                Step(st, label, documentPath, found);
                return;
        }

        // R-em3d64-3b — a result's material and role are its Blank's: stated on the operation they would be a second answer.
        if (o is C3dOperation && (o.Material is not null || o.Role is not null))
            found.Add(C3dDiagnostics.OperationMaterial(label, C3dObject.KindOf(o), o is C3dBoolean ? "Blank" : "Target"));

        // brief-hzn-10 R-hzn10-1 — a Boolean of sheets: every operand a sheet, all in one plane, each placed within it.
        bool sheets = C3dSheetBooleans.IsSheetBoolean(o);
        if (sheets)
            foreach (var (operand, why) in C3dSheetBooleans.PlaneFaults((C3dBoolean)o, label, deep: false))
                found.Add(C3dDiagnostics.SheetBooleanPlane(label, operand, why));
        // A sheet beside a solid is refused once, naming both, rather than once per sheet as an operand of the wrong kind.
        var mixed = o is C3dBoolean mb ? C3dSheetBooleans.Mixed(mb, label) : null;
        if (mixed is { } mix) found.Add(C3dDiagnostics.SheetBooleanMixed(label, mix.Sheet, mix.Other, mix.OtherKind));

        foreach (var (prefix, child) in C3dOperands.Of(o))
        {
            string where = child.Name.Length > 0 ? child.Name : label + "." + prefix.TrimEnd('.');
            bool inner = ReferenceEquals(child, C3dOperands.Inner(o));
            // R-em3d64-1d — the wrapper carries the name; the object inside it has none.
            if (inner && child.Name.Length > 0) found.Add(C3dDiagnostics.OperandNamed(label, child.Name, prefix.TrimEnd('.')));
            // R-em3d64-1e — solids only, or (brief-hzn-10) sheets only.
            bool sheet = C3dSheetBooleans.IsSheet(child);
            if (!(sheets ? sheet : C3dOperands.IsSolid(child)))
            {
                if (!(mixed is not null && sheet)) found.Add(C3dDiagnostics.OperandKind(label, where, C3dObject.KindOf(child)));
                continue;
            }
            // A Tool's material matters when it is kept or the boolean is disabled; an unknown one is worth a warning.
            if (!inner && child.Material is { Length: > 0 } m && isKnownMaterial is not null && !isKnownMaterial(m))
                found.Add(C3dDiagnostics.UnknownMaterial(where, m));
            Geometry(child, where, found);
            if (C3dOperands.IsKernel(child) || C3dSheetBooleans.IsSheetBoolean(child)) Operation(child, where, isKnownMaterial, documentPath, found);
            Unread(child.Unread, $"'{where}'", found);
        }
    }

    /// <summary>A Step object's file: present, inside the cell's <c>3d/</c> folder, and the bytes its <c>face&lt;n&gt;</c>
    /// names were recorded against (brief 68 §6).</summary>
    private static void Step(C3dStep st, string label, string? documentPath, List<Diagnostic> found)
    {
        if (string.IsNullOrWhiteSpace(st.File)) { found.Add(C3dDiagnostics.StepShape(label, "names no File")); return; }
        if (string.IsNullOrWhiteSpace(st.Part)) found.Add(C3dDiagnostics.StepShape(label, "names no Part"));
        // brief-em3d-127 R-em3d127-2b: a solid past the part's last is the worker's refusal, reported where the build is.
        if (st.Solid is < 1) found.Add(C3dDiagnostics.StepShape(label, $"names Solid {st.Solid}; solids are numbered from 1"));
        if (documentPath is null) return;
        string folder = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        string file = Path.GetFullPath(Path.Combine(folder, st.File));
        if (!file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            found.Add(C3dDiagnostics.StepShape(label, $"names '{st.File}', which is outside this 3D view's folder; an imported file is copied into it"));
            return;
        }
        if (!File.Exists(file)) { found.Add(C3dDiagnostics.StepShape(label, $"names '{st.File}', which does not exist")); return; }
        if (StepHash(file) is var actual && !string.Equals(actual, st.Hash, StringComparison.OrdinalIgnoreCase))
            found.Add(C3dDiagnostics.StepHashMismatch(label, st.File));
    }

    /// <summary><c>sha256:&lt;hex&gt;</c> of a file's bytes — how a Step object records the file it was read from.</summary>
    public static string StepHash(string path)
    {
        using var s = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(s));
    }

    /// <summary>
    /// R-em3d64-5e — one ERROR per object that needs the kernel, when it is absent: the same sentence the editor's
    /// refusal on open uses, so a headless check fails a document the application would not open.
    /// </summary>
    public static IReadOnlyList<Diagnostic> KernelFindings(C3dDocument doc, Occ.GeometryKernelCapability capability)
    {
        if (capability.Available) return [];
        return [.. C3dKernelUse.Of(doc).Select(k => C3dDiagnostics.NeedsKernel(Occ.GeometryKernel.NeedsKernel(k.Label, capability)))];
    }

    // ── names ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Objects and instances share one namespace: both are addressed by name, and a port on
    /// <c>U1/…</c> must not be able to mean two things. Compared case-insensitively, for the same
    /// reason cell names are.</summary>
    private static void Names(C3dDocument doc, List<Diagnostic> found)
    {
        // brief-em3d-64 R-em3d64-1d — a Tool keeps its own name, unique across the whole document at every depth; the
        // object an operation wraps has none (Operation reports one that does).
        var named = doc.Objects.Select(o => (Kind: "object", o.Name))
                   .Concat(doc.Objects.SelectMany(Tools).Select(t => (Kind: "object", t.Name)))
                   .Concat(doc.Instances.Select(i => (Kind: "instance", i.Name)))
                   .Concat(doc.LumpedElements.Select(l => (Kind: "lumped element", l.Name)))
                   .ToList();

        foreach (var (kind, name) in named)
        {
            if (NameValidator.Validate(name) is { } why) found.Add(C3dDiagnostics.InvalidName(kind, name, why));
            else if (string.Equals(name, ReservedName, StringComparison.OrdinalIgnoreCase))
                found.Add(C3dDiagnostics.ReservedName(name));
        }

        foreach (var g in named.Where(n => n.Name.Length > 0)
                               .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1))
            found.Add(C3dDiagnostics.DuplicateName(g.Key, g.Count()));
    }

    /// <summary>Every Tool at any depth — the named operands.</summary>
    private static IEnumerable<C3dObject> Tools(C3dObject o)
    {
        foreach (var (_, child) in C3dOperands.Of(o))
        {
            if (!ReferenceEquals(child, C3dOperands.Inner(o))) yield return child;
            foreach (var t in Tools(child)) yield return t;
        }
    }

    private static void Unread(Dictionary<string, System.Text.Json.JsonElement>? keys, string owner, List<Diagnostic> found)
    {
        if (keys is null) return;
        foreach (var k in keys.Keys) found.Add(C3dDiagnostics.UnreadKey(owner, k));
    }

    // ── primitives ───────────────────────────────────────────────────────────────────────────

    private static void Box(C3dBox b, List<Diagnostic> found)
    {
        if (b.Size.X == 0 || b.Size.Y == 0 || b.Size.Z == 0)
            found.Add(C3dDiagnostics.ZeroVolume(b.Name, "box", "a size component is zero"));
    }

    private static void Prism(C3dPrism p, List<Diagnostic> found)
    {
        bool outlineOk = Loop(p.Name, "outline", p.Outline, found);
        for (int h = 0; h < p.Holes.Count; h++) Loop(p.Name, $"hole {h}", p.Holes[h], found);

        if (p.Height == 0)
            found.Add(C3dDiagnostics.ZeroVolume(p.Name, "prism", "its height is zero"));
        else if (outlineOk && TwiceArea(p.Outline) == 0)
            found.Add(C3dDiagnostics.ZeroVolume(p.Name, "prism", "its outline encloses no area"));
    }

    private static void Cylinder(C3dCylinder c, List<Diagnostic> found)
    {
        if (c.Radius <= 0)
            found.Add(C3dDiagnostics.ZeroVolume(c.Name, "cylinder", "its radius is not positive"));
        else if (c.Length == 0)
            found.Add(C3dDiagnostics.ZeroVolume(c.Name, "cylinder", "its length is zero"));
    }

    private static void Sphere(C3dSphere s, List<Diagnostic> found)
    {
        if (s.Radius <= 0)
            found.Add(C3dDiagnostics.ZeroVolume(s.Name, "sphere", "its radius is not positive"));
        if (s.FacetVertices != 0 && s.FacetVertices is < C3dFacetedSphere.MinVertices or > C3dFacetedSphere.MaxVertices)
            found.Add(C3dDiagnostics.FacetVertices(s.Name, s.FacetVertices));
    }

    private static void Ellipsoid(C3dEllipsoid e, List<Diagnostic> found)
    {
        if (e.SemiAxes.X <= 0 || e.SemiAxes.Y <= 0 || e.SemiAxes.Z <= 0)
            found.Add(C3dDiagnostics.ZeroVolume(e.Name, "ellipsoid", "a semi-axis is not positive"));
    }

    /// <summary>brief-sar-3 R-sar3-2 — a phantom's shape: positive dimensions, shells that leave every layer a volume, and one
    /// named layer per solid.</summary>
    /// <summary>brief-sar-11 — a voxel body's own shape: a file, a raw layout that holds together, a label table with one row per
    /// label and names a tissue can take, a crop that keeps something, a grid step that is not negative.</summary>
    private static void VoxelBody(C3dVoxelBody b, List<Diagnostic> found)
    {
        if (string.IsNullOrWhiteSpace(b.File)) found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, "names no File"));
        if (b.Raw is { } raw && C3dVoxelBodies.LayoutOf(raw, 1000)!.Fault() is { } fault)
            found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, "has a Raw layout that does not hold together: " + fault.TrimEnd('.')));
        if (b.Raw is null && b.File.Length > 0 && C3dVoxelBodies.FormatOf(b.File) == "raw")
            found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, $"names '{b.File}', a raw volume, and states no Raw layout (dimensions, type, voxel size)"));
        if (b.Labels.GroupBy(r => r.Label).FirstOrDefault(g => g.Count() > 1) is { } twice)
            found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, $"has two rows for label {twice.Key}"));
        foreach (var r in b.Labels.Where(r => !r.Air))
        {
            if (string.IsNullOrWhiteSpace(r.Name)) found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, $"has a row for label {r.Label} with no name"));
            else if (r.Name.Contains('/') || r.Name.Contains(',')) found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, $"has the tissue '{r.Name}', whose name holds '/' or ','"));
        }
        if (b.Labels.Where(r => !r.Air).GroupBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } same)
            found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, $"has two tissues named '{same.Key}'; each label's tissue is its own solid"));
        if (b.Labels.FirstOrDefault(r => r.Air && r.Material is { Length: > 0 }) is { } both)
            found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, $"marks label {both.Label} Air and gives it the material '{both.Material}'"));
        if (b.Crop is { } c && (c.I1 <= c.I0 || c.J1 <= c.J0 || c.K1 <= c.K0 || c.I0 < 0 || c.J0 < 0 || c.K0 < 0))
            found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, "has a Crop that keeps nothing (each upper index must exceed its lower, from 0)"));
        if (b.GridStep < 0) found.Add(C3dDiagnostics.VoxelBodyShape(b.Name, "has a GridStep that is negative (0 puts a line on every voxel boundary)"));
    }

    private static void Mesh(C3dMesh m, List<Diagnostic> found)
    {
        if (string.IsNullOrWhiteSpace(m.File)) found.Add(C3dDiagnostics.MeshShape(m.Name, "names no File"));
        if (C3dMeshes.UnitMetres(m.Unit) is null) found.Add(C3dDiagnostics.MeshShape(m.Name, $"states the unit '{m.Unit}', which is not m, cm, mm, um, in or mil"));
    }

    /// <summary>brief-sar-11 — an imported anatomy file: inside this 3D view's folder, present, and (a warning) unchanged since import.</summary>
    private static void ImportedFile(C3dObject o, string? documentPath, List<Diagnostic> found)
    {
        if (documentPath is null) return;
        var (file, hash, kind) = o switch
        {
            C3dVoxelBody b => (b.File, b.Hash, "voxel body"),
            C3dMesh m => (m.File, m.Hash, "mesh"),
            _ => ("", "", ""),
        };
        if (file.Length == 0) return;
        Diagnostic Shape(string why) => o is C3dMesh ? C3dDiagnostics.MeshShape(o.Name, why) : C3dDiagnostics.VoxelBodyShape(o.Name, why);
        string folder = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        string full = Path.GetFullPath(Path.Combine(folder, file));
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { found.Add(Shape($"names '{file}', which is outside this 3D view's folder; an imported file is copied into it")); return; }
        if (!File.Exists(full)) { found.Add(Shape($"names '{file}', which does not exist")); return; }
        if (hash.Length > 0 && !string.Equals(StepHash(full), hash, StringComparison.OrdinalIgnoreCase))
            found.Add(C3dDiagnostics.ImportedFileChanged(kind, o.Name, file));
    }

    private static void Phantom(C3dPhantom p, List<Diagnostic> found)
    {
        if (C3dPhantoms.ShapeFault(p) is { } why) found.Add(C3dDiagnostics.PhantomShape(p.Name, why));
        foreach (var l in p.Layers) Unread(l.Unread, $"The layer '{l.Name}' of '{p.Name}'", found);
    }

    private static void Sheet(C3dSheet s, List<Diagnostic> found)
    {
        bool hasOutline = s.Outline.Count > 0;
        // brief-hzn-13 R-hzn13-2 — an Ellipse is the third shape: exactly one of the three.
        int shapes = (s.Rect is null ? 0 : 1) + (hasOutline ? 1 : 0) + (s.Ellipse is null ? 0 : 1);
        if (shapes == 0) { found.Add(C3dDiagnostics.SheetShape(s.Name, "has none of a Rect, an Outline and an Ellipse")); return; }
        if (shapes > 1)
        {
            string both = string.Join(" and ", new[] { s.Rect is null ? null : "a Rect", hasOutline ? "an Outline" : null, s.Ellipse is null ? null : "an Ellipse" }.Where(x => x is not null));
            found.Add(C3dDiagnostics.SheetShape(s.Name, $"has {both}; it must have one"));
            return;
        }
        if (s.Ellipse is { } e)
        {
            if (C3dEllipses.Fault(e) is { } why) found.Add(C3dDiagnostics.SheetShape(s.Name, why));
            if (s.Holes.Count > 0) found.Add(C3dDiagnostics.SheetShape(s.Name, "is an ellipse with Holes; holes belong to an Outline"));
            return;
        }

        if (s.Rect is { } r)
        {
            if (r.Size.U == 0 || r.Size.V == 0) found.Add(C3dDiagnostics.SheetShape(s.Name, "is a rectangle with no area"));
            return;
        }

        Loop(s.Name, "outline", s.Outline, found);
        for (int h = 0; h < s.Holes.Count; h++) Loop(s.Name, $"hole {h}", s.Holes[h], found);
    }

    /// <summary>brief-sar-8b R-sar8b-2 — a sheet on a cylinder: a radius, a length and a sweep that leave it an area.</summary>
    private static void CylinderSheet(C3dCylinderSheet s, List<Diagnostic> found)
    {
        if (s.Radius <= 0) found.Add(C3dDiagnostics.SheetShape(s.Name, "is on a cylinder whose radius is not positive"));
        else if (s.Length == 0) found.Add(C3dDiagnostics.SheetShape(s.Name, "is on a cylinder and has no length along its axis"));
        else if (!(Math.Abs(s.SweepDeg) > 0) || Math.Abs(s.SweepDeg) > 360 || !double.IsFinite(s.StartDeg))
            found.Add(C3dDiagnostics.SheetShape(s.Name, "is on a cylinder and sweeps no azimuth, or more than a full turn"));
    }

    private static void Polyline(C3dPolyline l, List<Diagnostic> found)
    {
        if (l.VertexCount < 2) found.Add(C3dDiagnostics.PolylineTooShort(l.Name, l.VertexCount));
    }

    private static void Wire(C3dWire w, List<Diagnostic> found)
    {
        if (!w.Placement.IsDefault) found.Add(C3dDiagnostics.WirePlacement(w.Name));
        if (w.Points.Distinct().Count() < 2)
            found.Add(C3dDiagnostics.WireShape(w.Name, $"has {w.Points.Distinct().Count()} distinct point(s); it needs at least two"));
        if (w.DiameterUm is { } d && !(d > 0))
            found.Add(C3dDiagnostics.WireShape(w.Name, "has a diameter that is not positive"));
        foreach (var (end, which) in new[] { (w.Start, "start"), (w.End, "end") })
            if (end.FootLengthUm is { } f && !(f > 0))
                found.Add(C3dDiagnostics.WireShape(w.Name, $"has a foot length at its {which} that is not positive"));
        if (w.Array is { Count: < 1 })
            found.Add(C3dDiagnostics.WireShape(w.Name, "is an array of fewer than one wire; an array's count is at least 1"));
        if (w.Array is { Count: > 1, Pitch: var p } && p == default)
            found.Add(C3dDiagnostics.WireShape(w.Name, "is an array with no pitch: every copy would lie on the first"));
        if (w.Role is { } role && role != CircuitRF.Engine.Em3d.Em3dRole.Conductor)
            found.Add(C3dDiagnostics.WireShape(w.Name, $"has the role {role}; a wire is a conductor"));
    }

    /// <summary>A loop needs three DISTINCT points; returns whether it has them.</summary>
    private static bool Loop(string name, string which, List<C3dPoint2> points, List<Diagnostic> found)
    {
        int distinct = points.Distinct().Count();
        if (distinct >= 3) return true;
        found.Add(C3dDiagnostics.TooFewPoints(name, which, distinct));
        return false;
    }

    /// <summary>Twice the signed area, exactly — Int128, because two DBU coordinates of a large board
    /// multiplied overflow a long and a double would make "exactly zero" a matter of rounding.</summary>
    private static Int128 TwiceArea(List<C3dPoint2> ring)
    {
        Int128 sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += (Int128)a.U * b.V - (Int128)b.U * a.V;
        }
        return sum;
    }

    // ── polyhedron ───────────────────────────────────────────────────────────────────────────

    private static void Polyhedron(C3dPolyhedron h, List<Diagnostic> found)
    {
        int n = h.Vertices.Count;
        bool indicesOk = true;

        var faceNames = new HashSet<string>(StringComparer.Ordinal);
        var reported  = new HashSet<string>(StringComparer.Ordinal);
        for (int f = 0; f < h.Faces.Count; f++)
        {
            var face = h.Faces[f];
            if (string.IsNullOrWhiteSpace(face.Name)) found.Add(C3dDiagnostics.FaceUnnamed(h.Name, f));
            else if (!faceNames.Add(face.Name) && reported.Add(face.Name))
                found.Add(C3dDiagnostics.FaceNameDuplicate(h.Name, face.Name));

            foreach (var loop in Loops(face))
            {
                if (loop.Count < 3) { found.Add(C3dDiagnostics.FaceTooSmall(h.Name, FaceLabel(face, f))); indicesOk = false; }
                foreach (int i in loop)
                    if (i < 0 || i >= n)
                    {
                        found.Add(C3dDiagnostics.FaceIndex(h.Name, FaceLabel(face, f), i, n));
                        indicesOk = false;
                    }
            }
        }

        // Closure and planarity read vertices through the indices; with a bad index they would only
        // report the same defect a second time, in a worse sentence.
        if (!indicesOk) return;

        Closure(h, found);
        for (int f = 0; f < h.Faces.Count; f++) Planarity(h, h.Faces[f], f, found);
    }

    private static IEnumerable<List<int>> Loops(C3dFace face)
    {
        yield return face.Outer;
        foreach (var hole in face.Holes) yield return hole;
    }

    private static string FaceLabel(C3dFace face, int index)
        => string.IsNullOrWhiteSpace(face.Name) ? index.ToString(CultureInfo.InvariantCulture) : face.Name;

    /// <summary>
    /// Every edge used by exactly two faces, in opposite directions: each directed edge a→b appears
    /// exactly once, and so does b→a. That is a closed, consistently oriented surface — the
    /// orientation half matters because a face wound the wrong way points its normal INTO the solid.
    /// </summary>
    private static void Closure(C3dPolyhedron h, List<Diagnostic> found)
    {
        var directed = new Dictionary<(int, int), int>();
        foreach (var face in h.Faces)
            foreach (var loop in Loops(face))
                for (int k = 0; k < loop.Count; k++)
                {
                    var e = (loop[k], loop[(k + 1) % loop.Count]);
                    directed[e] = directed.GetValueOrDefault(e) + 1;
                }

        var bad = new SortedSet<(int, int)>();
        foreach (var ((a, b), count) in directed)
        {
            if (count != 1 || directed.GetValueOrDefault((b, a)) != 1)
                bad.Add(a < b ? (a, b) : (b, a));
        }
        if (bad.Count == 0) return;

        const int Named = 8;
        var list = bad.Take(Named).Select(e => $"{e.Item1}–{e.Item2} ({Point(h.Vertices[e.Item1])} to {Point(h.Vertices[e.Item2])})");
        string edges = string.Join("; ", list) + (bad.Count > Named ? $"; and {bad.Count - Named} more" : "");
        found.Add(C3dDiagnostics.NotClosed(h.Name, edges));

        static string Point(C3dPoint3 p) => FormattableString.Invariant($"[{p.X}, {p.Y}, {p.Z}]");
    }

    /// <summary>
    /// Newell's normal, then the largest distance of any vertex (holes included) from the plane through
    /// the loop's centroid. Newell's rather than a cross product of two edges because it is the
    /// least-squares plane for a non-planar loop, so the deviation it reports is the honest one rather
    /// than one that depends on which vertices happened to come first.
    /// </summary>
    private static void Planarity(C3dPolyhedron h, C3dFace face, int index, List<Diagnostic> found)
    {
        var v = h.Vertices;
        double nx = 0, ny = 0, nz = 0, cx = 0, cy = 0, cz = 0;
        var outer = face.Outer;
        for (int k = 0; k < outer.Count; k++)
        {
            var a = v[outer[k]];
            var b = v[outer[(k + 1) % outer.Count]];
            nx += (double)(a.Y - b.Y) * (a.Z + b.Z);
            ny += (double)(a.Z - b.Z) * (a.X + b.X);
            nz += (double)(a.X - b.X) * (a.Y + b.Y);
            cx += a.X; cy += a.Y; cz += a.Z;
        }
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len == 0) { found.Add(C3dDiagnostics.FaceNoArea(h.Name, FaceLabel(face, index))); return; }

        nx /= len; ny /= len; nz /= len;
        cx /= outer.Count; cy /= outer.Count; cz /= outer.Count;

        double worst = 0;
        foreach (var loop in Loops(face))
            foreach (int i in loop)
                worst = Math.Max(worst, Math.Abs(nx * (v[i].X - cx) + ny * (v[i].Y - cy) + nz * (v[i].Z - cz)));

        if (worst > PlanarityToleranceDbu)
            found.Add(C3dDiagnostics.NotPlanar(h.Name, FaceLabel(face, index), Math.Round(worst, 1)));
    }
}
