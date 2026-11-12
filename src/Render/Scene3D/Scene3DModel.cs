// brief-em3d-28 R-em3d28-1a / R-em3d28-2b — the scene a 3D view draws: flat vertex and index arrays,
// object IDs, material slots, visibility defaults and a generation number. No GPU API, no Avalonia.
//
// ONE VERTEX BUFFER, ONE INDEX BUFFER, ONE LINE BUFFER for the whole scene, uploaded once per
// generation (§8.2 point 1). Each object's triangles are CONTIGUOUS in the index buffer, so an object
// is one draw whatever its triangle count: a draw per (object, material), never per face (gate 2). A
// solid has exactly one material, so in practice that is a draw per object.
//
// VERTICES ARE SCENE-LOCAL. Em3dProblem is in metres; a board is 0.1 m across and its features are a
// micrometre. Single precision at 0.1 m resolves ~6 nm, but only if the numbers are small — so every
// position is stored relative to Origin (the bounds' centre, in double), and the camera lives in the
// same frame.

using System.Numerics;
using System.Runtime.InteropServices;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

/// <summary>What an object in the tree is (R-em3d28-4c's grouping).</summary>
public enum Scene3DKind { Conductor, Dielectric, Air, Body, Wire, Via, Sheet, Port, Boundary, Excitation }

/// <summary>A vertex, 24 bytes: position (scene-local metres), the object's ID, its colour (RGBA8), and
/// the face of that object it lies on (brief-em3d-43 R-em3d43-3a) — the second half of the ID pass's
/// (object, face) pair. The face is last so every older attribute keeps its offset.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Scene3DVertex(float x, float y, float z, uint id, uint rgba, uint face = Scene3DVertex.NoFace)
{
    public const int Stride = 24;
    /// <summary>The face of a vertex on no face: a port's sheet, an arrow, the box's edges.</summary>
    public const uint NoFace = uint.MaxValue;
    public float X = x, Y = y, Z = z;
    public uint Id = id;
    /// <summary>R in the low byte, A in the high byte — RGBA8 unorm in memory order.</summary>
    public uint Rgba = rgba;
    /// <summary>The face index (Em3dTriangle.Face), or <see cref="NoFace"/>. On an EDGE line vertex it is
    /// the edge's two faces packed: the low 16 bits one, the high 16 the other.</summary>
    public uint Face = face;

    public static uint Pack(byte r, byte g, byte b, byte a) => (uint)(r | (g << 8) | (b << 16) | (a << 24));
}

/// <summary>
/// brief-em3d-104 R-em3d104-2a — a vertex of the SHADE stream, 16 bytes: the shading normal (scene-local, ShadingNormals) and the
/// object's appearance slot (overview D16; 0 until briefs 105/106 fill it). <see cref="Scene3DModel.ShadeVertices"/> is parallel
/// to <see cref="Scene3DModel.Vertices"/> — same length, same index — and only the realistic view's pipelines read it, so
/// <see cref="Scene3DVertex"/>'s 24-byte stride, which every other draw relies on, is unchanged.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Scene3DShadeVertex(float nx, float ny, float nz, uint slot = 0)
{
    public const int Stride = 16;
    /// <summary>brief-em3d-106 — <see cref="Slot"/>'s low 8 bits are the appearance row; this bit says the vertex colour's alpha is a
    /// STATED coverage (a Transparency, a dimmed context part) that the realistic view multiplies on (overview D12), not a kind's
    /// default, which the appearance's Transmission replaces. <see cref="Closed"/> says the object is a closed volume (not a sheet, a
    /// port or a boundary face): faded by an Object-mode selection, its far side is not drawn (scene.wgsl's pbr).</summary>
    public const uint StatedAlpha = 0x100, Closed = 0x200, SlotMask = 0xFF;
    public float Nx = nx, Ny = ny, Nz = nz;
    public uint Slot = slot;
}

/// <summary>
/// brief-em3d-101 R-em3d101-4a — a vertex of an IMAGE draw, 32 bytes: position (scene-local metres), the texture coordinate
/// (u right, v DOWN — a texture's rows run top first), the object's ID and face (what hover and selection compare, as on
/// <see cref="Scene3DVertex"/>), and a colour whose alpha multiplies the texture's (the object's transparency). A stream of its
/// own: <see cref="Scene3DVertex"/>'s 24-byte stride is what every other draw relies on.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Scene3DImageVertex(float x, float y, float z, float u, float v, uint id, uint face, uint rgba)
{
    public const int Stride = 32;
    public float X = x, Y = y, Z = z;
    public float U = u, V = v;
    public uint Id = id;
    public uint Face = face;
    /// <summary>R in the low byte, A in the high byte, as <see cref="Scene3DVertex.Rgba"/>: white, with the object's alpha.</summary>
    public uint Rgba = rgba;
}

/// <summary>
/// brief-em3d-101 — one object's image triangles: <see cref="VertexCount"/> vertices (a triangle list, not indexed) from
/// <see cref="FirstVertex"/> in <see cref="Scene3DModel.ImageVertices"/>, drawn with texture <see cref="Image"/>
/// (<see cref="Scene3DModel.Images"/>). A <see cref="Surface"/> batch IS its object's colour (an image sheet: its ordinary
/// triangles are drawn only in the ID pass and the selection's passes); otherwise it is drawn over its object's own face (brief 101
/// Phase B). <see cref="Translucent"/> when the object's alpha or any texel's is below 1.
/// </summary>
/// <para><see cref="OnFace"/> — an image mapped onto a face (Phase B): its draw ties <c>FaceImage</c>, over the face it lies on, where
/// an image sheet's ties <c>Underlay</c>, under everything on its plane (or <c>FaceImage</c> when it is in front).</para>
public readonly record struct Scene3DImageBatch(uint ObjectId, int Image, int FirstVertex, int VertexCount, bool Translucent, bool Surface,
                                                bool OnFace = false);

/// <summary>One object: a solid, a sheet, a port, a face of the air box, or the box's edges. IDs start
/// at 1; 0 is the background in the ID pass.</summary>
public sealed class Scene3DObject
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required Scene3DKind Kind { get; init; }
    /// <summary>The material's name, or null for a port, a face or the box's edges.</summary>
    public string? Material { get; init; }
    /// <summary>The material's values at the setup's operating temperature — what the tooltip shows.</summary>
    public Em3dMaterial? MaterialValues { get; init; }
    /// <summary>brief-em3d-94 — the role the elaborator gave it, which the solve uses (an object's Role override included): a
    /// solid's own, a sheet's conductor. Null for a port, a face or the box's edges. What the hover's material lines follow.</summary>
    public Em3dRole? Role { get; init; }
    /// <summary>Index into the problem's materials — the batch's material slot; −1 for none.</summary>
    public int MaterialSlot { get; init; } = -1;
    public required uint Rgba { get; init; }
    public bool Translucent { get; init; }
    /// <summary>R-em3d28-4c — air and the outermost dielectric start hidden.</summary>
    public bool InitiallyVisible { get; init; } = true;
    /// <summary>A port's number, or 0.</summary>
    public int PortNumber { get; init; }
    /// <summary>A face's boundary kind, for a <see cref="Scene3DKind.Boundary"/> object.</summary>
    public Em3dBoundaryKind? Boundary { get; init; }
    public Vector3 Min { get; set; }
    public Vector3 Max { get; set; }
    public Vector3 Centroid => (Min + Max) * 0.5f;

    /// <summary>brief-em3d-43 — its faces' names, by face index; empty when nothing named them.</summary>
    public IReadOnlyList<string> FaceNames { get; init; } = [];

    /// <summary>brief-em3d-47 R-em3d47-5 — a cylinder's two cap centres (world metres), which are all Vertex mode offers on
    /// it: its tessellation's vertices are not design. brief-em3d-102 R-em3d102-5d — a drawn sphere's one centre, likewise.
    /// Null for every other object.</summary>
    public Engine.Em3d.Point3[]? CapCentres { get; init; }

    /// <summary>brief-em3d-43 — its triangle vertices: <see cref="VertexCount"/> from <see cref="FirstVertex"/>
    /// in the scene's vertex buffer, contiguous (what a partial upload patches).</summary>
    public int FirstVertex { get; set; }
    public int VertexCount { get; set; }

    /// <summary>brief-em3d-48 R-em3d48-3a — an array element's object: which element (index into
    /// <see cref="Scene3DModel.Elements"/>, −1 for an object that owns its geometry) and the prototype object whose
    /// triangles it draws, moved by the element's offset. It owns no vertices: <see cref="FirstVertex"/> and
    /// <see cref="VertexCount"/> are the prototype's.</summary>
    public int Element { get; init; } = -1;
    public uint Prototype { get; init; }

    /// <summary>brief-em3d-48 R-em3d48-4a — part of the PARENT drawn around a pushed-in child: dimmed, snapped to, never
    /// hovered or selected.</summary>
    public bool Context { get; init; }

    /// <summary>3D editor bugs round 1 — an object with no material (or one its technology does not define): drawn as its
    /// feature edges only. Its triangles are still in the scene, fully transparent (alpha 0), so the ID pass, the CPU
    /// pick, the snap and the selection reach its faces as they reach any other object's; the shader fills a face only
    /// to show hover or a selected face.</summary>
    public bool Wireframe { get; init; }

    /// <summary>brief-em3d-92 — the transparency the document states for it (its own percentage and the opacity its instances
    /// multiply on), already packed into <see cref="Rgba"/>'s alpha; null when it states none. Kept so a vector export paints
    /// the object as the view does and knows which objects are see-through (Em3dDrawingRequest.TransparentObjectsOcclude).</summary>
    public Scene3DTransparency? Transparency { get; init; }

    /// <summary>brief-em3d-105 R-em3d105-4 — its row of <see cref="Scene3DModel.Appearances"/>, which its shade vertices carry too;
    /// -1 for what has no appearance (air, a port, a boundary).</summary>
    public int AppearanceSlot { get; set; } = -1;

    /// <summary>brief-em3d-108 — a copy (every field, shallow): what a restyled scene holds where this object's row moved, so the
    /// scene it came from keeps its own.</summary>
    internal Scene3DObject Copy() => (Scene3DObject)MemberwiseClone();

    /// <summary>The name of face <paramref name="face"/>: its stored name, else <c>face&lt;n&gt;</c>.</summary>
    public string FaceName(int face) => face >= 0 && face < FaceNames.Count ? FaceNames[face]
        : face == Scene3DBuilder.FaceUnknown ? "surface" : $"face{face}";

    /// <summary>Whether hover and click can land on it: everything but the air and the box's faces,
    /// which enclose everything else.</summary>
    public bool Pickable => Kind is not (Scene3DKind.Air or Scene3DKind.Boundary);

    /// <summary>
    /// brief-em3d-49 R-em3d49-3b — an air-box face in the editor: pickable in Face mode at the LOWEST priority. The GPU's
    /// ID pass never draws it (it encloses everything, so a near face would stand in front of every solid); the pane
    /// finds it on the CPU only where nothing pickable is under the cursor, and B reaches it after every solid face.
    /// </summary>
    public bool PickLast { get; init; }

    /// <summary>
    /// brief-em3d-90 R-em3d90-3 — a face boundary's tint (an EM face boundary, or a thermal setup's), drawn just off the face it
    /// conditions. It is NOT <see cref="Pickable"/>: the GPU's ID pass never draws it, so the snap (which reads that pass) still
    /// lands on the face beneath. Hover, click and B reach it on the CPU instead, and in FRONT of the face it lies on
    /// (Scene3DPicking.TintHits, RayHits.Collect): the boundary is what a click on it selects, and B steps to the solid.
    /// </summary>
    public bool Tint { get; init; }

    /// <summary>brief-em3d-101 R-em3d101-4b — an image sheet: drawn with its image, and giving way to EVERY face lying on its
    /// plane (<c>Scene3DDepthTie.Underlay</c>), so a polygon traced on the image's own plane is drawn over it and is what a
    /// click finds.</summary>
    public bool Underlay { get; init; }

    /// <summary>An image sheet whose image is drawn OVER the faces on its plane (C3dImage.InFront): it ties
    /// <c>Scene3DDepthTie.FaceImage</c> instead of <c>Underlay</c> — a picture placed on a solid's face.</summary>
    public bool ImageInFront { get; init; }

    /// <summary>brief-em3d-101 — the image's file name (<c>die.png</c>), for the hover; null for an object with none.</summary>
    public string? ImageName { get; init; }

    /// <summary>Whether a hover or a click may land on it: pickable, and not the dimmed parent around a pushed-in child
    /// (which the ID pass still draws, so the snap reaches it).</summary>
    public bool Selectable => Pickable && !Context;
}

/// <summary>An object's triangles: <see cref="IndexCount"/> indices from <see cref="FirstIndex"/>.</summary>
/// <para>brief-em3d-48 — an array element's object draws its prototype's range: <see cref="Element"/> says which element
/// (−1 for none) and <see cref="Offset"/> is that element's translation, scene-local metres, which every reader of the
/// vertex buffer adds (the GPU through the element's per-draw transform).</para>
/// <para>brief-em3d-138 — <see cref="Free"/>: a slab's outline walls rebuilt round a conductor lying on them (Scene3DWalls). They
/// coincide with nothing, so they draw with no polygon offset (<see cref="Scene3DFramePlan.TieOf(Scene3DModel, in Scene3DBatch)"/>);
/// such a batch follows its object's other one directly.</para>
public readonly record struct Scene3DBatch(uint ObjectId, int MaterialSlot, int FirstIndex, int IndexCount, bool Translucent,
                                           int Element = -1, Vector3 Offset = default, bool Free = false);

/// <summary>An object's lines (a port's arrow, the box's edges): a line list of
/// <see cref="VertexCount"/> vertices from <see cref="FirstVertex"/> in the line buffer.</summary>
public readonly record struct Scene3DLineBatch(uint ObjectId, int FirstVertex, int VertexCount, int Element = -1, Vector3 Offset = default);

/// <summary>
/// brief-em3d-48 R-em3d48-3a — one array element (or repeated placement) drawn from a prototype's geometry: its
/// objects are <see cref="Count"/> ids from <see cref="FirstId"/>, each the prototype's object at the same position,
/// and its batches are <see cref="BatchCount"/> from <see cref="FirstBatch"/>. The GPU draws it with one per-draw
/// transform: the translation <see cref="Offset"/> and an ID offset <see cref="IdOffset"/> added to every vertex's id,
/// so the ID pass writes the ELEMENT's object id — what makes a pick name the element (R-em3d48-3b).
/// </summary>
public readonly record struct Scene3DElement(int Group, uint FirstId, int Count, int FirstBatch, int BatchCount, Vector3 Offset, uint IdOffset);

/// <summary>
/// brief-em3d-48 — a prototype and the elements drawn from it: the prototype's objects are <see cref="Count"/> ids
/// from <see cref="FirstId"/> (it is element 0, drawn like any object); its opaque triangles are the contiguous range
/// <see cref="OpaqueFirst"/>..+<see cref="OpaqueCount"/> (−1 when they are not contiguous, and then an element is
/// drawn object by object); <see cref="Min"/>/<see cref="Max"/> bound it, scene-local.
/// </summary>
public readonly record struct Scene3DInstanceGroup(uint FirstId, int Count, int OpaqueFirst, int OpaqueCount, int Triangles,
                                                   Vector3 Min, Vector3 Max);

/// <summary>The scene. Immutable once built; a new generation is a new instance.</summary>
public sealed class Scene3DModel
{
    /// <summary>The regeneration number this scene was built for (R-em3d28-2d).</summary>
    public required long Generation { get; init; }
    /// <summary>World position of the scene-local origin, metres.</summary>
    public required (double X, double Y, double Z) Origin { get; init; }
    public required Scene3DVertex[] Vertices { get; init; }
    /// <summary>brief-em3d-104 R-em3d104-2a — each vertex's shading normal and appearance slot, parallel to <see cref="Vertices"/>
    /// (a scene built by Scene3DBuilder always has one per vertex). Uploaded only while the realistic view is on.</summary>
    public Scene3DShadeVertex[] ShadeVertices { get => _shadeVertices; init => _shadeVertices = value; }
    private Scene3DShadeVertex[] _shadeVertices = [];
    public required uint[] Indices { get; init; }
    public required Scene3DVertex[] LineVertices { get; init; }
    public required Scene3DObject[] Objects { get => _objects; init => _objects = value; }
    private Scene3DObject[] _objects = [];
    /// <summary>Opaque batches first, then translucent — each one object.</summary>
    public required Scene3DBatch[] Batches { get; init; }
    public required Scene3DLineBatch[] LineBatches { get; init; }
    /// <summary>brief-em3d-43 R-em3d43-5 — each object's feature edges (where two of its faces meet), in the
    /// line buffer after <see cref="LineBatches"/>' lines. Drawn only for what is selected.</summary>
    public Scene3DLineBatch[] EdgeBatches { get; init; } = [];
    /// <summary>brief-em3d-44 R-em3d44-3 — each object's snap features, by ID − 1 (a port and the air box's
    /// faces have none). Elements of one instance share a table and differ by an offset.</summary>
    public Edit.Scene3DFeatureRef[] Features { get; init; } = [];

    /// <summary>brief-em3d-48 R-em3d48-3a — the prototypes and their elements. The objects that OWN geometry come first
    /// (<see cref="GeometryObjectCount"/> of them, and <see cref="GeometryBatchCount"/> batches, and
    /// <see cref="GeometryEdgeBatchCount"/> edge batches); every element's objects and batches follow, so adding an
    /// element renumbers nothing that is in a buffer.</summary>
    public Scene3DInstanceGroup[] Groups { get; init; } = [];
    public Scene3DElement[] Elements { get; init; } = [];
    public int GeometryObjectCount { get; init; } = -1;
    public int GeometryBatchCount { get; init; } = -1;
    public int GeometryEdgeBatchCount { get; init; } = -1;

    /// <summary>The unit cube's twelve edges in the line buffer — what an element beyond the triangle budget is drawn as
    /// (R-em3d48-3c), scaled and moved by its own transform. Empty when there are no elements.</summary>
    public Scene3DLineBatch UnitBox { get; init; }

    /// <summary>Objects that own geometry — all of them unless elements follow.</summary>
    public int OwnedObjects => GeometryObjectCount >= 0 ? GeometryObjectCount : Objects.Length;
    public int OwnedBatches => GeometryBatchCount >= 0 ? GeometryBatchCount : Batches.Length;
    public int OwnedEdgeBatches => GeometryEdgeBatchCount >= 0 ? GeometryEdgeBatchCount : EdgeBatches.Length;

    /// <summary>A vertex of object <paramref name="o"/> where it is drawn: the buffer's position plus the element's offset.</summary>
    public Vector3 Position(Scene3DObject o, int vertex)
    {
        var v = Vertices[vertex];
        var p = new Vector3(v.X, v.Y, v.Z);
        return o.Element >= 0 ? p + Elements[o.Element].Offset : p;
    }

    /// <summary>The triangles the scene DRAWS — every owned batch's, and every element's under its offset.</summary>
    public long DrawnTriangleCount => Batches.Sum(b => (long)b.IndexCount) / 3;

    /// <summary>The distinct feature tables this scene holds — an array of 900 elements holds one.</summary>
    public int FeatureTableCount => Features.Where(f => f.Table is not null).Select(f => f.Table).Distinct().Count();

    /// <summary>Object <paramref name="id"/>'s features, or none.</summary>
    public Edit.Scene3DFeatureRef FeaturesOf(uint id) => id >= 1 && id <= Features.Length ? Features[id - 1] : default;
    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }
    /// <summary>The bounds of what is not air, box or boundary — what Fit frames.</summary>
    public required Vector3 ContentMin { get; init; }
    public required Vector3 ContentMax { get; init; }

    /// <summary>3D editor round 5 — what Fit frames NOW: the bounds of every object <paramref name="visible"/> shows. A hidden
    /// solid is not framed; a dielectric the user shows is, and so is the air box while it is drawn — unlike
    /// <see cref="ContentMin"/>, which is what the FIRST view of a scene frames. With nothing shown, the content's bounds.</summary>
    public (Vector3 Min, Vector3 Max) VisibleContent(ReadOnlySpan<bool> visible)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < Objects.Length; i++)
        {
            var o = Objects[i];
            if (!visible.IsEmpty && (i >= visible.Length || !visible[i])) continue;
            min = Vector3.Min(min, o.Min);
            max = Vector3.Max(max, o.Max);
        }
        return min.X > max.X ? (ContentMin, ContentMax) : (min, max);
    }
    /// <summary>The problem the scene was built from — kept for the tooltip, the grid and the tree.</summary>
    public Em3dProblem? Problem { get; init; }
    /// <summary>The problem's notes and warnings, or the refusal when there is no problem.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>brief-em3d-105 R-em3d105-4 — the scene's appearance table: one row per distinct resolved appearance (equal ones
    /// share a row), at most <see cref="Scene3DBuilder.AppearanceSlots"/>. A shade vertex's <see cref="Scene3DShadeVertex.Slot"/>
    /// indexes it.</summary>
    public CircuitRF.Design.ThreeD.Appearance.AppearanceValues[] Appearances { get => _appearances; init => _appearances = value; }
    private CircuitRF.Design.ThreeD.Appearance.AppearanceValues[] _appearances = [];

    /// <summary>How many objects draw with their role's default because the table was full (overview D16) — for the status
    /// line.</summary>
    public int AppearanceFallbacks { get => _appearanceFallbacks; init => _appearanceFallbacks = value; }
    private int _appearanceFallbacks;

    /// <summary>brief-em3d-108 — the question the builder asked the resolver for each object that owns geometry (parallel to the
    /// first <see cref="OwnedObjects"/> objects; null for what has no appearance), so a look can be asked again with no builder
    /// (<see cref="Scene3DLooks.Restyle"/>). An element asks nothing: it takes its prototype's row.</summary>
    public CircuitRF.Design.ThreeD.Appearance.AppearanceRequest?[] AppearanceRequests { get => _appearanceRequests; init => _appearanceRequests = value; }
    private CircuitRF.Design.ThreeD.Appearance.AppearanceRequest?[] _appearanceRequests = [];

    /// <summary>brief-em3d-108 — the scene whose geometry this one draws: itself, or (a restyled copy) the scene it was restyled from.
    /// What a cache of anything built from the GEOMETRY (a field's surfaces, the FDTD grid) compares, so a look changed under a drag
    /// rebuilds none of them.</summary>
    public Scene3DModel Geometry => _geometry ?? this;
    private Scene3DModel? _geometry;

    /// <summary>brief-em3d-108 — this scene with another look: the same geometry, colours, batches and generation, with
    /// <paramref name="objects"/> (copies where a slot moved), <paramref name="shade"/>, the table and its fallbacks. What
    /// <see cref="Scene3DLooks.Restyle"/> returns; a shallow copy, so every array it does not replace is shared.</summary>
    internal Scene3DModel WithLooks(Scene3DObject[] objects, Scene3DShadeVertex[] shade,
                                    CircuitRF.Design.ThreeD.Appearance.AppearanceValues[] table, int fallbacks,
                                    CircuitRF.Design.ThreeD.Appearance.AppearanceRequest?[] requests)
    {
        var copy = (Scene3DModel)MemberwiseClone();
        copy._geometry = Geometry;
        copy._objects = objects;
        copy._shadeVertices = shade;
        copy._appearances = table;
        copy._appearanceFallbacks = fallbacks;
        copy._appearanceRequests = requests;
        return copy;
    }

    public int TriangleCount => Indices.Length / 3;
    public long VertexBytes => (long)Vertices.Length * Scene3DVertex.Stride;
    public long IndexBytes => (long)Indices.Length * sizeof(uint);
    public long LineBytes => (long)LineVertices.Length * Scene3DVertex.Stride;
    public long ShadeBytes => (long)ShadeVertices.Length * Scene3DShadeVertex.Stride;

    /// <summary>The object with ID <paramref name="id"/>, or null (0, or out of range).</summary>
    public Scene3DObject? Object(uint id) => id >= 1 && id <= Objects.Length ? Objects[id - 1] : null;

    /// <summary>brief-em3d-101 R-em3d101-4a — the distinct textures the scene draws (one per path), what
    /// <see cref="ImageBatches"/> index, and the image vertex stream they draw from.</summary>
    public Scene3DTexture[] Images { get; init; } = [];
    public Scene3DImageVertex[] ImageVertices { get; init; } = [];
    public Scene3DImageBatch[] ImageBatches { get; init; } = [];
    public long ImageBytes => (long)ImageVertices.Length * Scene3DImageVertex.Stride;

    /// <summary>brief-em3d-101 — each image sheet's picture as it was placed, by object name: what a vector export of this scene
    /// draws (Em3dSceneImages).</summary>
    public IReadOnlyDictionary<string, CircuitRF.Design.ThreeD.C3dPlacedImage> PlacedImages { get; init; } =
        new Dictionary<string, CircuitRF.Design.ThreeD.C3dPlacedImage>();

    /// <summary>brief-em3d-101 Phase B — the face images drawn, placed (what a vector export of this scene draws), and why each one
    /// that is not drawn is not, by face spelled <c>object/face</c> (a curved face, a face the object no longer has).</summary>
    public IReadOnlyList<Scene3DPlacedFaceImage> PlacedFaceImages { get; init; } = [];
    public IReadOnlyDictionary<string, string> FaceImageProblems { get; init; } = new Dictionary<string, string>();

    /// <summary>brief-em3d-101 — whether object <paramref name="id"/>'s colour is an image (an image sheet's surface).</summary>
    public bool HasImageSurface(uint id) => Object(id)?.Underlay == true;

    /// <summary>brief-em3d-90 — how far the builder lifted each face-boundary tint off its face, metres (0 with none): the
    /// distance along a ray within which a tint and the face under it are one place (Scene3DPicking.TintTie).</summary>
    public float TintLift { get; init; }

    /// <summary>brief-em3d-90 — whether any object is a face-boundary tint: hover asks the CPU about tints only then.</summary>
    public bool HasTints => _hasTints ??= Objects.Any(o => o.Tint);
    private bool? _hasTints;

    /// <summary>A scene-local point in world metres.</summary>
    public (double X, double Y, double Z) ToWorld(Vector3 local) => (Origin.X + local.X, Origin.Y + local.Y, Origin.Z + local.Z);

    /// <summary>A world point in scene-local coordinates.</summary>
    public Vector3 ToLocal(double x, double y, double z) => new((float)(x - Origin.X), (float)(y - Origin.Y), (float)(z - Origin.Z));

    /// <summary>The empty scene — what a view shows before its first generation lands.</summary>
    public static Scene3DModel Empty(long generation = 0, IReadOnlyList<string>? notes = null) => new()
    {
        Generation = generation, Origin = (0, 0, 0), Vertices = [], Indices = [], LineVertices = [],
        Objects = [], Batches = [], LineBatches = [],
        BoundsMin = new Vector3(-1e-3f), BoundsMax = new Vector3(1e-3f),
        ContentMin = new Vector3(-1e-3f), ContentMax = new Vector3(1e-3f),
        Notes = notes ?? [],
    };
}
