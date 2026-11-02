// brief-em3d-28 R-em3d28-2 — the scene from the problem, through the one tessellation (R-em3d5-1).
//
// Everything is tessellated by Em3dTessellation, so the viewer's surfaces are the section renderer's,
// the CSXCAD writer's and the size estimate's: three consumers already agree on where a wire's surface
// is, and the viewer does not make a fourth answer.
//
// COLOURS (R-em3d28-2c). A conductor is painted with its drawing layer's colour from the technology,
// resolved by Em3dSectionRenderer.ObjectColours — the function brief 5's pictures use, so a via is the
// same colour in the 2D section, the layout editor and here. Dielectrics, air and the air-box faces
// take the fixed palette below, one set for the light variant and one for the dark, which is how the
// 2D editors follow the application theme. Ports take the theme's pin colour, as in the section view.
//
// FACES AND EDGES (brief-em3d-43 R-em3d43-3a / -5). Every vertex carries the face it lies on, so a vertex
// the tessellation shares between two faces (a box's corner is on three) is emitted once PER FACE: flat
// interpolation takes the face from one vertex, and a shared one would give a triangle its neighbour's
// face. Shading is flat already (the fragment shader's derivative normal), so nothing looks different. A
// sheet is one face, 0. Each solid and sheet also gets its FEATURE EDGES — where two of its faces meet,
// or a sheet's boundary — in the line buffer, drawn only when selected (the Object-mode outline).
//
// EDIT LOCALITY (R-em3d43-1b). With a Scene3DTessellationCache, a solid whose primitive is the same as
// last build's is not tessellated again: the elaborator hands back its cached primitive for an unchanged
// object, so an edit to one object of a thousand tessellates one. An editor also fixes the ORIGIN, so
// an edit that does not move the bounds' centre leaves every other object's vertex bytes identical — which
// is what lets the session upload only the changed ranges (Scene3DPatch).
//
// THE PALETTE, documented because it is a user-visible choice: dielectrics cycle through six hues in
// problem order (green, blue, amber, violet, teal, rose) at 35 % opacity, so a stack of two substrates
// is two colours; air is a pale blue at 8 %; an air-box face is grey for PEC, blue for absorbing,
// orange for PMC and violet for symmetry.

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Theming;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Render.Scene3D;

/// <summary>brief-em3d-43 — what an editor's build adds to the viewer's.</summary>
/// <param name="FaceNames">An object's face names by its name (the elaboration's provenance), or null.</param>
/// <param name="Cache">Tessellations kept from the last build; null tessellates everything.</param>
/// <param name="DrawAirBox">False draws no air-box faces or edges — a document with no setup has none.</param>
/// <param name="Origin">The scene-local origin to use, world metres; null takes the air box's centre.</param>
/// <param name="FeatureShare">brief-em3d-44 R-em3d44-3b — an object's share key and translation when it is one
/// element of an instance (objects under one key share ONE feature table), or null for its own table.</param>
/// <param name="Instancing">brief-em3d-48 R-em3d48-3a — where an object stands in an array or a repeated placement, or
/// null: an element after the first is not tessellated — it draws the first's triangles under its own offset.</param>
/// <param name="Context">brief-em3d-48 R-em3d48-4a — true for the parent drawn around a pushed-in child: dimmed, and
/// never hovered or selected (the snap still reaches it).</param>
/// <param name="EditorBoundaries">brief-em3d-49 R-em3d49-3a — the air box as the 3D editor draws its active setup's: an
/// absorbing face untinted, a PEC face metal grey, a PMC face its own hue, a symmetry face hatched, and every face
/// pickable last (<see cref="Scene3DObject.PickLast"/>). False — the read-only viewer — keeps brief 28's tints.</param>
/// <param name="FaceTints">brief-em3d-49 R-em3d49-4d — the face boundaries, each drawn tinted just off its face.</param>
/// <param name="Wireframe">3D editor bugs round 1 — true for an object with no material: drawn as a wireframe
/// (<see cref="Scene3DObject.Wireframe"/>), never taken for the outermost dielectric.</param>
/// <param name="Ghost">brief-em3d-66 R-em3d66-2e — how an object draws while a boolean is previewed or entered: a ghost is
/// translucent and never hovered or selected; a Tool of a subtraction is a ghost in the overlay's red.</param>
/// <param name="OwnFrame">brief-em3d-67 R-em3d67-2b — an object's map from world metres into its own frame (before its
/// placement), where the runs one pair of faces bounds are numbered; null (or null for a name) is the identity.</param>
/// <param name="Transparency">brief-em3d-92 — an object's stated transparency and the opacity its instances multiply onto it
/// (<see cref="Scene3DTransparency"/>), or null for its kind's look. It replaces the kind's alpha (a conductor's too, which is
/// otherwise opaque); the context, ghost and wireframe looks keep their own and win while in force.</param>
/// <param name="HideOutermostDielectric">True (a 3D .cem's viewer) opens with the outermost dielectric hidden: there the
/// substrate is a slab the size of the air box — solver geometry — and the traces are what a user came to look at.
/// False shows every dielectric: the 3D editor, where a board placed from a layout is bounded to the board (hiding its
/// thickest dielectric drew copper floating in air with nothing between the layers), and a PLANAR .cem's preview, whose
/// slabs are drawn to the board outline or the copper hull (SlabLateralBound) rather than across the air box.</param>
/// <param name="Images">brief-em3d-101 — a sheet's image (C3dElaboration.Images), or null: the sheet is drawn with it instead of its
/// colour, never as a wireframe and never as an array element (it is drawn as an ordinary object, which is always correct).</param>
/// <param name="Appearance">brief-em3d-105 — what the document says of an object's look over its material's (AppearanceOverride.Of
/// an elaboration's provenance), or null.</param>
/// <param name="FaceImages">brief-em3d-101 Phase B — images mapped onto faces (C3dElaboration.FaceImages): each drawn over its face as
/// a record of its own (a face boundary's tint's kind of object, named C3dImages.FacePrefix + face), from the face's own triangles.
/// An object carrying one is drawn as an ordinary object, never an array element.</param>
public sealed record Scene3DBuildOptions(
    Func<string, IReadOnlyList<string>?>? FaceNames = null,
    Scene3DTessellationCache? Cache = null,
    bool DrawAirBox = true,
    (double X, double Y, double Z)? Origin = null,
    Func<string, Scene3DFeatureShare?>? FeatureShare = null,
    Func<string, Scene3DInstancing?>? Instancing = null,
    Func<string, bool>? Context = null,
    bool EditorBoundaries = false,
    IReadOnlyList<Scene3DFaceTint>? FaceTints = null,
    Func<string, bool>? Wireframe = null,
    Func<string, Scene3DGhost>? Ghost = null,
    Func<string, Func<Point3, Point3>?>? OwnFrame = null,
    bool HideOutermostDielectric = true,
    Func<string, Scene3DTransparency?>? Transparency = null,
    Func<string, CircuitRF.Design.ThreeD.C3dPlacedImage?>? Images = null,
    IReadOnlyList<CircuitRF.Design.ThreeD.C3dFaceImageUse>? FaceImages = null,
    Func<string, AppearanceOverride?>? Appearance = null)
{
    /// <summary>brief-em3d-104 gate 11 — false keeps every vertex whole (no crease duplicates): the "before" the default view's
    /// pixels are compared against, built in the same test rather than read from a stored picture.</summary>
    internal bool SplitShadingCreases { get; init; } = true;
}

/// <summary>brief-em3d-92 — how see-through one object is drawn: its own percentage (null, its kind's default) and the opacity the
/// instances it sits in multiply onto it (1 for none). C3dTransparency.Alpha turns the two and the kind's alpha into one.</summary>
public readonly record struct Scene3DTransparency(int? Percent, double Opacity = 1)
{
    /// <summary>The alpha an object whose kind draws it at <paramref name="kindAlpha"/> is drawn at.</summary>
    public byte Alpha(byte kindAlpha) => C3dTransparency.Alpha(Percent, Opacity, kindAlpha);

    /// <summary>The option an elaboration's provenance answers: each object's own percentage and its instances' opacity; null for
    /// an object that states neither, whose kind's look stands.</summary>
    public static Func<string, Scene3DTransparency?> Of(IReadOnlyDictionary<string, C3dProvenance> provenance)
        => name => provenance.TryGetValue(name, out var p) && States(p) ? new Scene3DTransparency(p.Transparency, p.Opacity) : null;

    /// <summary>The same, as a map by elaborated name: what a section or a drawing is painted with.</summary>
    public static IReadOnlyDictionary<string, Scene3DTransparency> MapOf(IReadOnlyDictionary<string, C3dProvenance> provenance)
        => provenance.Where(kv => States(kv.Value))
                     .ToDictionary(kv => kv.Key, kv => new Scene3DTransparency(kv.Value.Transparency, kv.Value.Opacity), StringComparer.Ordinal);

    private static bool States(C3dProvenance p) => p.Transparency is not null || p.Opacity < 1;

    /// <summary>The transparency, percent, a <paramref name="role"/>'s kind is drawn at when an object states none — what the
    /// Inspector shows greyed as "(default)": a dielectric's <see cref="Scene3DBuilder.DielectricAlpha"/>, air's
    /// <see cref="Scene3DBuilder.AirAlpha"/>, a conductor opaque.</summary>
    public static int DefaultPercent(Em3dRole role) => role switch
    {
        Em3dRole.Dielectric => (int)Math.Round(100 * (1 - Scene3DBuilder.DielectricAlpha / 255.0), MidpointRounding.AwayFromZero),
        Em3dRole.Air        => (int)Math.Round(100 * (1 - Scene3DBuilder.AirAlpha / 255.0), MidpointRounding.AwayFromZero),
        _                   => 0,
    };

    /// <summary><paramref name="rgba"/> with its alpha replaced, and whether the result is translucent (not fully opaque).</summary>
    public (uint Rgba, bool Translucent) Apply(uint rgba)
    {
        byte a = Alpha((byte)(rgba >> 24));
        return ((rgba & 0x00FF_FFFFu) | ((uint)a << 24), a < 255);
    }
}

/// <summary>brief-em3d-66 — an object's draw state while a boolean is previewed or entered.</summary>
public enum Scene3DGhost
{
    /// <summary>Drawn as it always is.</summary>
    None,
    /// <summary>Translucent, and never hovered or selected: an operand under a preview, or a result whose operands are being edited.</summary>
    Ghost,
    /// <summary>A ghost in the overlay's red: what a subtraction takes away.</summary>
    Taken,
    /// <summary>brief-em3d-67 R-em3d67-5c — translucent like a ghost but still hovered and selected: a fillet's target
    /// while its preview is shown, whose edges the panel's list is still edited on.</summary>
    Pickable,
    /// <summary>brief-em3d-67 — drawn as it is, but never hovered or selected: the fillet's previewed result, seen through
    /// its translucent target.</summary>
    Inert,
}

/// <summary>brief-em3d-49 R-em3d49-4d — one face boundary to draw: its name (<c>object/face</c>), its kind and its pieces.</summary>
/// <para>brief-em3d-75 — <paramref name="Colour"/> overrides the kind's: a thermal boundary is tinted by ITS kind (blue for a
/// fixed temperature, green for convection), which no EM kind names.</para>
public sealed record Scene3DFaceTint(string Name, Em3dFaceBoundaryKind Kind, IReadOnlyList<Em3dFacePolygon> Pieces,
                                     (byte R, byte G, byte B)? Colour = null);

/// <summary>
/// brief-em3d-48 R-em3d48-3a — an object's place in a RUN: the objects one placement of one child document puts in the
/// problem under one rotation, element after element, each element's objects in the child's own order. <see cref="Run"/>
/// is compared with Equals; <see cref="Element"/> numbers the elements; (<see cref="Tx"/>, <see cref="Ty"/>,
/// <see cref="Tz"/>) is the element's translation (world metres) and <see cref="LocalName"/> the object's name inside
/// the element — which is what pairs an element's k-th object with the prototype's.
/// </summary>
public readonly record struct Scene3DInstancing(object Run, int Element, double Tx, double Ty, double Tz, string LocalName);

/// <summary>
/// brief-em3d-43 R-em3d43-1b — tessellations kept between builds, keyed by the primitive (a sheet by its
/// outline, holes, height and frame). A key a build did not use is dropped at its end, so the cache holds
/// one scene's worth, never a history. Not thread-safe: one per document, used by one build at a time.
/// </summary>
public sealed class Scene3DTessellationCache
{
    private Dictionary<object, Em3dTriangleMesh> _kept = [];
    private Dictionary<object, Em3dTriangleMesh> _used = [];
    private long _misses;

    /// <summary>Tessellations this cache had to make — one per primitive it had not kept.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    internal Em3dTriangleMesh Get(object key, Func<Em3dTriangleMesh> make, ref long misses)
    {
        if (_used.TryGetValue(key, out var m)) return m;
        if (!_kept.TryGetValue(key, out m))
        {
            m = make();
            Interlocked.Increment(ref misses);
            Interlocked.Increment(ref _misses);
        }
        _used[key] = m;
        return m;
    }

    internal void EndBuild()
    {
        (_kept, _used) = (_used, _kept);
        _used.Clear();
    }

    /// <summary>How many tessellations are kept.</summary>
    public int Count => _kept.Count;
}

/// <summary>Builds a <see cref="Scene3DModel"/> from an <see cref="Em3dProblem"/>.</summary>
public static class Scene3DBuilder
{
    /// <summary>Opacity of a dielectric solid.</summary>
    public const byte DielectricAlpha = 90;
    /// <summary>Opacity of the air.</summary>
    public const byte AirAlpha = 20;
    /// <summary>Opacity of an air-box face.</summary>
    public const byte FaceAlpha = 40;
    /// <summary>Opacity of a port's sheet.</summary>
    public const byte PortAlpha = 170;
    /// <summary>A port's arrow is this fraction of the port's longer side.</summary>
    public const float ArrowFraction = 0.8f;
    /// <summary>3D editor bugs round 9 — the most checkerboard cells along either side of a lumped port.</summary>
    public const int CheckerCellsMax = 64;
    /// <summary>Segments around a coaxial port's annulus.</summary>
    public const int AnnulusSegments = 32;
    /// <summary>brief-em3d-105 R-em3d105-4 — the most distinct appearances one scene holds (overview D16: one uniform table, which
    /// Vulkan's guaranteed 16 KB of uniform range fits).</summary>
    public const int AppearanceSlots = 256;

    /// <summary>brief-em3d-105 R-em3d105-3b — the appearance role a scene kind is resolved for; null for air, a port and a
    /// boundary, which have none.</summary>
    public static AppearanceRole? AppearanceRoleOf(Scene3DKind kind) => kind switch
    {
        Scene3DKind.Conductor  => AppearanceRole.Conductor,
        Scene3DKind.Dielectric => AppearanceRole.Dielectric,
        Scene3DKind.Via        => AppearanceRole.Via,
        Scene3DKind.Wire       => AppearanceRole.Wire,
        Scene3DKind.Body       => AppearanceRole.Body,
        Scene3DKind.Sheet      => AppearanceRole.Sheet,
        _                      => null,
    };

    /// <summary>
    /// brief-em3d-105 R-em3d105-7c — one object's resolved appearance, with each field's provenance, exactly as
    /// <see cref="Build"/> resolves it (its kind, its palette colour, its material): what <c>explain --object</c> prints. Null for
    /// a name the problem has no solid or sheet of, and for air.
    /// </summary>
    public static ResolvedAppearance? AppearanceOf(Em3dProblem problem, string name, IReadOnlyDictionary<string, Em3dObjectOrigin>? origins,
                                                   Technology? tech, AppearanceOverride? appearance, ColorTheme? theme = null,
                                                   ColorVariant variant = ColorVariant.Light)
    {
        origins ??= new Dictionary<string, Em3dObjectOrigin>();
        theme ??= ColorTheme.BuiltIn;
        var conductorColours = Em3dSectionRenderer.ObjectColours(problem, origins, tech, theme, variant);
        bool dark = variant == ColorVariant.Dark;
        if (problem.Solids.FirstOrDefault(s => s.Name == name) is { } solid)
        {
            var kind = KindOf(solid, origins);
            var palette = solid.Role == Em3dRole.Dielectric ? DielectricPalette(problem, solid.Material, dark, null) : ConductorPalette(conductorColours, name);
            return AppearanceRequestFor(kind, solid.Role, solid.Material, palette, tech, appearance) is { } r ? AppearanceResolver.Resolve(r) : null;
        }
        if (problem.Sheets.FirstOrDefault(s => s.Name == name) is { } sheet)
            return AppearanceResolver.Resolve(AppearanceRequestFor(Scene3DKind.Sheet, Em3dRole.Conductor, sheet.Material,
                                                                   ConductorPalette(conductorColours, name), tech, appearance)!.Value);
        return null;
    }

    private static AppearanceRequest? AppearanceRequestFor(Scene3DKind kind, Em3dRole role, string material, (byte R, byte G, byte B) palette,
                                                          Technology? tech, AppearanceOverride? appearance)
        => AppearanceRoleOf(kind) is { } r && role != Em3dRole.Air ? new AppearanceRequest(tech, material, r, role, palette, appearance) : null;

    /// <summary>A conductor's or a sheet's colour from the scene's palette — before its material's own Color.</summary>
    private static (byte R, byte G, byte B) ConductorPalette(IReadOnlyDictionary<string, SkiaSharp.SKColor> colours, string name)
        => colours.TryGetValue(name, out var c) ? (c.Red, c.Green, c.Blue) : ((byte)150, (byte)150, (byte)155);

    /// <summary>A dielectric's colour from the scene's palette — before its material's own Color: the palette's entry for the
    /// material's place among the drawn dielectrics.</summary>
    private static (byte R, byte G, byte B) DielectricPalette(Em3dProblem problem, string material, bool dark, Func<string, bool>? wire)
    {
        var dielectrics = problem.Solids.Where(s => s.Role == Em3dRole.Dielectric && wire?.Invoke(s.Name) != true)
                                 .Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();
        var pal = dark ? DielectricDark : DielectricLight;
        return pal[Math.Max(0, dielectrics.IndexOf(material)) % pal.Length];
    }

    /// <summary>brief-em3d-53 M6 — a material's own display colour (<c>#rrggbb</c>), when its technology states one;
    /// null keeps the scene's palette. A qualified or stackup-derived name finds nothing, which is the palette.</summary>
    /// brief-em3d-105 R-em3d105-1b — read by MaterialValidation.ParseColour, the one #rrggbb reader.
    private static (byte R, byte G, byte B)? MaterialColour(Technology? tech, string material)
        => MaterialValidation.ParseColour(tech?.FindMaterial(material)?.Color);

    private static long _tessellations;

    /// <summary>How many solids and sheets have been tessellated in this process — gate 5's counter
    /// (a hover must not move it).</summary>
    public static long Tessellations => Interlocked.Read(ref _tessellations);

    private static readonly (byte R, byte G, byte B)[] DielectricLight =
        [(92, 158, 92), (89, 140, 191), (191, 153, 77), (153, 115, 179), (77, 166, 166), (179, 102, 102)];
    private static readonly (byte R, byte G, byte B)[] DielectricDark =
        [(120, 190, 120), (120, 170, 225), (225, 185, 105), (185, 150, 215), (105, 200, 200), (215, 135, 135)];

    /// <summary>The scene for <paramref name="problem"/>. <paramref name="origins"/> and
    /// <paramref name="tech"/> colour the conductors; either may be absent.</summary>
    public static Scene3DModel Build(Em3dProblem problem, long generation,
                                     IReadOnlyDictionary<string, Em3dObjectOrigin>? origins = null,
                                     Technology? tech = null, ColorTheme? theme = null,
                                     ColorVariant variant = ColorVariant.Light,
                                     IReadOnlyList<string>? notes = null, Scene3DBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        origins ??= new Dictionary<string, Em3dObjectOrigin>();
        theme ??= ColorTheme.BuiltIn;
        options ??= new Scene3DBuildOptions();
        bool dark = variant == ColorVariant.Dark;

        var box = problem.Boundary;
        var origin = options.Origin is { } fixedOrigin
            ? (fixedOrigin.X, fixedOrigin.Y, fixedOrigin.Z)
            : ((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, (box.Min.Z + box.Max.Z) / 2);
        var cache = options.Cache;
        Em3dTriangleMesh Tessellate(object key, Func<Em3dTriangleMesh> make)
        {
            if (cache is not null) return cache.Get(key, make, ref _tessellations);
            Interlocked.Increment(ref _tessellations);
            return make();
        }
        IReadOnlyList<string> FacesOf(string name) => options.FaceNames?.Invoke(name) ?? [];
        // brief-em3d-44 R-em3d44-3 — each solid's and sheet's snap features, one table per shared key.
        var shared = new Dictionary<object, (Scene3DFeatureTable Table, double X, double Y, double Z)>();
        // brief-em3d-67 — and its named edges: the kernel's for a kernel solid, else runs of its own segments.
        Scene3DFeatureRef Features(string name, Em3dTriangleMesh mesh, bool sheet, IReadOnlyList<Em3dShapeEdge>? kernel = null,
                                   IReadOnlyList<string>? sheetNames = null)
        {
            var source = new Scene3DEdgeSource(sheetNames ?? FacesOf(name), kernel, options.OwnFrame?.Invoke(name));
            if (options.FeatureShare?.Invoke(name) is not { } share) return new(Scene3DFeatureTable.Of(mesh, sheet, source), 0, 0, 0, false);
            if (shared.TryGetValue(share.Key, out var t))
                return new(t.Table, share.Tx - t.X, share.Ty - t.Y, share.Tz - t.Z, true);
            var table = Scene3DFeatureTable.Of(mesh, sheet, source);
            shared[share.Key] = (table, share.Tx, share.Ty, share.Tz);
            return new(table, 0, 0, 0, true);
        }
        Vector3 L(Point3 p) => new((float)(p.X - origin.Item1), (float)(p.Y - origin.Item2), (float)(p.Z - origin.Item3));

        var conductorColours = Em3dSectionRenderer.ObjectColours(problem, origins, tech, theme, variant);
        var pin = theme.Resolve(ColorRole.LayoutPCellPin, variant);
        var ink = dark ? (R: (byte)205, G: (byte)210, B: (byte)215) : (R: (byte)60, G: (byte)64, B: (byte)70);
        var dielectrics = problem.Solids.Where(s => s.Role == Em3dRole.Dielectric && options.Wireframe?.Invoke(s.Name) != true)
                                 .Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();
        var materials = problem.Materials.Select((m, i) => (m, i)).ToDictionary(t => t.m.Name, t => t, StringComparer.Ordinal);
        (byte R, byte G, byte B) DielectricPaletteOf(string material)
        {
            var pal = dark ? DielectricDark : DielectricLight;
            return pal[Math.Max(0, dielectrics.IndexOf(material)) % pal.Length];
        }

        var b = new Accumulator(L) { SplitShading = options.SplitShadingCreases };
        var imageNotes = new List<string>();
        // brief-em3d-101 Phase B — the objects face images are on, and each one's triangles once it is tessellated.
        var faceImageObjects = new HashSet<string>(options.FaceImages?.Select(u => u.Object) ?? [], StringComparer.Ordinal);
        var faceMeshes = new Dictionary<string, (Em3dTriangleMesh Mesh, IReadOnlyList<string> Faces, bool Sheet, bool Dim)>(StringComparer.Ordinal);
        var bores = Scene3DBores.Of(problem, name => origins.TryGetValue(name, out var o) && o.Kind == Em3dObjectKind.Via);
        var walls = Scene3DWalls.Of(problem);
        var solidRuns = new RunTracker(b);
        var sheetRuns = new RunTracker(b);
        bool Dim(string name) => options.Context?.Invoke(name) == true;
        // brief-em3d-105 — an object's resolved look and its role's default (what it falls back to when the table is full).
        Scene3DLook? Look(string name, Scene3DKind kind, Em3dRole role, string material, (byte R, byte G, byte B) palette)
            => AppearanceRequestFor(kind, role, material, palette, tech, options.Appearance?.Invoke(name)) is { } request
                ? Scene3DLook.Of(request) : null;

        // ── solids ───────────────────────────────────────────────────────────────────────────
        bool Wire(string name) => options.Wireframe?.Invoke(name) == true;
        string? outermost = options.HideOutermostDielectric ? OutermostDielectric(problem, Wire) : null;
        // 3D editor bugs round 1 — a wireframe object's triangles: the ink colour at alpha 0; its edges: the ink, opaque.
        // 3D editor bugs round 2 — on a light background the mid-grey ink was too faint for an object drawn as its edges
        // ALONE (one-pixel lines, nothing filled): the wireframe's edges are near-black there. Dark keeps the ink.
        var wireInk = dark ? ink : (R: (byte)20, G: (byte)22, B: (byte)28);
        uint wireFill = Scene3DVertex.Pack(wireInk.R, wireInk.G, wireInk.B, 0), wireEdge = Scene3DVertex.Pack(wireInk.R, wireInk.G, wireInk.B, 255);
        foreach (var s in problem.Solids)
        {
            var place = faceImageObjects.Contains(s.Name) ? null : options.Instancing?.Invoke(s.Name);
            if (place is { } pl && solidRuns.Element(pl, s.Name)) continue;
            bool wire = Wire(s.Name);
            var kind = wire ? Scene3DKind.Body : KindOf(s, origins);
            uint rgba;
            bool translucent = false;
            switch (s.Role)
            {
                case Em3dRole.Air:
                    rgba = dark ? Scene3DVertex.Pack(150, 190, 255, AirAlpha) : Scene3DVertex.Pack(140, 185, 240, AirAlpha);
                    translucent = true;
                    break;
                case Em3dRole.Dielectric:
                {
                    var c = MaterialColour(tech, s.Material) ?? DielectricPaletteOf(s.Material);
                    rgba = Scene3DVertex.Pack(c.R, c.G, c.B, DielectricAlpha);
                    translucent = true;
                    break;
                }
                default:
                {
                    var c = conductorColours.TryGetValue(s.Name, out var sk) ? sk : new SkiaSharp.SKColor(150, 150, 155);
                    if (MaterialColour(tech, s.Material) is { } own) c = new SkiaSharp.SKColor(own.R, own.G, own.B);
                    rgba = Scene3DVertex.Pack(c.Red, c.Green, c.Blue, 255);
                    break;
                }
            }
            // brief-em3d-92 — the object's own transparency replaces its kind's alpha; the looks below keep theirs and win.
            var see = options.Transparency?.Invoke(s.Name);
            if (see is { } t) (rgba, translucent) = t.Apply(rgba);
            bool dim = Dim(s.Name);
            if (dim) (rgba, translucent) = (Dimmed(rgba, dark), true);
            if (wire) (rgba, translucent) = (wireFill, true);
            var ghost = options.Ghost?.Invoke(s.Name) ?? Scene3DGhost.None;
            if (ghost is Scene3DGhost.Ghost or Scene3DGhost.Taken) (rgba, translucent, dim) = (Ghosted(rgba, ghost == Scene3DGhost.Taken, dark), true, true);
            else if (ghost == Scene3DGhost.Pickable) (rgba, translucent) = (Ghosted(rgba, false, dark), true);
            else if (ghost == Scene3DGhost.Inert) dim = true;
            var solid = s;
            // A plated via's bore carves the barrel and the pads it passes through — in the drawing only (Scene3DBores).
            var (innerKey, innerMake) = bores?.Carved(s) is { } carved ? carved
                : NamedSurface(s.Primitive, FacesOf(s.Name)) ? ((object)(s.Primitive, SurfaceKey), () => AsFaceZero(Em3dTessellation.Of(solid)))
                : ((object)s.Primitive, () => Em3dTessellation.Of(solid));
            var mesh = Tessellate(innerKey, innerMake);
            // brief-em3d-138 — and a slab's outline walls where a conductor's side lies on them (Scene3DWalls): the wall round the
            // conductor comes last and draws with no depth bias. Its edge lines stay the uncarved surface's.
            Em3dTriangleMesh? edgeMesh = null;
            int free = 0;
            if (walls?.Carved(s, innerKey, mesh) is { } walled)
            {
                edgeMesh = mesh;
                mesh = Tessellate(walled.Key, walled.Make);
                free = walled.Free;
            }
            var (m, slot) = materials.TryGetValue(s.Material, out var mt) ? (mt.m, mt.i) : ((Em3dMaterial?)null, -1);
            if (faceImageObjects.Contains(s.Name)) faceMeshes[s.Name] = (mesh, FacesOf(s.Name), false, dim);
            // brief-em3d-105 R-em3d105-4 — its look, from the one resolver, with the palette colour it would otherwise be drawn in.
            var look = Look(s.Name, kind, s.Role, s.Material,
                            s.Role == Em3dRole.Dielectric ? DielectricPaletteOf(s.Material) : ConductorPalette(conductorColours, s.Name));
            b.Object(new Scene3DObject
            {
                Id = 0, Name = s.Name, Kind = kind, Role = s.Role, Material = s.Material, MaterialValues = m, MaterialSlot = slot,
                Rgba = rgba, Translucent = translucent,
                InitiallyVisible = wire || (s.Role != Em3dRole.Air && s.Name != outermost),
                FaceNames = FacesOf(s.Name),
                CapCentres = s.Primitive is Em3dCylinder cyl ? [cyl.AxisStart, cyl.AxisEnd]
                           : s.Primitive is Em3dSphere ball && NamedSurface(ball, FacesOf(s.Name)) ? [ball.Center]
                           : s.Primitive is Em3dEllipsoid egg && NamedSurface(egg, FacesOf(s.Name)) ? [egg.Center] : null,
                Context = dim, Wireframe = wire, Transparency = see,
            }, mesh, wire && s.Primitive is Em3dCylinder c0 ? CylinderGenerators(c0).Select(q => (q, wireEdge)) : null,
               faces: true, features: Features(s.Name, mesh, sheet: false, (s.Primitive as Em3dShapeSolid)?.Edges), wireEdges: wire ? wireEdge : null,
               look: look, kernelEdges: s.Primitive is Em3dShapeSolid { Edges.Count: > 0 } shape && ReferenceEquals(mesh.Vertices, shape.Display.Vertices) ? shape : null,
               freeTriangles: free, edgeMesh: edgeMesh);
            if (place is { } p0) solidRuns.Prototype(p0, s.Name);
            else solidRuns.Break();
        }

        // ── sheets ───────────────────────────────────────────────────────────────────────────
        foreach (var sh in problem.Sheets)
        {
            var image = options.Images?.Invoke(sh.Name);
            var place = image is null && !faceImageObjects.Contains(sh.Name) ? options.Instancing?.Invoke(sh.Name) : null;
            if (place is { } pl && sheetRuns.Element(pl, sh.Name)) continue;
            var c = conductorColours.TryGetValue(sh.Name, out var sk) ? sk : new SkiaSharp.SKColor(150, 150, 155);
            if (MaterialColour(tech, sh.Material) is { } own) c = new SkiaSharp.SKColor(own.R, own.G, own.B);
            var sheet = sh;
            var mesh = Tessellate((sh.Outline, sh.Holes, sh.Z, sh.Frame), () => Em3dTessellation.OfSheet(sheet));
            var (m, slot) = materials.TryGetValue(sh.Material, out var mt) ? (mt.m, mt.i) : ((Em3dMaterial?)null, -1);
            var names = FacesOf(sh.Name);
            bool dim = Dim(sh.Name);
            bool wire = Wire(sh.Name) && image is null;
            uint rgba = Scene3DVertex.Pack(c.Red, c.Green, c.Blue, 255);
            bool translucent = false;
            var see = options.Transparency?.Invoke(sh.Name);
            if (see is { } t) (rgba, translucent) = t.Apply(rgba);
            // brief-em3d-101 R-em3d101-4a — an image sheet is drawn with its image, whatever its material (or none, §1e): the
            // object's alpha (its transparency; the context's while dimmed) multiplies the texture's, and either below 1 sorts it
            // with the translucent objects.
            if (faceImageObjects.Contains(sh.Name)) faceMeshes[sh.Name] = (mesh, names.Count > 0 ? names : SheetFaceNames, true, dim);
            Scene3DTexture? texture = null;
            uint imageRgba = 0;
            if (image is not null)
            {
                texture = Scene3DTextures.Get(image.Path);
                byte a = dim ? ContextAlpha : see is { } ti ? ti.Alpha(255) : (byte)255;
                imageRgba = Scene3DVertex.Pack(255, 255, 255, a);
                translucent = a < 255 || !texture.Opaque;
                rgba = Scene3DVertex.Pack(200, 200, 205, a);
                if (Scene3DTextures.DownsampleNote(texture) is { } note) imageNotes.Add(note);
            }
            b.Object(new Scene3DObject
            {
                Id = 0, Name = sh.Name, Kind = Scene3DKind.Sheet, Role = Em3dRole.Conductor, Material = sh.Material, MaterialValues = m,
                MaterialSlot = slot, Rgba = wire ? wireFill : dim && image is null ? Dimmed(rgba, dark) : rgba, Translucent = dim || wire || translucent,
                FaceNames = names.Count > 0 ? names : SheetFaceNames, Context = dim, Wireframe = wire, Transparency = see,
                Underlay = image is not null, ImageInFront = image?.InFront == true, ImageName = image is null ? null : Path.GetFileName(image.Path),
            }, mesh, faces: true, sheet: true, features: Features(sh.Name, mesh, sheet: true, sheetNames: names.Count > 0 ? names : SheetFaceNames),
               wireEdges: wire ? wireEdge : null, look: Look(sh.Name, Scene3DKind.Sheet, Em3dRole.Conductor, sh.Material, ConductorPalette(conductorColours, sh.Name)));
            if (image is not null)
            {
                b.Image((uint)(b.LastIndex + 1), texture!, mesh, image, imageRgba, dim || translucent, surface: true, face: 0);
                b.PlacedImages[sh.Name] = image;
            }
            if (place is { } p0) sheetRuns.Prototype(p0, sh.Name);
            else sheetRuns.Break();
        }

        // ── brief-sar-8b — sheets on a cylinder: the patch's own tessellation, drawn as a sheet is ────────────────
        foreach (var cs in problem.CylinderSheets)
        {
            var c = conductorColours.TryGetValue(cs.Name, out var sk) ? sk : new SkiaSharp.SKColor(150, 150, 155);
            if (MaterialColour(tech, cs.Material) is { } own) c = new SkiaSharp.SKColor(own.R, own.G, own.B);
            var mesh = cs.Patch.Tessellate(cs.Name);
            var (m, slot) = materials.TryGetValue(cs.Material, out var mt) ? (mt.m, mt.i) : ((Em3dMaterial?)null, -1);
            bool dim = Dim(cs.Name);
            bool wire = Wire(cs.Name);
            uint rgba = Scene3DVertex.Pack(c.Red, c.Green, c.Blue, 255);
            bool translucent = false;
            var see = options.Transparency?.Invoke(cs.Name);
            if (see is { } t) (rgba, translucent) = t.Apply(rgba);
            b.Object(new Scene3DObject
            {
                Id = 0, Name = cs.Name, Kind = Scene3DKind.Sheet, Role = Em3dRole.Conductor, Material = cs.Material, MaterialValues = m,
                MaterialSlot = slot, Rgba = wire ? wireFill : dim ? Dimmed(rgba, dark) : rgba, Translucent = dim || wire || translucent,
                FaceNames = SheetFaceNames, Context = dim, Wireframe = wire, Transparency = see,
            }, mesh, faces: true, sheet: true, features: Features(cs.Name, mesh, sheet: true, sheetNames: SheetFaceNames),
               wireEdges: wire ? wireEdge : null, look: Look(cs.Name, Scene3DKind.Sheet, Em3dRole.Conductor, cs.Material, ConductorPalette(conductorColours, cs.Name)));
            sheetRuns.Break();
        }

        // ── ports: a named sheet and a direction arrow ───────────────────────────────────────
        // 3D editor bugs round 9 — a rectangular lumped port is a checkerboard over exactly its own rectangle, − edge to +
        // edge and its full width, outlined, with a flat arrow on it pointing to the + edge: an arrow the size of its longer
        // side stood far outside a wide, short port and said nothing of where the port starts, ends or how wide it is. A wave port keeps its voltage path's arrow, and a
        // coaxial one its annulus.
        uint pinFill = Scene3DVertex.Pack(pin.R, pin.G, pin.B, PortAlpha), pinLine = Scene3DVertex.Pack(pin.R, pin.G, pin.B, 255);
        var (checkFill, checkArrow) = CheckerColours(pin.R, pin.G, pin.B);
        foreach (var p in problem.Ports)
        {
            var obj = new Scene3DObject
            {
                Id = 0, Name = p.Name, Kind = Scene3DKind.Port, PortNumber = p.Number, Rgba = pinFill, Translucent = true,
            };
            // A port snaps as a sheet does — its corners, its edges and their midpoints, its centre — so a measurement can
            // start or end on one. The features come from the port's own surface, never from what is drawn on it: the
            // checkerboard's cells and its arrow would add corners that are not the port's. Its ID-pass pixels carry no face
            // (Scene3DVertex.NoFace, which keeps Face mode off it); the snap reads them as the table's one face.
            static Scene3DFeatureRef Snaps(Em3dTriangleMesh m) => new(Scene3DFeatureTable.Of(m, sheet: true), 0, 0, 0, false);
            // brief-sar-8b — a gap on a cylinder: its patch, and the arrow along it from its centre on the surface.
            if (p.Cylinder is { } gap)
            {
                var patch = gap.Patch.Tessellate(p.Name);
                b.Object(obj, patch, PortArrow(p).Select(q => (q, pinLine)), features: Snaps(patch));
                continue;
            }
            if (p.Kind == Em3dPortKind.Lumped && p.Annulus is null && Checkerboard(p, pinFill, checkFill, checkArrow) is { } board)
            {
                var (sv, st) = PortSheet(p);
                b.Object(obj, board.Mesh, Outline(p).Select(q => (q, pinLine)), vertexRgba: board.Rgba,
                         features: Snaps(new Em3dTriangleMesh(sv, st)));
                continue;
            }
            // brief-em3d-114 R-em3d114-3c — a multi-terminal port's terminals share one rectangle: the first draws it, and each
            // other terminal a strip along its own voltage path, so each arrow (reference to conductor) has a sheet to pick it by
            var (verts, tris) = p.FaceGroup is { } g && p.VoltagePath is { } path && problem.Ports.First(q => q.FaceGroup == g) != p
                ? TerminalStrip(p, path) : PortSheet(p);
            var sheetMesh = new Em3dTriangleMesh(verts, tris);
            b.Object(obj, sheetMesh, PortArrow(p).Select(q => (q, pinLine)), features: Snaps(sheetMesh));
        }

        // ── brief-em3d-49: face boundaries, tinted just off their faces (never z-fighting the solid) ─
        if (options.FaceTints is { Count: > 0 } tints)
        {
            double lift = 1e-4 * Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
            b.TintLift = (float)lift;
            foreach (var t in tints)
            {
                var (r, g, bl) = t.Colour ?? (t.Kind == Em3dFaceBoundaryKind.Pec ? ((byte)150, (byte)150, (byte)158) : ((byte)214, (byte)168, (byte)64));
                uint fill = Scene3DVertex.Pack(r, g, bl, BoundaryTintAlpha), edge = Scene3DVertex.Pack(r, g, bl, 255);
                var verts = new List<Point3>();
                var tris = new List<Em3dTriangle>();
                var lines = new List<(Point3, uint)>();
                foreach (var piece in t.Pieces)
                {
                    // brief-em3d-65 — a curved kernel face (Palace takes a boundary on one): its own triangles, each lifted
                    // along its own normal.
                    if (piece.Mesh is { } curved)
                    {
                        foreach (var tr in curved.Triangles)
                        {
                            Point3 a = curved.Vertices[tr.A], c1 = curved.Vertices[tr.B], c2 = curved.Vertices[tr.C];
                            double ux = c1.X - a.X, uy = c1.Y - a.Y, uz = c1.Z - a.Z, vx = c2.X - a.X, vy = c2.Y - a.Y, vz = c2.Z - a.Z;
                            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                            var tn = nl > 0 ? new Point3(nx / nl, ny / nl, nz / nl) : new Point3(0, 0, 0);
                            Point3 Lift(Point3 q) => new(q.X + tn.X * lift, q.Y + tn.Y * lift, q.Z + tn.Z * lift);
                            int k0 = verts.Count;
                            verts.Add(Lift(a)); verts.Add(Lift(c1)); verts.Add(Lift(c2));
                            tris.Add(new Em3dTriangle(k0, k0 + 1, k0 + 2, t.Name));
                        }
                        continue;
                    }
                    var n = piece.Normal;
                    Point3 Up(Point3 q) => new(q.X + n.X * lift, q.Y + n.Y * lift, q.Z + n.Z * lift);
                    var mesh = Em3dTessellation.OfSheet(piece.AsSheet(t.Name, "", 0, 0));
                    int at = verts.Count;
                    verts.AddRange(mesh.Vertices.Select(Up));
                    tris.AddRange(mesh.Triangles.Select(tr => new Em3dTriangle(tr.A + at, tr.B + at, tr.C + at, t.Name)));
                    foreach (var ring in piece.Holes.Prepend(piece.Outer))
                        for (int k = 0; k < ring.Count; k++) { lines.Add((Up(ring[k]), edge)); lines.Add((Up(ring[(k + 1) % ring.Count]), edge)); }
                }
                b.Object(new Scene3DObject
                {
                    Id = 0, Name = FaceTintPrefix + t.Name, Kind = Scene3DKind.Boundary, Rgba = fill, Translucent = true, Tint = true,
                }, new Em3dTriangleMesh(verts, tris), lines);
            }
        }

        // ── brief-em3d-101 Phase B: images on faces — a record each, drawn over its face from the face's own triangles ─────
        if (options.FaceImages is { Count: > 0 } faceImages)
            foreach (var use in faceImages)
            {
                if (!Scene3DFaceImages.Drawn(faceImages, use) || !faceMeshes.TryGetValue(use.Object, out var host)) continue;
                if (Scene3DFaceImages.Place(use, host.Mesh, host.Faces, host.Sheet, out string? why) is not { } placed)
                {
                    b.FaceImageProblems[use.FaceSpelled] = why!;
                    continue;
                }
                var texture = Scene3DTextures.Get(use.Path ?? "");
                if (Scene3DTextures.DownsampleNote(texture) is { } note) imageNotes.Add(note);
                // its OWN transparency, and the instances' opacity multiplied on — never its object's (R-em3d101-10)
                byte a = host.Dim ? ContextAlpha : new Scene3DTransparency(use.Record.Transparency, use.Opacity).Alpha(255);
                bool translucent = a < 255 || !texture.Opaque;
                var verts = new List<Point3>();
                var tris = new List<Em3dTriangle>();
                foreach (var (p0, p1, p2) in placed.Triangles)
                {
                    int k0 = verts.Count;
                    verts.Add(p0); verts.Add(p1); verts.Add(p2);
                    tris.Add(new Em3dTriangle(k0, k0 + 1, k0 + 2, use.Record.Face));
                }
                var sub = new Em3dTriangleMesh(verts, tris);
                b.Object(new Scene3DObject
                {
                    Id = 0, Name = C3dImages.FacePrefix + use.FaceSpelled, Kind = Scene3DKind.Boundary, Tint = true,
                    Rgba = Scene3DVertex.Pack(200, 200, 205, a), Translucent = translucent, Context = host.Dim,
                    ImageName = Path.GetFileName(use.Path ?? use.Record.Image.Path), FaceNames = [use.Record.Face],
                }, sub);
                b.Image((uint)(b.LastIndex + 1), texture, sub, placed.Placed, Scene3DVertex.Pack(255, 255, 255, a), translucent,
                        surface: true, face: 0, onFace: true);
                b.PlacedFaceImages.Add(placed);
            }

        // ── the air box: its six faces (hidden until asked for) and its twelve edges ─────────
        if (options.DrawAirBox)
        {
            foreach (var (face, kind, corners) in Faces(box))
            {
                var (r, g, bl) = kind switch
                {
                    Em3dBoundaryKind.Pec       => ((byte)150, (byte)150, (byte)158),
                    Em3dBoundaryKind.Absorbing => ((byte)80, (byte)130, (byte)235),
                    Em3dBoundaryKind.Pmc       => ((byte)235, (byte)140, (byte)50),
                    _                          => ((byte)160, (byte)80, (byte)210),
                };
                // brief-em3d-49 R-em3d49-3a — the editor's box: an absorbing face is untinted (it is what the space does
                // anyway), a symmetry face hatched, and every face picked last.
                bool editor = options.EditorBoundaries;
                byte alpha = editor && kind == Em3dBoundaryKind.Absorbing ? (byte)0 : FaceAlpha;
                uint hatchInk = Scene3DVertex.Pack(r, g, bl, 255);
                b.Object(new Scene3DObject
                {
                    Id = 0, Name = Em3dAirBox.FaceName(face), Kind = Scene3DKind.Boundary, Boundary = kind,
                    Rgba = Scene3DVertex.Pack(r, g, bl, alpha), Translucent = true, InitiallyVisible = false,
                    PickLast = editor, FaceNames = editor ? [face] : [],
                }, new Em3dTriangleMesh(corners, [new Em3dTriangle(0, 1, 2, "", editor ? 0 : -1), new Em3dTriangle(0, 2, 3, "", editor ? 0 : -1)]),
                   editor && kind == Em3dBoundaryKind.Symmetry ? Hatch(corners).Select(q => (q, hatchInk)) : null,
                   faces: editor);
            }
            uint edge = Scene3DVertex.Pack(ink.R, ink.G, ink.B, 255);
            b.Object(new Scene3DObject { Id = 0, Name = AirBoxEdgesName, Kind = Scene3DKind.Boundary, Rgba = edge },
                     null, BoxEdges(box).Select(q => (q, edge)));
        }

        cache?.EndBuild();
        return b.Finish(generation, origin, problem, imageNotes.Count == 0 ? notes : [.. notes ?? [], .. imageNotes.Distinct()]);
    }

    /// <summary>The object holding the air box's twelve edges.</summary>
    public const string AirBoxEdgesName = "airbox";

    /// <summary>A sheet is one surface, face 0.</summary>
    public static readonly IReadOnlyList<string> SheetFaceNames = ["surface"];

    /// <summary>Opacity of the parent drawn around a pushed-in child.</summary>
    public const byte ContextAlpha = 70;

    /// <summary>Opacity of a ghost (brief-em3d-66).</summary>
    public const byte GhostAlpha = 60;

    /// <summary>brief-em3d-66 R-em3d66-2e — a ghost: the object's colour pulled toward the background's grey, or the overlay's
    /// red for what a subtraction takes away, at <see cref="GhostAlpha"/>.</summary>
    private static uint Ghosted(uint rgba, bool taken, bool dark)
    {
        if (taken) return Scene3DVertex.Pack(225, 70, 70, GhostAlpha);
        byte g = dark ? (byte)110 : (byte)170;
        byte Mix(uint c) => (byte)((c + 2 * g) / 3);
        return Scene3DVertex.Pack(Mix(rgba & 0xFF), Mix((rgba >> 8) & 0xFF), Mix((rgba >> 16) & 0xFF), GhostAlpha);
    }

    /// <summary>brief-em3d-48 R-em3d48-4a — a colour pulled halfway to the background's grey and made translucent: the
    /// parent around a pushed-in child reads as surroundings, not as something to edit.</summary>
    private static uint Dimmed(uint rgba, bool dark)
    {
        byte g = dark ? (byte)70 : (byte)190;
        byte Mix(uint c) => (byte)((c + g) / 2);
        return Scene3DVertex.Pack(Mix(rgba & 0xFF), Mix((rgba >> 8) & 0xFF), Mix((rgba >> 16) & 0xFF), ContextAlpha);
    }

    /// <summary>
    /// brief-em3d-48 R-em3d48-3a — follows the runs of one pass (solids, or sheets) as the builder walks them: the
    /// first element of a run is the PROTOTYPE, tessellated as any object is; each later element's k-th object, when it
    /// is named as the prototype's k-th is, becomes an element object that draws the prototype's triangles under its own
    /// offset. Anything that does not line up is drawn as an ordinary object — slower, never wrong.
    /// </summary>
    private sealed class RunTracker(Accumulator acc)
    {
        private object? _run;
        private int _protoElement, _element = -1, _k;
        private (double X, double Y, double Z) _protoAt;
        private readonly List<(string Local, int Index)> _proto = [];
        private bool _contiguous;
        private int _group = -1;

        public void Break() { _run = null; _proto.Clear(); _group = -1; }

        /// <summary>An object the builder has just added as an ordinary object: it starts a run, or extends its prototype.</summary>
        public void Prototype(Scene3DInstancing p, string name)
        {
            int index = acc.LastIndex;
            if (!Equals(_run, p.Run) || p.Element != _protoElement || _element != _protoElement)
            {
                _run = p.Run;
                _protoElement = _element = p.Element;
                _protoAt = (p.Tx, p.Ty, p.Tz);
                _proto.Clear();
                _contiguous = true;
                _group = -1;
            }
            if (_proto.Count > 0 && _proto[^1].Index + 1 != index) _contiguous = false;
            _proto.Add((p.LocalName, index));
        }

        /// <summary>True when the object is a later element's and was deferred as an element object.</summary>
        public bool Element(Scene3DInstancing p, string name)
        {
            if (!Equals(_run, p.Run) || !_contiguous || _proto.Count == 0 || p.Element == _protoElement) return false;
            if (p.Element != _element) { _element = p.Element; _k = 0; }
            if (_k >= _proto.Count || _proto[_k].Local != p.LocalName) return false;
            if (_group < 0) _group = acc.NewGroup(_proto[0].Index, _proto.Count);
            acc.Defer(_group, _element, _proto[_k].Index, name, p.Tx - _protoAt.X, p.Ty - _protoAt.Y, p.Tz - _protoAt.Z);
            _k++;
            return true;
        }
    }

    /// <summary>brief-em3d-102 R-em3d102-2b — the cache key's second half for a sphere whose one face is named.</summary>
    private const string SurfaceKey = "surface";

    /// <summary>brief-em3d-102 §0 — a DRAWN sphere names its one face (<c>surface</c>, by its provenance), so its triangles carry
    /// face 0 and Face mode selects it; a ball, which names none, keeps <see cref="FaceUnknown"/>. Per object, not per primitive.</summary>
    private static bool NamedSurface(Em3dPrimitive p, IReadOnlyList<string> faces) => p is Em3dSphere or Em3dEllipsoid && faces.Count == 1;

    private static Em3dTriangleMesh AsFaceZero(Em3dTriangleMesh m)
        => new(m.Vertices, [.. m.Triangles.Select(t => t with { Face = 0 })]);

    private static Scene3DKind KindOf(Em3dSolid s, IReadOnlyDictionary<string, Em3dObjectOrigin> origins)
    {
        if (origins.TryGetValue(s.Name, out var o))
            return o.Kind switch
            {
                Em3dObjectKind.Dielectric => Scene3DKind.Dielectric,
                Em3dObjectKind.Air        => Scene3DKind.Air,
                Em3dObjectKind.Body       => Scene3DKind.Body,
                Em3dObjectKind.Via        => s.Role == Em3dRole.Air ? Scene3DKind.Air : Scene3DKind.Via,
                Em3dObjectKind.Wire       => Scene3DKind.Wire,
                _                         => Scene3DKind.Conductor,
            };
        return s.Role switch
        {
            Em3dRole.Air        => Scene3DKind.Air,
            Em3dRole.Dielectric => Scene3DKind.Dielectric,
            _                   => s.Primitive is Em3dSweep or Em3dSphere or Em3dTruncatedSphere ? Scene3DKind.Wire : Scene3DKind.Conductor,
        };
    }

    /// <summary>The dielectric that encloses the most volume — the substrate a user looks through.
    /// Ties go to the first in problem order.</summary>
    public static string? OutermostDielectric(Em3dProblem problem) => OutermostDielectric(problem, null);

    private static string? OutermostDielectric(Em3dProblem problem, Func<string, bool>? skip)
    {
        string? best = null;
        double vol = -1;
        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Dielectric && skip?.Invoke(s.Name) != true))
        {
            double v = Em3dSizeEstimate.Volume(s.Primitive);
            if (v > vol) { vol = v; best = s.Name; }
        }
        return best;
    }

    // ── geometry of ports and the box ────────────────────────────────────────────────────────

    /// <summary>3D editor bugs round 1 — four lines along a wireframe cylinder's side, a quarter turn apart: its feature
    /// edges are only the two rims, which alone read as two circles, not a solid.</summary>
    private static IEnumerable<Point3> CylinderGenerators(Em3dCylinder c)
    {
        var a = new Vector3((float)(c.AxisEnd.X - c.AxisStart.X), (float)(c.AxisEnd.Y - c.AxisStart.Y), (float)(c.AxisEnd.Z - c.AxisStart.Z));
        if (a.LengthSquared() == 0) yield break;
        a = Vector3.Normalize(a);
        var u = Vector3.Normalize(Vector3.Cross(a, MathF.Abs(a.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        var w = Vector3.Cross(a, u);
        foreach (var d in new[] { u, w, -u, -w })
        {
            double dx = d.X * c.Radius, dy = d.Y * c.Radius, dz = d.Z * c.Radius;
            yield return new Point3(c.AxisStart.X + dx, c.AxisStart.Y + dy, c.AxisStart.Z + dz);
            yield return new Point3(c.AxisEnd.X + dx, c.AxisEnd.Y + dy, c.AxisEnd.Z + dz);
        }
    }

    private static (List<Point3>, List<Em3dTriangle>) PortSheet(Em3dPort p)
    {
        var (a, c) = (p.Min, p.Max);
        var verts = new List<Point3>();
        var tris = new List<Em3dTriangle>();
        if (p.Annulus is { } an)
        {
            double cx = (a.X + c.X) / 2, cy = (a.Y + c.Y) / 2, z = a.Z;
            for (int k = 0; k < AnnulusSegments; k++)
            {
                double t = 2 * Math.PI * k / AnnulusSegments;
                verts.Add(new Point3(cx + an.InnerRadiusM * Math.Cos(t), cy + an.InnerRadiusM * Math.Sin(t), z));
                verts.Add(new Point3(cx + an.OuterRadiusM * Math.Cos(t), cy + an.OuterRadiusM * Math.Sin(t), z));
            }
            for (int k = 0; k < AnnulusSegments; k++)
            {
                int i0 = 2 * k, o0 = 2 * k + 1, i1 = 2 * ((k + 1) % AnnulusSegments), o1 = i1 + 1;
                tris.Add(new Em3dTriangle(i0, o0, o1, p.Name));
                tris.Add(new Em3dTriangle(i0, o1, i1, p.Name));
            }
            return (verts, tris);
        }
        if (p.Quad is { } quad) verts.AddRange(quad.Corners);
        else if (a.X == c.X) verts.AddRange([new(a.X, a.Y, a.Z), new(a.X, c.Y, a.Z), new(a.X, c.Y, c.Z), new(a.X, a.Y, c.Z)]);
        else if (a.Y == c.Y) verts.AddRange([new(a.X, a.Y, a.Z), new(c.X, a.Y, a.Z), new(c.X, a.Y, c.Z), new(a.X, a.Y, c.Z)]);
        else                 verts.AddRange([new(a.X, a.Y, a.Z), new(c.X, a.Y, a.Z), new(c.X, c.Y, a.Z), new(a.X, c.Y, a.Z)]);
        tris.Add(new Em3dTriangle(0, 1, 2, p.Name));
        tris.Add(new Em3dTriangle(0, 2, 3, p.Name));
        return (verts, tris);
    }

    /// <summary>brief-em3d-114 — a terminal's own sheet: a strip in the port's plane along its voltage path, a fifth of the
    /// path's length wide.</summary>
    private static (List<Point3>, List<Em3dTriangle>) TerminalStrip(Em3dPort p, Em3dSegment path)
    {
        var (from, to) = (path.From, path.To);
        var d = new Vector3((float)(to.X - from.X), (float)(to.Y - from.Y), (float)(to.Z - from.Z));
        var n = p.Min.X == p.Max.X ? Vector3.UnitX : p.Min.Y == p.Max.Y ? Vector3.UnitY : Vector3.UnitZ;
        var side = Vector3.Cross(d, n) * 0.1f;
        Point3 Off(Point3 q, float k) => new(q.X + side.X * k, q.Y + side.Y * k, q.Z + side.Z * k);
        return ([Off(from, -1), Off(to, -1), Off(to, 1), Off(from, 1)],
                [new Em3dTriangle(0, 1, 2, p.Name), new Em3dTriangle(0, 2, 3, p.Name)]);
    }

    /// <summary>3D editor bugs round 9 — the checkerboard's second colour and its arrow's: against a dark pin colour the cells
    /// alternate with white and the arrow is near-black; against a light one, the reverse. The cells take the port's opacity,
    /// the arrow none, so it reads over both.</summary>
    private static (uint Partner, uint Arrow) CheckerColours(byte r, byte g, byte b)
        => 0.2126 * r + 0.7152 * g + 0.0722 * b > 140
            ? (Scene3DVertex.Pack(40, 40, 40, PortAlpha), Scene3DVertex.Pack(255, 255, 255, 255))
            : (Scene3DVertex.Pack(255, 255, 255, PortAlpha), Scene3DVertex.Pack(20, 20, 20, 255));

    /// <summary>
    /// 3D editor bugs round 9 — a rectangular lumped port's sheet as a checkerboard of <paramref name="a"/> and
    /// <paramref name="b"/> cells, each cell its own four vertices so its colour is flat. The cells are as near square as the
    /// rectangle allows, two of them across its shorter side, at most <see cref="CheckerCellsMax"/> along either side. Over
    /// them, in the same plane, a flat <paramref name="arrow"/>-coloured arrow from the − edge towards the + edge (the port's
    /// <see cref="Em3dPort.Direction"/>), centred across its width. The arrow's triangles come AFTER the cells in the one
    /// translucent draw, which writes no depth, so they paint over the cells like a texture and never fight them.
    /// Null for a rectangle that is not a sheet in one axis plane.
    /// </summary>
    internal static (Em3dTriangleMesh Mesh, uint[] Rgba)? Checkerboard(Em3dPort p, uint a, uint b, uint arrow)
    {
        // The sheet as a corner and its two sides: an upright port's from its box, a turned one's from its corners.
        Point3 origin, su3, sv3;
        if (p.Quad is { } quad)
        {
            origin = quad.A;
            (su3, sv3) = quad.Sides;
        }
        else
        {
            var (lo, hi) = (p.Min, p.Max);
            double[] e3 = [hi.X - lo.X, hi.Y - lo.Y, hi.Z - lo.Z];
            int flat = Array.FindIndex(e3, e => e == 0);
            if (flat < 0) return null;
            int iu = (flat + 1) % 3, iv = (flat + 2) % 3;
            static Point3 Axis(int i, double l) => i == 0 ? new Point3(l, 0, 0) : i == 1 ? new Point3(0, l, 0) : new Point3(0, 0, l);
            (origin, su3, sv3) = (lo, Axis(iu, e3[iu]), Axis(iv, e3[iv]));
        }
        static double Len(Point3 q) => Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        double[] ext = [Len(su3), Len(sv3)];
        const int u = 0, v = 1;
        if (!(ext[u] > 0 && ext[v] > 0)) return null;
        double cell = Math.Min(ext[u], ext[v]) / 2;
        int nu = Math.Clamp((int)Math.Round(ext[u] / cell), 1, CheckerCellsMax), nv = Math.Clamp((int)Math.Round(ext[v] / cell), 1, CheckerCellsMax);
        var verts = new List<Point3>(4 * nu * nv + 7);
        var tris = new List<Em3dTriangle>(2 * nu * nv + 3);
        var rgba = new List<uint>(4 * nu * nv + 7);
        Point3 At(double fu, double fv)
            => new(origin.X + su3.X * fu + sv3.X * fv, origin.Y + su3.Y * fu + sv3.Y * fv, origin.Z + su3.Z * fu + sv3.Z * fv);
        void Quad(Point3 p0, Point3 p1, Point3 p2, Point3 p3, uint colour)
        {
            int k = verts.Count;
            verts.AddRange([p0, p1, p2, p3]);
            rgba.AddRange([colour, colour, colour, colour]);
            tris.Add(new Em3dTriangle(k, k + 1, k + 2, p.Name));
            tris.Add(new Em3dTriangle(k, k + 2, k + 3, p.Name));
        }
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                double u0 = (double)i / nu, u1 = (double)(i + 1) / nu, v0 = (double)j / nv, v1 = (double)(j + 1) / nv;
                Quad(At(u0, v0), At(u1, v0), At(u1, v1), At(u0, v1), ((i + j) & 1) == 0 ? a : b);
            }

        // The arrow, in the sheet's (along, across) fractions: along runs − to +, so a negative direction flips it.
        static double Dot(Point3 q, Point3 r) => q.X * r.X + q.Y * r.Y + q.Z * r.Z;
        double[] d = [Dot(p.Direction, su3) / ext[u], Dot(p.Direction, sv3) / ext[v]];
        int along = Math.Abs(d[u]) >= Math.Abs(d[v]) ? u : v, across = along == u ? v : u;
        if (d[along] != 0)
        {
            double h = ext[along], w = ext[across], s = Math.Min(h, w);
            double headLen = Math.Min(0.4 * h, 0.6 * s), headHalf = 0.35 * s, shaftHalf = 0.12 * s;
            double tail = 0.1 * h, tip = 0.9 * h, neck = tip - headLen;
            bool flip = d[along] < 0;
            Point3 Arrow(double t, double c)
            {
                double fa = (flip ? h - t : t) / h, fc = 0.5 + c / w;
                return along == u ? At(fa, fc) : At(fc, fa);
            }
            Quad(Arrow(tail, -shaftHalf), Arrow(neck, -shaftHalf), Arrow(neck, shaftHalf), Arrow(tail, shaftHalf), arrow);
            int k = verts.Count;
            verts.AddRange([Arrow(neck, -headHalf), Arrow(tip, 0), Arrow(neck, headHalf)]);
            rgba.AddRange([arrow, arrow, arrow]);
            tris.Add(new Em3dTriangle(k, k + 1, k + 2, p.Name));
        }
        return (new Em3dTriangleMesh(verts, tris), [.. rgba]);
    }

    /// <summary>3D editor bugs round 9 — a rectangular port's four edges, as pairs of points.</summary>
    private static List<Point3> Outline(Em3dPort p)
    {
        var (verts, _) = PortSheet(p);
        var lines = new List<Point3>(8);
        for (int k = 0; k < verts.Count; k++) { lines.Add(verts[k]); lines.Add(verts[(k + 1) % verts.Count]); }
        return lines;
    }

    /// <summary>The arrow's segments, as pairs of points: shaft then two barbs. A wave port's arrow is
    /// its voltage path; a lumped port's runs along its direction through the sheet's centre.</summary>
    private static List<Point3> PortArrow(Em3dPort p)
    {
        Point3 from, to;
        if (p.Kind == Em3dPortKind.Wave && p.VoltagePath is { } vp) (from, to) = (vp.From, vp.To);
        else
        {
            var c = p.Cylinder?.Patch.Centre ?? p.Quad?.Centre ?? new Point3((p.Min.X + p.Max.X) / 2, (p.Min.Y + p.Max.Y) / 2, (p.Min.Z + p.Max.Z) / 2);
            double side = p.Quad is { } quad ? Math.Max(quad.Extent(p.Direction).Length, quad.Extent(p.Direction).Width)
                        : Math.Max(p.Max.X - p.Min.X, Math.Max(p.Max.Y - p.Min.Y, p.Max.Z - p.Min.Z));
            if (p.Annulus is { } an) side = 2 * an.OuterRadiusM;
            double h = side * ArrowFraction / 2;
            var d = p.Direction;
            from = new Point3(c.X - d.X * h, c.Y - d.Y * h, c.Z - d.Z * h);
            to = new Point3(c.X + d.X * h, c.Y + d.Y * h, c.Z + d.Z * h);
        }
        var dir = new Vector3((float)(to.X - from.X), (float)(to.Y - from.Y), (float)(to.Z - from.Z));
        float len = dir.Length();
        if (len <= 0) return [];
        dir /= len;
        var side3 = Vector3.Cross(dir, MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
        side3 = Vector3.Normalize(side3);
        double head = len * 0.25;
        Point3 Barb(float s) => new(to.X - dir.X * head + side3.X * head * 0.5 * s,
                                    to.Y - dir.Y * head + side3.Y * head * 0.5 * s,
                                    to.Z - dir.Z * head + side3.Z * head * 0.5 * s);
        return [from, to, to, Barb(1), to, Barb(-1)];
    }

    private static IEnumerable<(string Face, Em3dBoundaryKind Kind, Point3[] Corners)> Faces(Em3dAirBox b)
    {
        var (lo, hi) = (b.Min, b.Max);
        yield return ("xmin", b.Faces.XMin, [new(lo.X, lo.Y, lo.Z), new(lo.X, lo.Y, hi.Z), new(lo.X, hi.Y, hi.Z), new(lo.X, hi.Y, lo.Z)]);
        yield return ("xmax", b.Faces.XMax, [new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, lo.Y, hi.Z)]);
        yield return ("ymin", b.Faces.YMin, [new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, lo.Y, hi.Z), new(lo.X, lo.Y, hi.Z)]);
        yield return ("ymax", b.Faces.YMax, [new(lo.X, hi.Y, lo.Z), new(lo.X, hi.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, hi.Y, lo.Z)]);
        yield return ("zmin", b.Faces.ZMin, [new(lo.X, lo.Y, lo.Z), new(lo.X, hi.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, lo.Y, lo.Z)]);
        yield return ("zmax", b.Faces.ZMax, [new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z)]);
    }

    /// <summary>brief-em3d-49 — the name prefix of a face boundary's tint object; the air-box toggle does not hide these.</summary>
    public const string FaceTintPrefix = "boundary:";

    /// <summary>brief-em3d-75 — a thermal setup's boundary tint is named <c>boundary:thermal:&lt;object&gt;/&lt;face&gt;</c>: this
    /// is the part after <see cref="FaceTintPrefix"/>.</summary>
    public const string ThermalTintPrefix = "thermal:";

    /// <summary>brief-em3d-90 — what a tint is called where a user reads it (B's readout, the Inspector's heading):
    /// <c>Thermal boundary die/zmin</c>, or <c>Boundary lid/zmax</c> for an EM face boundary.</summary>
    public static string TintLabel(string sceneName)
    {
        string rest = sceneName.StartsWith(FaceTintPrefix, StringComparison.Ordinal) ? sceneName[FaceTintPrefix.Length..] : sceneName;
        return rest.StartsWith(ThermalTintPrefix, StringComparison.Ordinal) ? "Thermal boundary " + rest[ThermalTintPrefix.Length..] : "Boundary " + rest;
    }

    /// <summary>Opacity of a face boundary's tint.</summary>
    public const byte BoundaryTintAlpha = 110;

    /// <summary>Hatch lines across a rectangle (corners in order), as point pairs: a symmetry face, which states neither
    /// wall. brief-em3d-90 — the editor draws a declared thermal symmetry plane with the same hatch, so the two read as one
    /// kind of thing.</summary>
    public static List<Point3> Hatch(Point3[] c)
    {
        var lines = new List<Point3>();
        const int n = 12;
        for (int k = 1; k < 2 * n; k++)
        {
            double t = (double)k / n;
            // Diagonals of slope 1 in the face's own (s, u) frame: from edge 0→1 or 1→2 to edge 0→3 or 3→2.
            Point3 On(Point3 a, Point3 b, double f) => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f);
            var p = t <= 1 ? On(c[0], c[1], t) : On(c[1], c[2], t - 1);
            var q = t <= 1 ? On(c[0], c[3], t) : On(c[3], c[2], t - 1);
            lines.Add(p);
            lines.Add(q);
        }
        return lines;
    }

    private static List<Point3> BoxEdges(Em3dAirBox b)
    {
        var (lo, hi) = (b.Min, b.Max);
        Point3 C(int k) => new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z);
        var e = new List<Point3>(24);
        for (int k = 0; k < 8; k++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((k & bit) == 0) { e.Add(C(k)); e.Add(C(k | bit)); }
        return e;
    }

    // ── accumulation ─────────────────────────────────────────────────────────────────────────

    /// <summary>The face a triangle of an unnamed-face primitive (a sweep, a sphere) carries: one face, and
    /// no feature edges drawn for it.</summary>
    public const int FaceUnknown = 0xFFFE;

    private sealed class Accumulator(Func<Point3, Vector3> local)
    {
        /// <summary>brief-em3d-90 — how far the face tints were lifted off their faces (metres), or 0 with none.</summary>
        public float TintLift;

        private readonly List<Scene3DObject> _objects = [];
        private readonly List<Scene3DVertex> _verts = [];
        /// <summary>brief-em3d-104 — parallel to <see cref="_verts"/>, always.</summary>
        private readonly List<Scene3DShadeVertex> _shade = [];
        /// <summary>brief-em3d-105 — parallel to <see cref="_objects"/> (the ones that own geometry): each one's look, or null.</summary>
        private readonly List<Scene3DLook?> _looks = [];
        public bool SplitShading { get; init; } = true;
        private readonly List<uint[]> _objIndices = [];
        /// <summary>brief-em3d-138 — parallel to <see cref="_objIndices"/>: how many of its triangles, at the end, are free walls.</summary>
        private readonly List<int> _objFree = [];
        private readonly List<Scene3DVertex> _lines = [];
        private readonly List<Scene3DLineBatch> _lineBatches = [];
        private readonly List<(uint Id, List<Scene3DVertex> Lines)> _edges = [];
        private readonly List<Scene3DFeatureRef> _features = [];
        private readonly List<(int ProtoIndex, int Count)> _groups = [];
        private readonly List<(int Group, int Element, int Proto, string Name, double Dx, double Dy, double Dz)> _deferred = [];

        /// <summary>The object-list index of the object added last.</summary>
        public int LastIndex => _objects.Count - 1;

        private readonly List<Scene3DTexture> _images = [];
        private readonly Dictionary<Scene3DTexture, int> _imageIndex = new(ReferenceEqualityComparer.Instance);
        private readonly List<Scene3DImageVertex> _imageVerts = [];
        private readonly List<Scene3DImageBatch> _imageBatches = [];
        public readonly Dictionary<string, C3dPlacedImage> PlacedImages = new(StringComparer.Ordinal);
        public readonly List<Scene3DPlacedFaceImage> PlacedFaceImages = [];
        public readonly Dictionary<string, string> FaceImageProblems = new(StringComparer.Ordinal);

        /// <summary>brief-em3d-101 — object <paramref name="id"/>'s image: <paramref name="mesh"/>'s triangles (world metres) with
        /// their texture coordinates in <paramref name="placed"/>'s frame, drawn with <paramref name="texture"/>.</summary>
        public void Image(uint id, Scene3DTexture texture, Em3dTriangleMesh mesh, C3dPlacedImage placed, uint rgba, bool translucent,
                          bool surface, int face, bool onFace = false, Func<Em3dTriangle, bool>? only = null, Func<Point3, Point3>? lift = null)
        {
            if (!_imageIndex.TryGetValue(texture, out int tex))
            {
                tex = _images.Count;
                _images.Add(texture);
                _imageIndex[texture] = tex;
            }
            int first = _imageVerts.Count;
            void Corner(Point3 p)
            {
                var (u, v) = placed.At(p);
                var q = local(lift is null ? p : lift(p));
                _imageVerts.Add(new Scene3DImageVertex(q.X, q.Y, q.Z, (float)u, (float)(1 - v), id, (uint)face, rgba));
            }
            foreach (var t in mesh.Triangles)
            {
                if (only is not null && !only(t)) continue;
                Corner(mesh.Vertices[t.A]); Corner(mesh.Vertices[t.B]); Corner(mesh.Vertices[t.C]);
            }
            if (_imageVerts.Count > first)
                _imageBatches.Add(new Scene3DImageBatch(id, tex, first, _imageVerts.Count - first, translucent, surface, onFace));
        }

        public int NewGroup(int protoIndex, int count)
        {
            _groups.Add((protoIndex, count));
            return _groups.Count - 1;
        }

        /// <summary>An element object, made in <see cref="Finish"/> after every object that owns geometry.</summary>
        public void Defer(int group, int element, int proto, string name, double dx, double dy, double dz)
            => _deferred.Add((group, element, proto, name, dx, dy, dz));

        /// <summary>
        /// A kernel solid's feature edges: the B-rep's own, each between two different faces, not runs found in its tessellation. The
        /// tessellation meshes every face on its own, so where a sloppy solid's two faces do not share their points along an edge (or
        /// a degenerate face meshes to nothing) no two triangles meet there, and the edge would be missing from the drawing.
        /// </summary>
        private void KernelEdges(Em3dShapeSolid shape, List<Scene3DVertex> edges, uint id, uint rgba)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < shape.Faces.Count; i++) index.TryAdd(shape.Faces[i].Name, i);
            foreach (var e in shape.Edges)
            {
                if (e.FaceA == e.FaceB || e.Polyline.Count == 0) continue;           // a seam: one face on both sides
                int f0 = index.GetValueOrDefault(e.FaceA, -1), f1 = index.GetValueOrDefault(e.FaceB, -1);
                uint packed = (uint)(f0 & 0xFFFF) | ((uint)(f1 & 0xFFFF) << 16);
                var line = e.Polyline;
                int n = e.Closed && line.Count > 2 && line[0] != line[^1] ? line.Count + 1 : line.Count;
                for (int k = 1; k < n; k++)
                {
                    var a = local(line[k - 1]); var b = local(line[k % line.Count]);
                    edges.Add(new Scene3DVertex(a.X, a.Y, a.Z, id, rgba, packed));
                    edges.Add(new Scene3DVertex(b.X, b.Y, b.Z, id, rgba, packed));
                }
            }
        }

        /// <summary><paramref name="faces"/>: tag each vertex with its triangle's face (un-welding a vertex
        /// shared by two faces) and collect the feature edges. <paramref name="sheet"/>: the whole mesh is
        /// face 0.</summary>
        /// <paramref name="wireEdges"/>: also draw the feature edges, always, in that colour — a wireframe object.
        /// <paramref name="freeTriangles"/>: brief-em3d-138 — the mesh's last triangles: a slab's wall round a conductor lying on it (Scene3DWalls),
        /// a batch of their own; <paramref name="edgeMesh"/>: the surface the feature edges are found on, when not the mesh's own.
        public void Object(Scene3DObject o, Em3dTriangleMesh? mesh, IEnumerable<(Point3 P, uint Rgba)>? lines = null,
                           bool faces = false, bool sheet = false, Scene3DFeatureRef features = default, uint? wireEdges = null,
                           IReadOnlyList<uint>? vertexRgba = null, Scene3DLook? look = null, Em3dShapeSolid? kernelEdges = null,
                           int freeTriangles = 0, Em3dTriangleMesh? edgeMesh = null)
        {
            _features.Add(features);
            _looks.Add(look);
            uint id = (uint)(_objects.Count + 1);
            var obj = new Scene3DObject
            {
                Id = id, Name = o.Name, Kind = o.Kind, Role = o.Role, Material = o.Material, MaterialValues = o.MaterialValues,
                MaterialSlot = o.MaterialSlot, Rgba = o.Rgba, Translucent = o.Translucent,
                InitiallyVisible = o.InitiallyVisible, PortNumber = o.PortNumber, Boundary = o.Boundary,
                FaceNames = o.FaceNames, CapCentres = o.CapCentres, Context = o.Context, PickLast = o.PickLast,
                Wireframe = o.Wireframe, Tint = o.Tint, Transparency = o.Transparency, Underlay = o.Underlay, ImageInFront = o.ImageInFront, ImageName = o.ImageName,
            };
            var min = new Vector3(float.MaxValue);
            List<Scene3DVertex>? featureEdges = null;
            var max = new Vector3(float.MinValue);
            uint[] idx = [];
            obj.FirstVertex = _verts.Count;
            if (mesh is not null && !faces)
            {
                int first = _verts.Count;
                for (int k = 0; k < mesh.Vertices.Count; k++)
                {
                    var q = local(mesh.Vertices[k]);
                    min = Vector3.Min(min, q); max = Vector3.Max(max, q);
                    // 3D editor bugs round 9 — a vertex's own colour when the caller gives one (a lumped port's checkerboard).
                    _verts.Add(new Scene3DVertex(q.X, q.Y, q.Z, id, vertexRgba?[k] ?? o.Rgba));
                }
                idx = new uint[mesh.Triangles.Count * 3];
                int w = 0;
                foreach (var t in mesh.Triangles)
                {
                    idx[w++] = (uint)(first + t.A);
                    idx[w++] = (uint)(first + t.B);
                    idx[w++] = (uint)(first + t.C);
                }
            }
            else if (mesh is not null)
            {
                // One scene vertex per (mesh vertex, face): a vertex's face must be its triangle's.
                var at = new Dictionary<(int V, int F), uint>();
                var q = new Vector3[mesh.Vertices.Count];
                for (int k = 0; k < q.Length; k++) { q[k] = local(mesh.Vertices[k]); min = Vector3.Min(min, q[k]); max = Vector3.Max(max, q[k]); }
                uint V(int v, int f)
                {
                    if (at.TryGetValue((v, f), out uint i)) return i;
                    i = (uint)_verts.Count;
                    _verts.Add(new Scene3DVertex(q[v].X, q[v].Y, q[v].Z, id, o.Rgba, (uint)f));
                    at[(v, f)] = i;
                    return i;
                }
                idx = new uint[mesh.Triangles.Count * 3];
                int w = 0;
                // Each undirected edge's faces, in first-seen order, for the feature edges. Keyed by the corners WELDED BY
                // POSITION, as Scene3DFeatureTable welds them (3D editor bugs round 2): a polyhedron's tessellation repeats
                // each corner once per face, so by index no two faces ever shared an edge and a polyhedron had no edges at
                // all — a material-less one, drawn as its edges only, vanished the moment a box became one.
                var weld = new Dictionary<Point3, int>();
                var welded = new int[mesh.Vertices.Count];
                for (int k = 0; k < welded.Length; k++)
                {
                    if (!weld.TryGetValue(mesh.Vertices[k], out int wk)) weld[mesh.Vertices[k]] = wk = k;
                    welded[k] = wk;
                }
                var edgeFaces = new Dictionary<(int, int), (int F0, int F1, int Count)>();
                void Edge(int[] welded, int a, int c, int f)
                {
                    a = welded[a]; c = welded[c];
                    var key = a < c ? (a, c) : (c, a);
                    edgeFaces[key] = edgeFaces.TryGetValue(key, out var e) ? (e.F0, e.Count == 1 ? f : e.F1, e.Count + 1) : (f, -1, 1);
                }
                // The scene vertex is keyed by the WELDED corner too: the kernel tessellates a periodic face (a cylinder, a torus
                // fillet) with its seam's vertices repeated, and unwelded the shading normals took the seam for an open edge — a
                // line drawn down the face in the realistic view.
                foreach (var t in mesh.Triangles)
                {
                    int f = sheet ? 0 : t.Face;
                    if (f < 0) f = FaceUnknown;
                    idx[w++] = V(welded[t.A], f);
                    idx[w++] = V(welded[t.B], f);
                    idx[w++] = V(welded[t.C], f);
                    if (edgeMesh is null) { Edge(welded, t.A, t.B, f); Edge(welded, t.B, t.C, f); Edge(welded, t.C, t.A, f); }
                }
                // brief-em3d-138 — a carved slab's edges are its uncarved surface's: the rim round a conductor lying on its wall is no
                // edge of the slab, and the cap's rim, now meeting wall pieces split where the conductor ends, still is.
                if (edgeMesh is not null)
                {
                    weld.Clear();
                    var welds = new int[edgeMesh.Vertices.Count];
                    for (int k = 0; k < welds.Length; k++)
                    {
                        if (!weld.TryGetValue(edgeMesh.Vertices[k], out int wk)) weld[edgeMesh.Vertices[k]] = wk = k;
                        welds[k] = wk;
                    }
                    foreach (var t in edgeMesh.Triangles)
                    {
                        int f = t.Face < 0 ? FaceUnknown : t.Face;
                        Edge(welds, t.A, t.B, f); Edge(welds, t.B, t.C, f); Edge(welds, t.C, t.A, f);
                    }
                    q = [.. edgeMesh.Vertices.Select(local)];
                }
                var edges = new List<Scene3DVertex>();
                if (kernelEdges is not null) KernelEdges(kernelEdges, edges, id, o.Rgba);
                else foreach (var ((a, c), (f0, f1, count)) in edgeFaces)
                {
                    if (f0 == FaceUnknown) continue;                            // a sweep or a sphere: no named faces
                    bool feature = count == 1 ? sheet : f0 != f1;               // a sheet's rim; where two faces meet
                    if (!feature) continue;
                    uint packed = (uint)(f0 & 0xFFFF) | ((uint)((count == 1 ? 0xFFFF : f1) & 0xFFFF) << 16);
                    edges.Add(new Scene3DVertex(q[a].X, q[a].Y, q[a].Z, id, o.Rgba, packed));
                    edges.Add(new Scene3DVertex(q[c].X, q[c].Y, q[c].Z, id, o.Rgba, packed));
                }
                if (edges.Count > 0) _edges.Add((id, edges));
                featureEdges = edges;
            }
            if (mesh is not null) idx = Shade(obj.FirstVertex, idx);
            obj.VertexCount = _verts.Count - obj.FirstVertex;
            _objFree.Add(mesh is null ? 0 : Math.Clamp(freeTriangles, 0, idx.Length / 3));
            if (lines is not null || (wireEdges is not null && featureEdges is { Count: > 0 }))
            {
                int first = _lines.Count;
                foreach (var (p, rgba) in lines ?? [])
                {
                    var q = local(p);
                    min = Vector3.Min(min, q); max = Vector3.Max(max, q);
                    _lines.Add(new Scene3DVertex(q.X, q.Y, q.Z, id, rgba));
                }
                if (wireEdges is uint wr && featureEdges is not null)
                    foreach (var e in featureEdges) _lines.Add(new Scene3DVertex(e.X, e.Y, e.Z, id, wr));
                if (_lines.Count > first) _lineBatches.Add(new Scene3DLineBatch(id, first, _lines.Count - first));
            }
            if (min.X > max.X) { min = max = Vector3.Zero; }
            obj.Min = min; obj.Max = max;
            _objects.Add(obj);
            _objIndices.Add(idx);
        }

        /// <summary>brief-em3d-104 R-em3d104-1e / 2c — the shade vertices of the object whose vertices start at <paramref name="first"/>:
        /// each split vertex is copied (position, id, colour and face identical) and its triangles' indices moved to the copy.
        /// Returns the indices to keep.</summary>
        private uint[] Shade(int first, uint[] idx)
        {
            int count = _verts.Count - first;
            if (idx.Length == 0)
            {
                for (int k = 0; k < count; k++) _shade.Add(new Scene3DShadeVertex(0, 0, 1));
                return idx;
            }
            var pos = new Vector3[count];
            for (int k = 0; k < count; k++) { var v = _verts[first + k]; pos[k] = new Vector3(v.X, v.Y, v.Z); }
            // Each face's vertices welded to the other faces' at the same point, so a tangent edge between faces shades smooth.
            int[]? weld = null;
            if (SplitShading)
            {
                weld = new int[count];
                var at = new Dictionary<Vector3, int>();
                for (int k = 0; k < count; k++)
                {
                    if (!at.TryGetValue(pos[k], out int w)) at[pos[k]] = w = k;
                    weld[k] = w;
                }
            }
            var r = ShadingNormals.Compute(pos, idx, (uint)first, SplitShading, weld);
            foreach (int d in r.Duplicates) _verts.Add(_verts[first + d]);
            foreach (var n in r.Normals) _shade.Add(new Scene3DShadeVertex(n.X, n.Y, n.Z));
            return r.Indices;
        }

        /// <summary>
        /// brief-em3d-105 R-em3d105-4 — the appearance table, and each object's row in it written onto the object and its shade
        /// vertices. Equal appearances share a row. Past <see cref="AppearanceSlots"/> distinct ones, the role defaults go in first
        /// and every object's own after them while there is room; an object whose own did not fit draws with its role's default,
        /// and is counted.
        /// </summary>
        private (AppearanceValues[] Table, int Fallbacks) Appearances()
        {
            var (table, slots, fallbacks) = Scene3DLooks.Intern(_looks);
            for (int k = 0; k < _looks.Count; k++)
            {
                if (_looks[k] is null) continue;
                var o = _objects[k];
                o.AppearanceSlot = slots[k];
                // brief-em3d-106 R-em3d106-2e (overview D12) — whether the vertex colour's alpha is a STATED coverage (a document's
                // Transparency, or a dimmed context part): the realistic shader multiplies it on; a kind default it does not.
                uint stated = o.Transparency is not null || o.Context ? Scene3DShadeVertex.StatedAlpha : 0;
                uint closed = o.Kind is Scene3DKind.Sheet or Scene3DKind.Port or Scene3DKind.Boundary ? 0 : Scene3DShadeVertex.Closed;
                for (int v = o.FirstVertex; v < o.FirstVertex + o.VertexCount; v++)
                {
                    var sv = _shade[v];
                    sv.Slot = (uint)slots[k] | stated | closed;
                    _shade[v] = sv;
                }
            }
            return (table, fallbacks);
        }

        public Scene3DModel Finish(long generation, (double, double, double) origin, Em3dProblem problem, IReadOnlyList<string>? notes)
        {
            var (appearances, fallbacks) = Appearances();
            var indices = new uint[_objIndices.Sum(i => i.Length)];
            var batches = new List<Scene3DBatch>();
            int at = 0;
            foreach (bool translucent in new[] { false, true })
                for (int k = 0; k < _objects.Count; k++)
                {
                    var o = _objects[k];
                    if (o.Translucent != translucent || _objIndices[k].Length == 0) continue;
                    _objIndices[k].CopyTo(indices, at);
                    // brief-em3d-138 — a slab's free walls (Scene3DWalls), last in its triangles, are a batch of their own right after it
                    int length = _objIndices[k].Length, free = _objFree[k] * 3;
                    if (length > free) batches.Add(new Scene3DBatch(o.Id, o.MaterialSlot, at, length - free, translucent));
                    if (free > 0) batches.Add(new Scene3DBatch(o.Id, o.MaterialSlot, at + length - free, free, translucent, Free: true));
                    at += length;
                }

            var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
            var cmin = new Vector3(float.MaxValue); var cmax = new Vector3(float.MinValue);
            foreach (var o in _objects)
            {
                bmin = Vector3.Min(bmin, o.Min); bmax = Vector3.Max(bmax, o.Max);
                if (o.Kind is Scene3DKind.Air or Scene3DKind.Boundary) continue;
                if (o.Kind == Scene3DKind.Dielectric && !o.InitiallyVisible) continue;
                cmin = Vector3.Min(cmin, o.Min); cmax = Vector3.Max(cmax, o.Max);
            }
            // brief-em3d-48 — the unit cube an element over the triangle budget is drawn as, after every ordinary line.
            Scene3DLineBatch unitBox = default;
            if (_deferred.Count > 0)
            {
                int first = _lines.Count;
                uint grey = Scene3DVertex.Pack(150, 150, 160, 255);
                for (int k = 0; k < 8; k++)
                    for (int bit = 1; bit <= 4; bit <<= 1)
                        if ((k & bit) == 0)
                        {
                            int j = k | bit;
                            _lines.Add(new Scene3DVertex(k & 1, (k >> 1) & 1, (k >> 2) & 1, 0, grey));
                            _lines.Add(new Scene3DVertex(j & 1, (j >> 1) & 1, (j >> 2) & 1, 0, grey));
                        }
                unitBox = new Scene3DLineBatch(0, first, _lines.Count - first);
            }

            // The feature edges follow every ordinary line, so LineBatches' offsets are what they were.
            var edgeBatches = new List<Scene3DLineBatch>(_edges.Count);
            var edgeOf = new Dictionary<uint, int>();
            foreach (var (id, lines) in _edges)
            {
                edgeOf[id] = edgeBatches.Count;
                edgeBatches.Add(new Scene3DLineBatch(id, _lines.Count, lines.Count));
                _lines.AddRange(lines);
            }

            // brief-em3d-48 — the prototypes, then every element's objects and batches, AFTER everything that owns bytes.
            int ownedObjects = _objects.Count, ownedBatches = batches.Count, ownedEdges = edgeBatches.Count;
            // an object's batches (two for a slab with free walls, adjacent) as their first index and count
            var batchOf = new Dictionary<uint, (int First, int Count)>();
            for (int k = 0; k < batches.Count; k++)
                batchOf[batches[k].ObjectId] = batchOf.TryGetValue(batches[k].ObjectId, out var run) ? (run.First, run.Count + 1) : (k, 1);
            var groups = new List<Scene3DInstanceGroup>();
            foreach (var (protoIndex, count) in _groups)
            {
                uint firstId = (uint)(protoIndex + 1);
                int opaqueFirst = -1, opaqueCount = 0, tris = 0;
                var gmin = new Vector3(float.MaxValue); var gmax = new Vector3(float.MinValue);
                for (int k = 0; k < count; k++)
                {
                    var o = _objects[protoIndex + k];
                    gmin = Vector3.Min(gmin, o.Min); gmax = Vector3.Max(gmax, o.Max);
                    if (!batchOf.TryGetValue(o.Id, out var run)) continue;
                    for (int bi = run.First; bi < run.First + run.Count; bi++)
                    {
                        var pb = batches[bi];
                        tris += pb.IndexCount / 3;
                        if (pb.Translucent) continue;
                        if (opaqueFirst == -1) { opaqueFirst = pb.FirstIndex; opaqueCount = pb.IndexCount; }
                        else if (opaqueFirst >= 0 && opaqueFirst + opaqueCount == pb.FirstIndex) opaqueCount += pb.IndexCount;
                        else opaqueFirst = -2;
                    }
                }
                if (opaqueFirst < 0) opaqueCount = opaqueFirst == -1 ? 0 : -1;
                groups.Add(new Scene3DInstanceGroup(firstId, count, Math.Max(opaqueFirst, 0), opaqueCount, tris, gmin, gmax));
            }
            var elements = new List<Scene3DElement>();
            int at2 = 0;
            while (at2 < _deferred.Count)
            {
                var (group, element, _, _, dx, dy, dz) = _deferred[at2];
                int end = at2;
                while (end < _deferred.Count && _deferred[end].Group == group && _deferred[end].Element == element) end++;
                var offset = new Vector3((float)dx, (float)dy, (float)dz);
                uint firstId = (uint)(_objects.Count + 1);
                int firstBatch = batches.Count, index = elements.Count;
                for (int k = at2; k < end; k++)
                {
                    var d = _deferred[k];
                    var proto = _objects[d.Proto];
                    uint id = (uint)(_objects.Count + 1);
                    var obj = new Scene3DObject
                    {
                        Id = id, Name = d.Name, Kind = proto.Kind, Role = proto.Role, Material = proto.Material, MaterialValues = proto.MaterialValues,
                        MaterialSlot = proto.MaterialSlot, Rgba = proto.Rgba, Translucent = proto.Translucent,
                        InitiallyVisible = proto.InitiallyVisible, FaceNames = proto.FaceNames, Context = proto.Context,
                        Wireframe = proto.Wireframe, Transparency = proto.Transparency, AppearanceSlot = proto.AppearanceSlot,
                        CapCentres = proto.CapCentres?.Select(c => new Point3(c.X + d.Dx, c.Y + d.Dy, c.Z + d.Dz)).ToArray(),
                        Element = index, Prototype = proto.Id,
                        FirstVertex = proto.FirstVertex, VertexCount = proto.VertexCount,
                        Min = proto.Min + offset, Max = proto.Max + offset,
                    };
                    _objects.Add(obj);
                    var f = _features[d.Proto];
                    _features.Add(f.Table is null ? f : new Scene3DFeatureRef(f.Table, f.Dx + d.Dx, f.Dy + d.Dy, f.Dz + d.Dz, true));
                    if (batchOf.TryGetValue(proto.Id, out var run))
                        for (int bi = run.First; bi < run.First + run.Count; bi++)
                            batches.Add(batches[bi] with { ObjectId = id, Element = index, Offset = offset });
                    if (edgeOf.TryGetValue(proto.Id, out int ei))
                        edgeBatches.Add(edgeBatches[ei] with { ObjectId = id, Element = index, Offset = offset });
                    bmin = Vector3.Min(bmin, obj.Min); bmax = Vector3.Max(bmax, obj.Max);
                    if (obj.Kind is not (Scene3DKind.Air or Scene3DKind.Boundary) && !(obj.Kind == Scene3DKind.Dielectric && !obj.InitiallyVisible))
                    { cmin = Vector3.Min(cmin, obj.Min); cmax = Vector3.Max(cmax, obj.Max); }
                }
                uint protoFirst = groups[group].FirstId;
                elements.Add(new Scene3DElement(group, firstId, end - at2, firstBatch, batches.Count - firstBatch, offset, firstId - protoFirst));
                at2 = end;
            }

            if (bmin.X > bmax.X) { bmin = new Vector3(-1e-3f); bmax = new Vector3(1e-3f); }
            if (cmin.X > cmax.X) { cmin = bmin; cmax = bmax; }

            return new Scene3DModel
            {
                Generation = generation, Origin = origin,
                Vertices = [.. _verts], ShadeVertices = [.. _shade], Indices = indices, LineVertices = [.. _lines],
                Objects = [.. _objects], Batches = [.. batches], LineBatches = [.. _lineBatches],
                EdgeBatches = [.. edgeBatches],
                Features = [.. _features],
                Groups = [.. groups], Elements = [.. elements],
                GeometryObjectCount = ownedObjects, GeometryBatchCount = ownedBatches, GeometryEdgeBatchCount = ownedEdges,
                UnitBox = unitBox,
                BoundsMin = bmin, BoundsMax = bmax, ContentMin = cmin, ContentMax = cmax,
                Problem = problem, Notes = notes ?? [], TintLift = TintLift,
                Images = [.. _images], ImageVertices = [.. _imageVerts], ImageBatches = [.. _imageBatches], PlacedImages = PlacedImages,
                PlacedFaceImages = [.. PlacedFaceImages], FaceImageProblems = FaceImageProblems,
                Appearances = appearances, AppearanceFallbacks = fallbacks,
                // brief-em3d-108 — each owned object's question, so a look can be asked again without this builder
                AppearanceRequests = [.. _looks.Take(ownedObjects).Select(l => l?.Request)],
            };
        }
    }
}
