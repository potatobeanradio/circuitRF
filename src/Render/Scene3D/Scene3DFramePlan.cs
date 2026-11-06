// brief-em3d-28 R-em3d28-1 / §8.4's frame loop — WHAT one frame draws, decided below the firewall.
//
// A GPU backend (src/Ui/Viewer3D) is plumbing: it binds the buffers it uploaded for a generation,
// writes the uniform block, and issues the draws this plan lists, in order. Every decision — which
// objects are visible, the back-to-front order of the translucent ones, what hover and selection
// highlight, where the clip plane is, whether this frame carries a pick — is made here, once, for all
// three backends. So a later headless `render` of a 3D view can reuse it, and the gates can count
// draws without a GPU.
//
// ZERO ALLOCATION PER FRAME. Plan() writes into arrays sized when the scene changed; the translucent
// sort is in place. The frame loop is near-zero managed work (§8.4 point 1), which is also what keeps
// the owner's Debug build responsive.

using System.Numerics;
using System.Runtime.InteropServices;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Look;

namespace CircuitRF.Render.Scene3D;

/// <summary>The pipeline state a draw needs.</summary>
public enum Scene3DPipeline
{
    /// <summary>Triangles, depth write, no blend.</summary>
    Opaque,
    /// <summary>Triangles, depth test without write, blend (RGB src-alpha; alpha ONE, 1 − src-alpha).</summary>
    Translucent,
    /// <summary>A line list, depth write, no blend, unshaded.</summary>
    Lines,
    /// <summary>The ID pass: triangles into the R32Uint + position targets.</summary>
    Pick,
    /// <summary>brief-em3d-29 — a field's triangles (FieldVertex, not indexed), depth write, no blend,
    /// coloured by the field uniforms.</summary>
    Field,
    /// <summary>brief-em3d-43 — a selected object's feature edges (a line list from the scene's line
    /// buffer), depth test off, blend, drawn only where the selection says (fs_edge). 3D editor bugs round 3 — each
    /// is drawn <see cref="Scene3DFramePlan.EdgePasses"/> times a pixel apart, so the outline is two pixels wide on
    /// every backend (none of them draws a line wider than one).</summary>
    Edges,
    /// <summary>brief-em3d-43 R-em3d43-4d — a selected face again, depth test off, blend at reduced
    /// opacity (fs_top), so a face behind others is seen through them.</summary>
    OnTop,
    /// <summary>brief-em3d-45 R-em3d45-2 — the drawing plane's grid: six vertices made by the vertex shader
    /// (vs_grid) covering the viewport, no vertex buffer, depth test without write, blend; fs_grid casts each
    /// fragment's ray at the plane and writes its depth.</summary>
    Grid,
    /// <summary>brief-em3d-101 R-em3d101-4 — an image's triangles (Scene3DImageVertex, not indexed) sampling texture
    /// <see cref="Scene3DDraw.Texture"/> (vs_image / fs_image), depth write, blend (an opaque image's alpha is 1, so the blend
    /// changes nothing; a texel the image leaves fully transparent is discarded and writes no depth).</summary>
    Image,
    /// <summary>brief-em3d-101 — the same, sorted with the translucent objects: depth test without write, blend.</summary>
    ImageTranslucent,
    /// <summary>brief-em3d-106 R-em3d106-2a — the realistic view's triangles (vs_pbr / fs_pbr), reading the shade stream as a second
    /// vertex buffer and the appearance table and environment: depth write, no blend.</summary>
    Pbr,
    /// <summary>The same, sorted with the translucent objects: depth test without write, blend PREMULTIPLIED (RGB one,
    /// 1 − src-alpha; alpha one, 1 − src-alpha), so a reflection on glass adds on top of what shows through.</summary>
    PbrTranslucent,
    /// <summary>R-em3d106-3f — the realistic view's backdrop, a gradient or the environment: six vertices made by vs_grid covering the
    /// viewport, fs_backdrop, no depth test or write, no blend; drawn first.</summary>
    Backdrop,
    /// <summary>brief-em3d-107 R-em3d107-1a — a caster's triangles into the key light's shadow map (vs_shadow / fs_depth, depth only,
    /// the shadow bias <see cref="Scene3DFramePlan.ShadowBias"/>): only in <see cref="Scene3DFramePlan.ShadowDraws"/>.</summary>
    ShadowDepth,
    /// <summary>R-em3d107-3 — the shadow catcher: six vertices made by vs_ground (a square around the disc on the ground's plane, no vertex
    /// buffer), fs_ground drawing black with the darkening as alpha; depth test without write, blend; after everything opaque.</summary>
    Ground,
    /// <summary>brief-em3d-109 R-em3d109-3b — a field's triangles as <see cref="Field"/> draws them (fs_field), but BLENDED (RGB src-alpha,
    /// 1 − src-alpha; alpha one, 1 − src-alpha) at the Look's FieldOpacity: the realistic view's Exact or Glow field below 100 %.</summary>
    FieldBlend,
    /// <summary>R-em3d109-2 — the realistic view's Lit field (vs_field_lit / fs_field_lit): the field's vertices with their normal stream
    /// (<see cref="Fields.FieldShading"/>) as a second vertex buffer, the environment bound; depth write, blended as
    /// <see cref="FieldBlend"/> (at 100 % its alpha is 1 and the blend changes nothing).</summary>
    FieldLit,
}

/// <summary>Which buffer a draw reads: the scene's, one of the overlay slots, the field's, none (the grid), or the scene's image
/// stream (brief-em3d-101).</summary>
public enum Scene3DBuffer { Scene, SceneLines, Overlay0, Overlay1, Overlay2, Field, None, Image }

/// <summary>One draw: <see cref="Count"/> indices (triangles) or vertices (lines) from <see cref="First"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Scene3DDraw
{
    public Scene3DPipeline Pipeline;
    public Scene3DBuffer Buffer;
    public int First, Count;
    /// <summary>brief-em3d-46 — the per-draw transform: a slot of <see cref="Scene3DFramePlan.Transforms"/>, 0 for the
    /// identity. A backend hands the slot's 80 bytes (the matrix, and brief-em3d-48's id offset) to the vertex stage before the draw (Metal setVertexBytes,
    /// a D3D11 constant buffer, Vulkan push constants), and only when it differs from the draw before.</summary>
    public int Transform;
    /// <summary>3D editor round 3 / bugs round 9 — who wins where two objects' faces coincide (<see cref="Scene3DDepthTie"/>):
    /// the draw takes <see cref="Scene3DFramePlan.DepthBias"/>'s polygon offset, so the winner passes the depth test — in
    /// the picture and in the ID pass — instead of the two fighting per pixel.</summary>
    public Scene3DDepthTie Tie;

    /// <summary>brief-em3d-101 — an image draw's texture: an index into <see cref="Scene3DModel.Images"/> (−1 for any other draw).</summary>
    public int Texture;

    /// <summary>brief-em3d-106 — the object a draw is of (0 for one that is of no single object: an element's whole draw, an
    /// overlay, the grid, a field). Read by no backend; the gates and <see cref="Scene3DFramePlan.ChromeOf"/> use it.</summary>
    public uint Object;

    /// <summary>A dielectric's or air's draw, which gives way to any metal face lying on one of its own.</summary>
    public readonly bool Behind => Tie == Scene3DDepthTie.Behind;
}

/// <summary>
/// 3D editor bugs round 9 — the order in which coincident faces of different objects win the depth test, lowest first:
/// an image sheet (brief-em3d-101), dielectric (and air), then metal, then via metal, then an image mapped onto a face (brief 101
/// Phase B), then a port's surface, then a field plot. Metal over dielectric is em-3d.md §6.3a's
/// precedence (3D editor round 3); a via over the metal it passes through, and a port over the metal it lies on, are the
/// owner's rule for the view. Each step is one <see cref="Scene3DFramePlan.BehindDepthBias"/> of polygon offset, so two
/// coplanar faces a step apart never fight, and faces genuinely apart are not reordered at any zoom the depth range allows.
/// </summary>
public enum Scene3DDepthTie : sbyte
{
    /// <summary>brief-em3d-101 R-em3d101-4b — an image sheet: gives way to EVERY face lying on its plane, so what is traced on it
    /// is drawn over it and is what a click finds — the 3D form of the layout's "bitmaps always render behind" (R-bmp-2). One
    /// whose image is <c>InFront</c> (a picture placed on a solid's face) ties <see cref="FaceImage"/> instead.</summary>
    Underlay = -2,
    /// <summary>A dielectric or air: gives way to a metal face on its own.</summary>
    Behind = -1,
    /// <summary>Metal — a conductor, a wire, a sheet — and everything else.</summary>
    None = 0,
    /// <summary>Via metal: wins over the pad and plane it passes through.</summary>
    Via = 1,
    /// <summary>brief-em3d-101 R-em3d101-10 — an image mapped onto a face: wins over the face it lies on (any kind of object's),
    /// and gives way to a port's surface or a field plot on that face. Also an image sheet whose image is <c>InFront</c>.</summary>
    FaceImage = 2,
    /// <summary>A port's surface: wins over the metal it lies on.</summary>
    Port = 3,
    /// <summary>A field plot's slice or painted faces: the datum, so it wins over any geometry lying where it lies.</summary>
    Field = 4,
}

/// <summary>
/// brief-em3d-46 R-em3d46-1a — a drag's PREVIEW: what is already drawn, drawn again under per-draw transforms. The
/// document is not touched and nothing uploads but the transforms. <see cref="Moving"/> marks the objects (by
/// ID − 1); each is drawn once per entry of <see cref="Copies"/> (row-vector matrices in scene-local metres), and
/// also where it is when <see cref="KeepOriginal"/> (a duplicate, an array). A moving object is left out of the
/// ID pass, so what the cursor finds — and snaps to — is what lies under it.
/// </summary>
public sealed class Scene3DPreview(bool[] moving, Matrix4x4[] copies, bool keepOriginal)
{
    public bool[] Moving { get; } = moving;
    public Matrix4x4[] Copies { get; set; } = copies;
    public bool KeepOriginal { get; } = keepOriginal;

    public bool IsMoving(uint id) => id >= 1 && id <= Moving.Length && Moving[id - 1];
}

/// <summary>An overlay's lines (the Gmsh mesh, its section on the clip plane, the FDTD grid) and the
/// version that changes when they do — a backend uploads a slot only when its version moved.</summary>
public sealed class Scene3DOverlay(Scene3DVertex[] lines, long version)
{
    public Scene3DVertex[] Lines { get; } = lines;
    public long Version { get; } = version;
    public static readonly Scene3DOverlay None = new([], 0);
}

/// <summary>
/// Everything the view knows that is not geometry: the camera, per-object visibility, hover, selection,
/// the clip plane, which overlays are on, and a pending pick. Changed by the input loop, read by
/// <see cref="Scene3DFramePlan.Plan"/>. Owns no geometry, so changing it can never cause any.
/// </summary>
public sealed class Viewer3DViewState
{
    /// <summary>The camera. Starts with a real field of view and distance, so a frame planned before
    /// the first scene lands (or for a setup that refused) is a valid, empty picture.</summary>
    public Camera3D Camera = new() { FovY = Camera3D.DefaultFovY, Distance = 1e-2f, SceneRadius = 1e-3f, Yaw = -MathF.PI / 4, Pitch = 0.6f };
    public ClipPlane3D Clip;
    public bool[] Visible = [];
    public uint Hovered;
    /// <summary>3D editor groups — the other objects a click on the hovered one takes (its group), tinted with it in Object mode.
    /// The shader reads as many as fit after the selection in its <see cref="Scene3DFramePlan.SelectionLimit"/> slots.</summary>
    public uint[] HoverGroup = [];
    /// <summary>brief-em3d-43 — the hovered object's face under the cursor (Face mode's hover), or −1.</summary>
    public int HoveredFace = -1;
    /// <summary>The hover draws no tint: a tool that picks something smaller than an object (the Wire tool's landing face)
    /// shows that itself, and a whole object lit under it would say the tool takes the object.</summary>
    public bool HoverHidden;
    /// <summary>brief-em3d-43 R-em3d43-2 — what a click selects.</summary>
    public Scene3DSelectMode Mode;
    /// <summary>brief-em3d-43 — the selection, in the order it was made. The shader highlights the first
    /// <see cref="Scene3DFramePlan.SelectionLimit"/>; the rest are selected all the same.</summary>
    public Scene3DItem[] Selection = [];

    /// <summary>The first selected object, 0 for none; setting it selects that one object.</summary>
    public uint Selected
    {
        get => Selection.Length > 0 ? Selection[0].Object : 0;
        set => Selection = value == 0 ? [] : [Scene3DItem.OfObject(value)];
    }
    /// <summary>The cursor, device pixels from the top-left; negative when it is not over the view.</summary>
    public float CursorX = -1, CursorY = -1;
    public bool ShowMesh, ShowMeshSection, ShowGrid;
    /// <summary>brief-em3d-29 — draw the field geometry.</summary>
    public bool ShowField;
    /// <summary>Objects a field surface is drawn ON (a conductor, a solid's faces): their own triangles
    /// are left out while the field is shown, since the two would lie in one plane and fight.</summary>
    public bool[] FieldCovered = [];
    /// <summary>The field uniform blocks (FieldUniforms.Floats each, brief-em3d-96: one per drawn plot, FieldUniforms.MaxLayers):
    /// the phase, the range, the mode and the colour map — what an animation changes, per frame, instead of any geometry.</summary>
    public readonly float[] Field = new float[Fields.FieldUniforms.LayersFloats];
    /// <summary>brief-em3d-45 — the drawing plane's grid, or null (the read-only viewer draws none).</summary>
    public DrawingGridSettings? DrawingGrid;
    public bool ShowAxisIndicator = true;
    /// <summary>A camera gesture moved the camera since the last frame.</summary>
    public bool Orbiting;
    public (float R, float G, float B) Background = ThemeBackground(dark: true);

    /// <summary>The 3D editor's background in a theme's variant — the realistic view's "Theme" background too. brief-em3d-110: one
    /// place, which `render --look realistic` reads for its <c>--variant</c>.</summary>
    public static (float R, float G, float B) ThemeBackground(bool dark) => dark ? (0.12f, 0.13f, 0.15f) : (0.93f, 0.94f, 0.96f);
    /// <summary>brief-em3d-46 — a drag's preview, or null.</summary>
    public Scene3DPreview? Preview;
    /// <summary>brief-em3d-106 R-em3d106-1a — the realistic view: view state, never saved in the document (overview D5). It takes
    /// effect once <see cref="Environment"/> is ready; until then the frame is the default view's.</summary>
    public bool Realistic;
    /// <summary>The document's Look, parsed (R-em3d106-5).</summary>
    public RealisticLook Look = RealisticLook.Default;
    /// <summary>The prefiltered environment the realistic view lights with, or null while it is being made.</summary>
    public PrefilteredEnvironment? Environment;

    /// <summary>Whether this frame draws realistically: asked for, and the environment is ready.</summary>
    public bool DrawsRealistic => Realistic && Environment is not null;

    /// <summary>Resets visibility to the scene's defaults when the object list changed shape; keeps the
    /// user's toggles across a regeneration that kept the same objects.</summary>
    public void Adopt(Scene3DModel scene, IReadOnlyList<string>? previousNames)
    {
        var old = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (previousNames is not null)
            for (int i = 0; i < previousNames.Count && i < Visible.Length; i++) old[previousNames[i]] = Visible[i];
        var v = new bool[scene.Objects.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = old.TryGetValue(scene.Objects[i].Name, out bool was) ? was : scene.Objects[i].InitiallyVisible;
        Visible = v;
        if (Hovered > v.Length) { Hovered = 0; HoveredFace = -1; }
        if (HoverGroup.Any(id => id > v.Length)) HoverGroup = [];
        if (Selection.Any(i => i.Object > v.Length)) Selection = [.. Selection.Where(i => i.Object <= v.Length)];
    }

    public bool IsVisible(uint id) => id >= 1 && id <= Visible.Length && Visible[id - 1];

    /// <summary>Drawn in the colour pass: visible, and not under a field surface being shown.</summary>
    public bool IsDrawn(uint id) => IsVisible(id) && !(ShowField && !FieldShowsThrough && id <= FieldCovered.Length && FieldCovered[id - 1]);

    /// <summary>brief-em3d-109 R-em3d109-3b — the realistic view's field below full opacity: what it is painted on is drawn under it, so
    /// the part's material shows through the colour (a covered object is drawn after all).</summary>
    public bool FieldShowsThrough => DrawsRealistic && Look.FieldBlends;
}

/// <summary>One frame's uniform blocks and draw lists. Reused from frame to frame.</summary>
public sealed class Scene3DFramePlan
{
    /// <summary>brief-em3d-43 R-em3d43-5 — how many selected items the shader highlights: the uniform
    /// block's (object, face) list. A selection beyond it is still the selection — the tree, the
    /// Properties panel and every command see all of it — but only the first this many are drawn
    /// highlighted, and the status line says so.</summary>
    public const int SelectionLimit = 64;

    /// <summary>Where the selection list starts in the block, in floats.</summary>
    private const int SelectionAt = 32;

    /// <summary>Where brief 29's field block starts, in floats.</summary>
    public const int FieldAt = SelectionAt + 2 * SelectionLimit;

    /// <summary>Where brief 45's grid block starts, in floats.</summary>
    public const int GridAt = FieldAt + Fields.FieldUniforms.LayersFloats;

    /// <summary>brief-em3d-106 R-em3d106-3 — where the realistic view's look block starts, in floats (<see cref="LookFloats"/>).</summary>
    public const int LookAt = GridAt + PlaneGrid.Floats;

    /// <summary>The look block, 544 bytes: exposure (2^EV), intensity, the environment's rotation (cos, sin); the background's mode and
    /// the environment's last level; the key light's world direction; its radiance; the background's two colours; the backdrop's
    /// ray (direction, and its change per clip x and y); the nine irradiance coefficients; then brief 107's (<see cref="LightingAt"/>):
    /// the shadow map's matrix, its parameters and the light's frame, the occlusion's parameters and the view's ray, and the ground.
    /// Zero while the view is not realistic.</summary>
    public const int LookFloats = 136;

    /// <summary>brief-em3d-107 — where its part of the look block starts, within the block (scene.wgsl's <c>lvp</c>).</summary>
    public const int LightingAt = 72;

    /// <summary>Floats in the uniform block — the WGSL <c>U</c>: vp (16), eye (4), clip (4), the hovered
    /// (object, face), flags, the mode, the selection's count and three pads (128 bytes), the selection's
    /// (object, face) pairs (512 bytes), brief 29's field blocks (<see cref="Fields.FieldUniforms"/>, 288 bytes each,
    /// brief-em3d-96: four, 1,152 bytes), brief 45's grid block (<see cref="PlaneGrid.Floats"/>, 224 bytes), then brief 106's look
    /// block (544 bytes since brief 107). 2,560 bytes — under Metal's 4 KB inline-bytes limit (<c>setVertexBytes</c>), which the Metal backend
    /// asserts.</summary>
    public const int UniformFloats = LookAt + LookFloats;
    public const int UniformBytes = UniformFloats * 4;

    /// <summary>3D editor bugs round 3 — how many times a selected object's edges are drawn: at (0, 0), (1, 0), (0, 1)
    /// and (1, 1) pixels, a 2 × 2 brush — twice the one-pixel line every backend draws. Pass k's slot carries k in
    /// <c>id.y</c> (x = k &amp; 1, y = k &gt;&gt; 1; 0 is no offset), applied after projection by the vertex shader.</summary>
    public const int EdgePasses = 4;

    /// <summary>3D editor bugs round 3 — the opacity an opaque object is drawn at while it is selected in Object mode
    /// (fs_color; a translucent one keeps its own when that is already less). Drawn with the translucent objects,
    /// sorted among them and writing no depth, so what it hides shows through; the ID pass is unchanged.</summary>
    public const float SelectedAlpha = 0.5f;

    /// <summary>3D editor round 3 — the depth bias a <see cref="Scene3DDraw.Behind"/> draw takes: a constant, in the depth
    /// format's smallest resolvable steps, and a slope factor — a polygon offset pushing the dielectric AWAY from the eye, so a
    /// coincident metal face passes LessEqual and the dielectric's does not. Small enough that a dielectric genuinely in
    /// front of a metal (a passivation over a trace) still covers it at any zoom the depth range allows.
    /// <para>The slope factor is also how far the metal's OWN hidden faces come through at an edge: a copper layer reaching the
    /// board's side is embedded in its slab, and its top face, meeting the slab's wall along that edge, wins every pixel within
    /// the bias of the wall — a lit line along each copper's top in the realistic view. At 2 that line was continuous; 1/16
    /// leaves a few dots, and a coincident wall needed only 1/128 on Metal (0 fights), so it keeps 8× for coarser rasterisers.</para></summary>
    public const float BehindDepthBias = 4, BehindSlopeScale = 1f / 16;

    /// <summary>The same bias as the CPU picks apply it (Scene3DPicking, Scene3DIdPatch): added to a Behind object's NDC
    /// depth (and, relative, to its ray distance) — a few float steps at depth 1, so exactly coincident faces resolve to
    /// the metal and nothing else changes.</summary>
    public const float BehindNdc = 1e-6f;

    /// <summary>3D editor bugs round 9 — where object <paramref name="id"/> stands when one of its faces coincides with another
    /// object's (<see cref="Scene3DDepthTie"/>).</summary>
    public static Scene3DDepthTie TieOf(Scene3DModel scene, uint id)
        => id < 1 || id > scene.Objects.Length ? Scene3DDepthTie.None
         : scene.Objects[id - 1].Underlay ? scene.Objects[id - 1].ImageInFront ? Scene3DDepthTie.FaceImage : Scene3DDepthTie.Underlay
         : scene.Objects[id - 1].Kind switch
        {
            Scene3DKind.Dielectric or Scene3DKind.Air => Scene3DDepthTie.Behind,
            // A .c3d object of a dielectric (a STEP part's mould compound) is a Body; its role is what decides the tie
            Scene3DKind.Body when scene.Objects[id - 1].Role is Em3dRole.Dielectric or Em3dRole.Air => Scene3DDepthTie.Behind,
            Scene3DKind.Via => Scene3DDepthTie.Via,
            Scene3DKind.Port => Scene3DDepthTie.Port,
            _ => Scene3DDepthTie.None,
        };

    /// <summary>brief-em3d-138 — where batch <paramref name="b"/> stands: its object's tie, except a slab's free walls (Scene3DWalls),
    /// which coincide with nothing and take no polygon offset — a face ending on a biased face leaks within the bias.</summary>
    public static Scene3DDepthTie TieOf(Scene3DModel scene, in Scene3DBatch b) => b.Free ? Scene3DDepthTie.None : TieOf(scene, b.ObjectId);

    /// <summary>The lowest and highest tie, for a backend that makes one state per tie (D3D11's rasterizer states).</summary>
    public const Scene3DDepthTie TieMin = Scene3DDepthTie.Underlay, TieMax = Scene3DDepthTie.Field;

    /// <summary>The polygon offset a <paramref name="tie"/> draw takes, in the backends' terms (constant steps, slope factor,
    /// clamp — the bias's largest magnitude in depth units, 0 for none): one <see cref="BehindDepthBias"/> per step, away from
    /// the eye below <see cref="Scene3DDepthTie.None"/> and towards it for each step above <see cref="Scene3DDepthTie.None"/>.
    /// A draw pushed AWAY takes the slope factor unclamped. Towards the eye it is unbounded where a wall is seen edge-on, and a
    /// via barrel's silhouette came through the copper above it as a dotted trail, so a via takes none. What lies ON a face —
    /// an image (<see cref="Scene3DDepthTie.FaceImage"/>), a port's surface, a field plot — takes one, CLAMPED
    /// (<see cref="DecalSlopeScale"/>, <see cref="DecalBiasClamp"/>): each is a different triangulation of the face's plane, and
    /// seen obliquely the two interpolate depths further apart than the constant covers.</summary>
    public static (float Constant, float Slope, float Clamp) DepthBias(Scene3DDepthTie tie)
        => tie < Scene3DDepthTie.None ? (-(int)tie * BehindDepthBias, BehindSlopeScale, 0)
         : tie >= Scene3DDepthTie.FaceImage ? (-(int)tie * BehindDepthBias, -DecalSlopeScale, -DecalBiasClamp)
         : (-(int)tie * BehindDepthBias, 0, 0);

    /// <summary>What lies on a face (<see cref="Scene3DDepthTie.FaceImage"/> and above): its slope factor towards the eye, and the clamp on
    /// its whole bias, in depth units. Measured on an image sheet lying on a box's face, Metal, 2,250 views (five zooms, both
    /// projections, ±77° about the face's normal): with no slope term the box won 17 % of the image's pixels (half the views
    /// fought, most at 45° and beyond); this slope with no clamp, none — but seen from BEHIND the box, the sheet then came through
    /// along the box's silhouette in 1–2 px lines (450 k px over the views). This clamp left 2 px fighting, on a face three
    /// pixels across, and a few silhouette pixels a view, about what the constant alone leaves. 1e-4 left 99 px fighting;
    /// 3e-4 none, at 35 % more silhouette pixels. A lumped port lying on a box's face, the same sweep: 14 % of its pixels lost
    /// in 63 % of the views with the constant alone, none with this; silhouette pixels from behind 382 → 1,728 over the views.
    /// A field plot on a face is the same arrangement and takes the same, unmeasured (it needs a solved field).</summary>
    public const float DecalSlopeScale = 2, DecalBiasClamp = 2e-4f;

    /// <summary>The same offset as the CPU picks apply it: added to an object's NDC depth (<see cref="BehindNdc"/> a step).</summary>
    public static float TieNdc(Scene3DDepthTie tie) => -(int)tie * BehindNdc;

    /// <summary>Flag bits in the uniform block's <c>flags</c>.</summary>
    public const uint FlagClip = 1, FlagCapBackFaces = 2;

    public readonly float[] Uniforms = new float[UniformFloats];

    /// <summary>brief-em3d-46 — the per-draw transforms, <see cref="TransformFloats"/> floats a slot: the WGSL <c>MX</c> —
    /// a <c>mat4x4f</c> (column-major, p' = M·p) and, brief-em3d-48, a <c>vec4u</c> whose x is added to every vertex's id
    /// (an array element's objects are the prototype's plus that). Slot 0 is the identity; then each element's slot and
    /// its box's (written once per scene); then a preview's copies. <see cref="TransformCount"/> slots are in use this
    /// frame.</summary>
    public float[] Transforms = IdentitySlots(8);
    public int TransformCount = 1;

    /// <summary>Floats a transform slot takes: the matrix's 16 and the id offset's vec4u.</summary>
    public const int TransformFloats = 20;

    /// <summary>Bytes of transform this frame hands the vertex stage for its preview draws: TransformBytesPerDraw each — what a
    /// drag's preview costs a frame, and all it costs (gate 1).</summary>
    public long TransformBytes;

    public const int TransformBytesPerDraw = TransformFloats * 4;

    /// <summary>The most copies a preview draws (an array's live preview beyond this shows its first copies; the
    /// accepted array writes them all). Also what a backend sizes its per-frame transform slots for.</summary>
    public const int MaxPreviewCopies = 256;
    public readonly float[] PickUniforms = new float[UniformFloats];
    public Scene3DDraw[] Draws = new Scene3DDraw[16];
    public int DrawCount;
    public Scene3DDraw[] PickDraws = new Scene3DDraw[16];
    public int PickDrawCount;
    /// <summary>Whether this frame carries an ID pass, and the pixel it reads.</summary>
    public bool Pick;
    public int PickX, PickY;
    /// <summary>brief-em3d-44 R-em3d44-2a — the ID pass reads an N × N patch centred on (PickX, PickY): N odd,
    /// set by the pane to what its backend reads back (1 for a backend that reads one texel).</summary>
    public int PickSize = 1;
    /// <summary>The viewport pixels per DIP the pane draws at — carried to the patch for the overlay.</summary>
    public float PickPixelsPerDip = 1;
    /// <summary>What the pick was planned with, handed back with its read-back (the patch's camera and cursor).</summary>
    public Camera3D PickCamera;
    public float PickCursorX, PickCursorY;
    public int Width, Height;
    public (float R, float G, float B) Clear;
    public long SceneGeneration = -1;

    /// <summary>brief-em3d-45 — the grid this frame drew (MinorDbu 0: none), and how many frames drew one.</summary>
    public PlaneGridSpacing GridSpacing;
    public long GridFrames;

    private float[] _keys = [];
    private int[] _order = [];
    private Scene3DModel? _sized;
    /// <summary>Per object (by ID − 1): its edge batch's index, or −1; and its triangle batch's.</summary>
    private int[] _edgeOf = [], _batchOf = [];
    private bool[] _marked = [];
    /// <summary>Per object (by ID − 1): selected in Object mode this frame, so drawn faded (<see cref="SelectedAlpha"/>).</summary>
    private bool[] _faded = [];
    private readonly int[] _fadedIds = new int[SelectionLimit];
    private int _fadedCount;
    /// <summary>Per transform slot this frame: the first of its <see cref="EdgePasses"/> − 1 offset slots, or −1.</summary>
    private int[] _edgeSlotsOf = [];
    private int[] _edgeSlotKeys = new int[16];
    private int _edgeSlotsUsed;

    /// <summary>brief-em3d-48 R-em3d48-3c — the most triangles a frame draws before array elements, farthest from the eye
    /// first, are drawn as their bounding boxes. The owner sets it (Settings); the status line says when it bites.</summary>
    public long TriangleBudget = DefaultTriangleBudget;
    public const long DefaultTriangleBudget = 20_000_000;

    /// <summary>brief-em3d-48 — how many elements this frame drew as boxes, and how many the scene has.</summary>
    public int LodBoxedElements;
    public int ElementCount;

    /// <summary>brief-em3d-48 — slot of element <paramref name="e"/>'s transform, and of its bounding box's.</summary>
    public static int ElementSlot(int e) => 1 + e;
    private int BoxSlot(int e) => 1 + _elements + e;
    private int _elements, _copyBase = 1;
    private float[] _elemKeys = [];
    private int[] _elemOrder = [];
    private bool[] _boxed = [];
    private long _ownedTriangles, _elementTriangles;
    private int[] _comboStart = [];
    private readonly List<int> _comboUsed = [];
    private int _nextSlot;

    /// <summary>
    /// Plans a <paramref name="width"/> × <paramref name="height"/> frame of <paramref name="scene"/>.
    /// <paramref name="flipY"/> for an API whose framebuffer y runs down. <paramref name="pick"/> asks
    /// for an ID pass at the cursor. Allocates only when the scene changed. brief-em3d-106 — <paramref name="export"/>: a picture
    /// (Export Picture, Copy), which in the realistic view draws no hover and no selection (R-em3d106-2c). brief-em3d-107 —
    /// <paramref name="pixelScale"/>: how many of this frame's pixels a window pixel spans (a picture's scale times its supersampling; 1
    /// live), which scales the occlusion's cap in pixels (<see cref="Look.Occlusion.MaxPixels"/>).
    /// </summary>
    public void Plan(Scene3DModel scene, Viewer3DViewState view, int width, int height, bool flipY, bool pick,
                     Scene3DOverlay mesh, Scene3DOverlay section, Scene3DOverlay grid, Fields.Scene3DFieldGeometry? field = null,
                     bool export = false, bool transparent = false, float pixelScale = 1)
    {
        if (!ReferenceEquals(_sized, scene)) Size(scene);
        Width = width; Height = height;
        Clear = view.Background;
        SceneGeneration = scene.Generation;
        bool real = Realistic = view.DrawsRealistic;
        bool quiet = real && export;
        // brief-em3d-107 R-em3d107-5b — a transparent PICTURE: cleared to (0, 0, 0, 0), no backdrop. Never the live view (its alpha stays 1).
        Transparent = quiet && transparent;
        uint flags = view.Clip.Enabled ? FlagClip | FlagCapBackFaces : 0;
        var cam = DepthCamera(scene, view, width, height);
        Fill(Uniforms, view, cam, width, height, flipY, -1, -1, flags, quiet: quiet);
        if (real) FillLook(Uniforms.AsSpan(LookAt, LookFloats), view, cam, width, height, flipY);
        else Uniforms.AsSpan(LookAt, LookFloats).Clear();

        DrawCount = 0;
        TransformBytes = 0;
        FieldNormals = false;
        var preview = view.Preview;
        WriteTransforms(preview);
        ChooseDetail(scene, view);
        PlanLighting(scene, view, preview, cam, width, height, flipY, export, pixelScale);
        if (!quiet) MarkFaded(view);
        else _fadedCount = 0;
        bool hoverTinted = !quiet && !view.HoverHidden;
        _hoverId = hoverTinted ? view.Hovered : 0;
        _hoverGroup = hoverTinted && _hoverId != 0 && view.Mode == Scene3DSelectMode.Object ? view.HoverGroup : [];
        if (real) ColourRealistic(scene, view, preview, mesh, section, grid, field, cam, width, height, flipY);
        else ColourDefault(scene, view, preview, mesh, section, grid, field, cam, width, height, flipY);

        if (!quiet) Selection(scene, view, preview);

        var batches = scene.Batches;
        int owned = scene.OwnedBatches;
        Pick = pick && view.CursorX >= 0 && view.CursorY >= 0 && view.CursorX < width && view.CursorY < height;
        PickDrawCount = 0;
        if (Pick)
        {
            PickX = (int)view.CursorX; PickY = (int)view.CursorY;
            PickCamera = cam;
            PickCursorX = view.CursorX; PickCursorY = view.CursorY;
            Fill(PickUniforms, view, cam, width, height, flipY, PickX, PickY, view.Clip.Enabled ? FlagClip : 0, PickSize);
            for (int k = 0; k < owned; k++)
            {
                var b = batches[k];
                if (view.IsVisible(b.ObjectId) && scene.Objects[b.ObjectId - 1].Pickable && preview?.IsMoving(b.ObjectId) != true)
                    Add(ref PickDraws, ref PickDrawCount, Scene3DPipeline.Pick, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount,
                        tie: TieOf(scene, b));
            }
            for (int e = 0; e < _elements; e++)
            {
                if (_boxed[e]) continue;
                var el = scene.Elements[e];
                var g = scene.Groups[el.Group];
                bool whole = Whole(scene, view, preview, el, g, pickPass: true);
                if (whole) Add(ref PickDraws, ref PickDrawCount, Scene3DPipeline.Pick, Scene3DBuffer.Scene, g.OpaqueFirst, g.OpaqueCount, ElementSlot(e));
                for (int k = el.FirstBatch; k < el.FirstBatch + el.BatchCount; k++)
                {
                    var b = batches[k];
                    if (whole && !b.Translucent) continue;
                    if (view.IsVisible(b.ObjectId) && scene.Objects[b.ObjectId - 1].Pickable && preview?.IsMoving(b.ObjectId) != true)
                        Add(ref PickDraws, ref PickDrawCount, Scene3DPipeline.Pick, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, ElementSlot(e),
                            TieOf(scene, b));
                }
            }
        }
        TransformCount = _nextSlot;
    }

    /// <summary>brief-em3d-106 — whether the last frame was planned realistically (the environment ready and asked for).</summary>
    public bool Realistic { get; private set; }

    /// <summary>The default view's colour pass: everything opaque, the field, the lines and overlays, the grid, then the translucent
    /// objects back to front. Exactly what it was before brief 106.</summary>
    private void ColourDefault(Scene3DModel scene, Viewer3DViewState view, Scene3DPreview? preview, Scene3DOverlay mesh,
                               Scene3DOverlay section, Scene3DOverlay grid, Fields.Scene3DFieldGeometry? field, in Camera3D cam,
                               int width, int height, bool flipY)
    {
        var batches = scene.Batches;
        int owned = scene.OwnedBatches;
        for (int k = 0; k < owned; k++)
        {
            var b = batches[k];
            if (!b.Translucent && !Faded(b.ObjectId) && view.IsDrawn(b.ObjectId))
            {
                // brief-em3d-101 — an image sheet's colour IS its image: its own triangles stay in the ID and selection passes.
                if (_surfaceOf[b.ObjectId - 1] >= 0) AddImage(scene, preview, _surfaceOf[b.ObjectId - 1], translucent: false);
                else AddMoved(preview, b.ObjectId, Scene3DPipeline.Opaque, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true,
                              tie: TieOf(scene, b));
            }
        }
        // brief-em3d-48 R-em3d48-3a — each element: ONE draw of its prototype's opaque triangles under its own transform
        // when the whole element is drawn as it is; object by object when part of it is hidden or moving.
        for (int e = 0; e < _elements; e++)
        {
            if (_boxed[e]) continue;
            var el = scene.Elements[e];
            var g = scene.Groups[el.Group];
            if (Whole(scene, view, preview, el, g, pickPass: false) && !AnyFaded(el))
            {
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Opaque, Scene3DBuffer.Scene, g.OpaqueFirst, g.OpaqueCount, ElementSlot(e));
                continue;
            }
            for (int k = el.FirstBatch; k < el.FirstBatch + el.BatchCount; k++)
            {
                var b = batches[k];
                if (!b.Translucent && !Faded(b.ObjectId) && view.IsDrawn(b.ObjectId))
                    AddMoved(preview, b.ObjectId, Scene3DPipeline.Opaque, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true, e,
                             TieOf(scene, b));
            }
        }
        // brief-em3d-29 — the field's slice and surfaces, opaque, before anything translucent; at a coincident face the field
        // wins (its own tie), over metal, a port, and a translucent face drawn after it. brief-em3d-96 — one draw per drawn plot,
        // in the geometry's draw order (the focused plot last, so it wins where two plots coincide under LessEqual), each under
        // a transform slot holding the identity and the plot's colour block in id.y.
        if (view.ShowField && field is { Vertices.Length: > 0 } f)
            foreach (var r in f.Layers)
                if (r.Count > 0 && r.First >= 0 && r.First + r.Count <= f.Vertices.Length)
                    Add(ref Draws, ref DrawCount, Scene3DPipeline.Field, Scene3DBuffer.Field, r.First, r.Count, FieldSlot(r.Layer),
                        Scene3DDepthTie.Field);

        // brief-em3d-45 R-em3d45-2b — the drawing grid after everything opaque (so solids hide it) and before
        // the translucent objects (so it shows faintly through a dielectric). Uniforms only: nothing uploads.
        // Before the lines and overlays, which draw over it, and followed by the hovered object drawn again (HoverOverGrid).
        GridSpacing = default;
        if (view.DrawingGrid is { Visible: true } dg)
        {
            GridSpacing = PlaneGrid.Fill(Uniforms.AsSpan(GridAt), scene, cam, dg, width, height, flipY);
            if (GridSpacing.MinorDbu > 0)
            {
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Grid, Scene3DBuffer.None, 0, 6);
                GridFrames++;
            }
        }
        else Uniforms.AsSpan(GridAt, PlaneGrid.Floats).Clear();
        if (GridSpacing.MinorDbu > 0) HoverOverGrid(scene, view, preview, realistic: false);
        foreach (var lb in scene.LineBatches)
            if (view.IsVisible(lb.ObjectId))
                AddMoved(preview, lb.ObjectId, Scene3DPipeline.Lines, Scene3DBuffer.SceneLines, lb.FirstVertex, lb.VertexCount, identity: true);
        // brief-em3d-48 R-em3d48-3c — an element over the budget: its bounding box, stated on screen.
        if (LodBoxedElements > 0)
            for (int e = 0; e < _elements; e++)
                if (_boxed[e])
                    Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.SceneLines, scene.UnitBox.FirstVertex, scene.UnitBox.VertexCount, BoxSlot(e));
        if (view.ShowMesh && mesh.Lines.Length > 0)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay0, 0, mesh.Lines.Length);
        if (view.ShowMeshSection && section.Lines.Length > 0)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay1, 0, section.Lines.Length);
        if (view.ShowGrid && grid.Lines.Length > 0)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay2, 0, grid.Lines.Length);


        // Translucent objects back to front, one draw each (brief 27 §2.4: per object, not per triangle) — and, 3D editor
        // bugs round 3, an opaque object selected in Object mode, faded, among them.
        // Keyed on VIEW DEPTH, not distance from the eye: orthographic allows a negative near plane, so
        // after zooming in the eye sits inside the scene and objects behind it are still drawn — the
        // nearest to the viewer, though the eye is closer to them than to what they cover.
        var eye = view.Camera.Eye;
        var forward = view.Camera.Forward;
        int n = 0;
        for (int i = 0; i < batches.Length; i++)
        {
            if (!(batches[i].Translucent || Faded(batches[i].ObjectId)) || !view.IsDrawn(batches[i].ObjectId)) continue;
            if (batches[i].Element >= 0 && _boxed[batches[i].Element]) continue;
            _order[n] = i;
            _keys[n] = -Vector3.Dot(scene.Objects[batches[i].ObjectId - 1].Centroid - eye, forward);
            n++;
        }
        Array.Sort(_keys, _order, 0, n);
        for (int k = 0; k < n; k++)
        {
            var b = batches[_order[k]];
            if (_surfaceOf[b.ObjectId - 1] >= 0) AddImage(scene, preview, _surfaceOf[b.ObjectId - 1], translucent: true);
            else AddMoved(preview, b.ObjectId, Scene3DPipeline.Translucent, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true, b.Element,
                          tie: TieOf(scene, b));
        }
        ClearFaded();
    }

    // ── brief-em3d-106 R-em3d106-2: the realistic colour pass ───────────────────────────────────────────────────────────

    /// <summary>What an object is to the realistic view (by ID − 1): a material drawn PBR (<see cref="Material"/>), something with no
    /// appearance drawn as the default view draws it (<see cref="Unlooked"/>: a wireframe object with no material), or a chrome row
    /// (≥ 0, a <see cref="Scene3DChrome"/>).</summary>
    private sbyte[] _chromeOf = [];
    private const sbyte Material = -1, Unlooked = -2;
    /// <summary>Per object (by ID − 1): a material object the realistic view draws translucent — its appearance transmits, or the
    /// document states a transparency (overview D12; a kind's default does not count).</summary>
    private bool[] _realTranslucent = [];

    /// <summary>The chrome row object <paramref name="o"/> belongs to, or null for a material (or an object with no look). One place:
    /// the plan and the gates ask here.</summary>
    public static Scene3DChrome? ChromeOfObject(Scene3DModel scene, Scene3DObject o)
    {
        if (o.Underlay || HasImageSurface(scene, o.Id)) return Scene3DChrome.Images;
        if (o.Kind == Scene3DKind.Air) return Scene3DChrome.AirBox;
        if (o.Kind == Scene3DKind.Port) return Scene3DChrome.Ports;
        if (o.Kind == Scene3DKind.Boundary)
            return o.Boundary is not null || o.Name == Scene3DBuilder.AirBoxEdgesName ? Scene3DChrome.AirBox : Scene3DChrome.Boundaries;
        return null;
    }

    private static bool HasImageSurface(Scene3DModel scene, uint id)
    {
        foreach (var ib in scene.ImageBatches) if (ib.ObjectId == id && ib.Surface) return true;
        return false;
    }

    /// <summary>The chrome row draw <paramref name="d"/> belongs to — null for a material's, a field's or an element's box. A scene line
    /// batch of an object in no other row is an <see cref="Scene3DChrome.Edges"/> draw — except a wireframe object's, whose edges are
    /// its whole drawing and not chrome.</summary>
    public static Scene3DChrome? ChromeOf(Scene3DModel scene, in Scene3DDraw d)
    {
        if (d.Pipeline == Scene3DPipeline.Grid) return Scene3DChrome.Grid;
        if (d.Buffer is Scene3DBuffer.Overlay0 or Scene3DBuffer.Overlay1 or Scene3DBuffer.Overlay2) return Scene3DChrome.Overlays;
        if (d.Pipeline is Scene3DPipeline.Edges or Scene3DPipeline.OnTop || scene.Object(d.Object) is not { } o) return null;
        return ChromeOfObject(scene, o) ?? (d.Buffer == Scene3DBuffer.SceneLines && !o.Wireframe ? Scene3DChrome.Edges : null);
    }

    private void SizeRealistic(Scene3DModel scene)
    {
        int n = scene.Objects.Length;
        _chromeOf = new sbyte[n];
        _realTranslucent = new bool[n];
        for (int k = 0; k < n; k++)
        {
            var o = scene.Objects[k];
            var row = ChromeOfObject(scene, o);
            // A wireframe object has no material: it is drawn as the default view draws it, its edges always (they are all it is).
            _chromeOf[k] = row is { } r ? (sbyte)r
                         : !o.Wireframe && o.AppearanceSlot >= 0 && o.AppearanceSlot < scene.Appearances.Length ? Material : Unlooked;
            if (_chromeOf[k] != Material) continue;
            bool stated = (o.Transparency is not null || o.Context) && (o.Rgba >> 24) < 255;
            _realTranslucent[k] = stated || scene.Appearances[o.AppearanceSlot].Transmission > 0;
        }
    }

    private bool Shows(Viewer3DViewState view, uint id)
        => _chromeOf[id - 1] is var c && (c < 0 || view.Look.Shows((Scene3DChrome)c));

    /// <summary>R-em3d106-2a/b — the realistic view's colour pass: the backdrop; every material object's triangles through Pbr (opaque)
    /// or PbrTranslucent (sorted with the translucent ones); each chrome row only when its Look key shows it, then drawn EXACTLY as the
    /// default view draws it (its own pipeline and colours); the field unchanged (rule 2).</summary>
    private void ColourRealistic(Scene3DModel scene, Viewer3DViewState view, Scene3DPreview? preview, Scene3DOverlay mesh,
                                 Scene3DOverlay section, Scene3DOverlay grid, Fields.Scene3DFieldGeometry? field, in Camera3D cam,
                                 int width, int height, bool flipY)
    {
        var look = view.Look;
        if (Transparent) Clear = (0, 0, 0);
        else if (look.Background is Design.ThreeD.C3dBackgroundKind.Gradient or Design.ThreeD.C3dBackgroundKind.Environment)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Backdrop, Scene3DBuffer.None, 0, 6);
        else
        {
            // a theme or solid background, darkened with the scene under Glow (R-em3d109-3a)
            var bg = look.Backdrop(look.Background == Design.ThreeD.C3dBackgroundKind.Solid ? look.Top : new Vector3(Clear.R, Clear.G, Clear.B));
            Clear = (bg.X, bg.Y, bg.Z);
        }

        var batches = scene.Batches;
        int owned = scene.OwnedBatches;
        for (int k = 0; k < owned; k++) OpaqueRealistic(scene, view, preview, batches[k], -1);
        for (int e = 0; e < _elements; e++)
        {
            if (_boxed[e]) continue;
            var el = scene.Elements[e];
            var g = scene.Groups[el.Group];
            if (Whole(scene, view, preview, el, g, pickPass: false) && !AnyFaded(el) && WholeIsPbr(scene, el))
            {
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Pbr, Scene3DBuffer.Scene, g.OpaqueFirst, g.OpaqueCount, ElementSlot(e));
                // The whole draw holds only the group's DEFAULT-opaque triangles: a part translucent by its kind's default whose
                // appearance does not transmit is opaque here, so it is drawn by itself (OpaqueRealistic skips every other one).
                for (int k = el.FirstBatch; k < el.FirstBatch + el.BatchCount; k++)
                    if (batches[k].Translucent) OpaqueRealistic(scene, view, preview, batches[k], e);
                continue;
            }
            for (int k = el.FirstBatch; k < el.FirstBatch + el.BatchCount; k++) OpaqueRealistic(scene, view, preview, batches[k], e);
        }
        // the field (overview rule 2 — its colour is a datum): Exact and Glow through fs_field unchanged, blended only below full
        // opacity; Lit (opt-in, and the picture says so) through fs_field_lit, which only ever lightens a colour (brief 109)
        var fieldPipeline = look.FieldStyle == Design.ThreeD.C3dFieldStyle.Lit ? Scene3DPipeline.FieldLit
                          : look.FieldBlends ? Scene3DPipeline.FieldBlend : Scene3DPipeline.Field;
        if (view.ShowField && field is { Vertices.Length: > 0 } f)
            foreach (var r in f.Layers)
                if (r.Count > 0 && r.First >= 0 && r.First + r.Count <= f.Vertices.Length)
                {
                    Add(ref Draws, ref DrawCount, fieldPipeline, Scene3DBuffer.Field, r.First, r.Count, FieldSlot(r.Layer), Scene3DDepthTie.Field);
                    FieldNormals |= fieldPipeline == Scene3DPipeline.FieldLit;
                }
        // brief-em3d-107 R-em3d107-3 — the ground, after everything opaque (which hides it) and before anything translucent
        if (GroundDrawn) Add(ref Draws, ref DrawCount, Scene3DPipeline.Ground, Scene3DBuffer.None, 0, 6);
        // the drawing grid, before the lines and overlays (which draw over it) and followed by the hovered object drawn again
        GridSpacing = default;
        if (look.Shows(Scene3DChrome.Grid) && view.DrawingGrid is { Visible: true } dg)
        {
            GridSpacing = PlaneGrid.Fill(Uniforms.AsSpan(GridAt), scene, cam, dg, width, height, flipY);
            if (GridSpacing.MinorDbu > 0)
            {
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Grid, Scene3DBuffer.None, 0, 6);
                GridFrames++;
            }
        }
        else Uniforms.AsSpan(GridAt, PlaneGrid.Floats).Clear();
        if (GridSpacing.MinorDbu > 0) HoverOverGrid(scene, view, preview, realistic: true);
        // A wireframe object (no material) is drawn as its edges ALONE — its faces are alpha 0 — so they are its body, not the Edges
        // row: hidden with that row, an imported part with no material vanished but for its hover highlight.
        bool edges = look.Shows(Scene3DChrome.Edges);
        foreach (var lb in scene.LineBatches)
            if (view.IsVisible(lb.ObjectId)
                && (_chromeOf[lb.ObjectId - 1] >= 0 ? Shows(view, lb.ObjectId) : edges || scene.Objects[lb.ObjectId - 1].Wireframe))
                AddMoved(preview, lb.ObjectId, Scene3DPipeline.Lines, Scene3DBuffer.SceneLines, lb.FirstVertex, lb.VertexCount, identity: true);
        // an element over the triangle budget stands in for geometry, not chrome: its box is drawn as the default view draws it
        if (LodBoxedElements > 0)
            for (int e = 0; e < _elements; e++)
                if (_boxed[e])
                    Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.SceneLines, scene.UnitBox.FirstVertex, scene.UnitBox.VertexCount, BoxSlot(e));
        if (look.Shows(Scene3DChrome.Overlays))
        {
            if (view.ShowMesh && mesh.Lines.Length > 0)
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay0, 0, mesh.Lines.Length);
            if (view.ShowMeshSection && section.Lines.Length > 0)
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay1, 0, section.Lines.Length);
            if (view.ShowGrid && grid.Lines.Length > 0)
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay2, 0, grid.Lines.Length);
        }

        // Translucent, back to front: a material that transmits or states a transparency, a shown chrome object or one with no look
        // that is translucent in the default view, and an object selected in Object mode (faded).
        var eye = view.Camera.Eye;
        var forward = view.Camera.Forward;
        int n = 0;
        for (int i = 0; i < batches.Length; i++)
        {
            var b = batches[i];
            uint id = b.ObjectId;
            if (!view.IsDrawn(id) || !Shows(view, id)) continue;
            if (b.Element >= 0 && _boxed[b.Element]) continue;
            bool translucent = _chromeOf[id - 1] == Material ? _realTranslucent[id - 1] : b.Translucent;
            if (!(translucent || Faded(id))) continue;
            _order[n] = i;
            _keys[n] = -Vector3.Dot(scene.Objects[id - 1].Centroid - eye, forward);
            n++;
        }
        Array.Sort(_keys, _order, 0, n);
        for (int k = 0; k < n; k++)
        {
            var b = batches[_order[k]];
            if (_chromeOf[b.ObjectId - 1] == Material)
                AddMoved(preview, b.ObjectId, Scene3DPipeline.PbrTranslucent, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true,
                         b.Element, TieOf(scene, b));
            else if (_surfaceOf[b.ObjectId - 1] >= 0) AddImage(scene, preview, _surfaceOf[b.ObjectId - 1], translucent: true);
            else AddMoved(preview, b.ObjectId, Scene3DPipeline.Translucent, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true,
                          b.Element, TieOf(scene, b));
        }
        ClearFaded();
    }

    /// <summary>One batch's opaque draw in the realistic view: a material through Pbr; a shown chrome object or one with no look as the
    /// default view draws it.</summary>
    private void OpaqueRealistic(Scene3DModel scene, Viewer3DViewState view, Scene3DPreview? preview, in Scene3DBatch b, int element)
    {
        uint id = b.ObjectId;
        if (Faded(id) || !view.IsDrawn(id) || !Shows(view, id)) return;
        if (_chromeOf[id - 1] == Material)
        {
            if (!_realTranslucent[id - 1])
                AddMoved(preview, id, Scene3DPipeline.Pbr, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true, element, TieOf(scene, b));
            return;
        }
        if (b.Translucent) return;
        if (element < 0 && _surfaceOf[id - 1] >= 0) AddImage(scene, preview, _surfaceOf[id - 1], translucent: false);
        else AddMoved(preview, id, Scene3DPipeline.Opaque, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true, element, TieOf(scene, b));
    }

    /// <summary>Whether an element's one opaque draw (its group's default-opaque objects) is all material the realistic view draws
    /// opaque — else it is drawn object by object. Its default-translucent objects are not in that draw; the caller adds them.</summary>
    private bool WholeIsPbr(Scene3DModel scene, in Scene3DElement el)
    {
        for (uint id = el.FirstId; id < el.FirstId + (uint)el.Count; id++)
        {
            if (scene.Objects[id - 1].Translucent) continue;
            if (_chromeOf[id - 1] != Material || _realTranslucent[id - 1]) return false;
        }
        return true;
    }

    // ── brief-em3d-107: shadows, contact shading, the ground ─────────────────────────────────────────────────────────────

    /// <summary>R-em3d107-1a — this frame's casters into the key light's shadow map (<see cref="Scene3DPipeline.ShadowDepth"/>, the
    /// scene's buffer), and the map's side (0: no shadows this frame). The map's draws are listed every realistic frame, so the key can be
    /// compared; whether the backend actually RE-RENDERS the map is <see cref="ShadowPass"/>.</summary>
    public Scene3DDraw[] ShadowDraws = new Scene3DDraw[16];
    public int ShadowDrawCount;
    public int ShadowSize;

    /// <summary>R-em3d107-1b — what the shadow map depends on, hashed: the scene (a new generation or a patch is a new scene), the light's
    /// matrix (the key's direction — the Look's rotation — and the casters' bounds), the map's size, and each caster draw with any preview
    /// transform it is drawn under (a drag). Nothing about the camera: an orbit leaves it unchanged. 0 when there are no shadows.</summary>
    public long ShadowKey;

    /// <summary>R-em3d107-1b — whether the backend renders the shadow pass this frame: set by whoever owns the map (Viewer3DSession),
    /// only when <see cref="ShadowKey"/> differs from the key the map was last rendered with.</summary>
    public bool ShadowPass;

    /// <summary>R-em3d107-2 — whether this frame computes occlusion (the depth prepass of its <see cref="Scene3DPipeline.Pbr"/> draws, the
    /// horizon pass and its blur), and whether it draws the ground (and so puts it in the prepass too).</summary>
    public bool Occlusion, GroundDrawn;

    /// <summary>brief-em3d-109 R-em3d109-2b — whether this frame draws a <see cref="Scene3DPipeline.FieldLit"/> field, so the backend
    /// needs the field's normal stream (the session uploads it once per field geometry, and lets it go when no frame asks).</summary>
    public bool FieldNormals;

    /// <summary>R-em3d107-5b — a transparent picture: the target is cleared to (0, 0, 0, 0) and no backdrop is drawn.</summary>
    public bool Transparent { get; private set; }

    /// <summary>The light's window over the casters this frame (default when there are no shadows).</summary>
    public ShadowWindow ShadowWindow { get; private set; }

    /// <summary>R-em3d107-1c — the polygon offset the shadow pass draws with: the existing helper's push AWAY from the eye — here, from the
    /// light — so a lit face never shadows itself. The receiver-plane slope in fs_pbr handles the filter's wider reach. Its own
    /// slope factor: acne wants the larger one, and <see cref="BehindSlopeScale"/> was made small for what it leaks at an edge.</summary>
    public static (float Constant, float Slope, float Clamp) ShadowBias => (BehindDepthBias, ShadowSlopeScale, 0);

    /// <summary>The shadow pass's slope factor (<see cref="ShadowBias"/>).</summary>
    public const float ShadowSlopeScale = 2;

    private bool[] _caster = [];

    /// <summary>The casters, the light's window and the shadow key; the occlusion's and the ground's parameters; all into the look block.</summary>
    private void PlanLighting(Scene3DModel scene, Viewer3DViewState view, Scene3DPreview? preview, in Camera3D cam, int width, int height,
                              bool flipY, bool export, float pixelScale)
    {
        ShadowDrawCount = 0;
        ShadowSize = 0;
        ShadowKey = 0;
        ShadowPass = false;
        Occlusion = GroundDrawn = false;
        ShadowWindow = default;
        if (!Realistic) return;
        var look = view.Look;
        var env = view.Environment!;
        var u = Uniforms.AsSpan(LookAt + LightingAt, LookFloats - LightingAt);
        u.Clear();

        // What is drawn as a material, and of it what casts (R-em3d107-1d: not glass, Transmission ≥ 0.5; never chrome or a field).
        var light = look.Lighting(env);
        var (right, up, forward) = Shadows.Frame(light.KeyDirectionWorld);
        if (_caster.Length != scene.Objects.Length) _caster = new bool[scene.Objects.Length];
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        var llo = new Vector3(float.MaxValue);
        var lhi = new Vector3(float.MinValue);
        bool any = false, casts = false;
        for (int k = 0; k < scene.Objects.Length; k++)
        {
            var o = scene.Objects[k];
            bool drawn = _chromeOf[k] == Material && view.IsDrawn(o.Id);
            _caster[k] = drawn && scene.Appearances[o.AppearanceSlot].Transmission < Shadows.CasterTransmissionLimit;
            if (!drawn) continue;
            bool moving = preview?.IsMoving(o.Id) == true;
            int copies = moving ? Math.Min(preview!.Copies.Length, MaxPreviewCopies) : 0;
            for (int c = -1; c < copies; c++)
            {
                if (c < 0 && moving && !preview!.KeepOriginal) continue;
                for (int n = 0; n < 8; n++)
                {
                    var corner = new Vector3((n & 1) == 0 ? o.Min.X : o.Max.X, (n & 2) == 0 ? o.Min.Y : o.Max.Y, (n & 4) == 0 ? o.Min.Z : o.Max.Z);
                    if (c >= 0) corner = Vector3.Transform(corner, preview!.Copies[c]);
                    if (!float.IsFinite(corner.X) || !float.IsFinite(corner.Y) || !float.IsFinite(corner.Z)) continue;
                    lo = Vector3.Min(lo, corner); hi = Vector3.Max(hi, corner);
                    any = true;
                    if (!_caster[k]) continue;
                    var q = new Vector3(Vector3.Dot(corner, right), Vector3.Dot(corner, up), Vector3.Dot(corner, forward));
                    llo = Vector3.Min(llo, q); lhi = Vector3.Max(lhi, q);
                    casts = true;
                }
            }
        }
        if (!any) return;
        float sceneRadius = MathF.Max((scene.ContentMax - scene.ContentMin).Length() * 0.5f, 1e-12f);

        // R-em3d107-1 — the shadow map: fitted, listed, keyed.
        if (look.Shadows && casts && env.KeyRadiance.LengthSquared() > 0)
        {
            ShadowSize = export ? Shadows.ExportMapSize : Shadows.LiveMapSize;
            float kernel = Shadows.KernelRadius(env.KeyAngleDeg, sceneRadius);
            float half = 0.5f * MathF.Max(lhi.X - llo.X, lhi.Y - llo.Y);
            half = MathF.Max(half * (1 + 2 * Shadows.WindowMargin) + 2 * kernel, 1e-12f);
            float depthPad = Shadows.WindowMargin * MathF.Max(lhi.Z - llo.Z, half) + kernel;
            var w = new ShadowWindow(right, up, forward, 0.5f * (llo.X + lhi.X), 0.5f * (llo.Y + lhi.Y), half, llo.Z - depthPad, lhi.Z + depthPad);
            ShadowWindow = w;
            WriteMatrix(w.Matrix, u[..16]);
            float texel = 2 * half / ShadowSize;
            u[16] = 1; u[17] = kernel; u[18] = w.UvPerWorld; u[19] = w.DepthPerWorld;
            Put(u, 20, right, Shadows.NormalOffsetTexels * texel);
            // the key light's share of what a level floor receives: the ground's shadow takes away exactly that much
            var keyOnFloor = env.KeyRadiance * MathF.Max(light.KeyDirectionWorld.Z, 0);
            var envOnFloor = env.Irradiance(light.ToEnvironment(Vector3.UnitZ));
            float k = Lum(keyOnFloor), e = Lum(envOnFloor);
            Put(u, 24, up, k + e > 0 ? k / (k + e) : 0);
            Put(u, 28, forward, 0);
            ListShadowDraws(scene, preview);
            ShadowKey = KeyOf(scene, view.Clip, u[..16]);
        }

        // R-em3d107-2 — occlusion: its reach, and the view's ray a pixel's point is rebuilt from.
        Occlusion = look.AmbientOcclusion;
        float radius = Look.Occlusion.RadiusFraction * sceneRadius;
        u[32] = Occlusion ? 1 : 0; u[33] = radius; u[34] = Look.Occlusion.BlurReach * radius; u[35] = MathF.Max(pixelScale, 1);
        var (ro, rox, roy, rd, rdx, rdy) = PlaneGrid.Ray(cam, width, height, flipY);
        Put(u, 36, ro, 0); Put(u, 40, rox, 0); Put(u, 44, roy, 0); Put(u, 48, rd, 0); Put(u, 52, rdx, 0); Put(u, 56, rdy, 0);

        // R-em3d107-3 — the ground: under the lowest point drawn, never seen from below.
        float materialRadius = MathF.Max((hi - lo).Length() * 0.5f, 1e-12f);
        float z = lo.Z - Look.Ground.Drop * materialRadius;
        bool above = cam.Projection == Projection3D.Orthographic ? cam.Forward.Z < -1e-4f : cam.Eye.Z > z;
        GroundDrawn = look.Ground && above;
        if (GroundDrawn)
        {
            var c = (lo + hi) * 0.5f;
            u[60] = c.X; u[61] = c.Y; u[62] = z; u[63] = Look.Ground.RadiusScale * materialRadius;
        }

        static void Put(Span<float> u, int at, Vector3 v, float w) { u[at] = v.X; u[at + 1] = v.Y; u[at + 2] = v.Z; u[at + 3] = w; }
        static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    }

    /// <summary>Each caster's batches, as the colour pass would draw them (a moving one under the preview's copies, an element's under its
    /// slot) — every element whole or not, never boxed: the map does not depend on where the camera is.</summary>
    private void ListShadowDraws(Scene3DModel scene, Scene3DPreview? preview)
    {
        var batches = scene.Batches;
        for (int k = 0; k < scene.OwnedBatches; k++)
            if (_caster[batches[k].ObjectId - 1]) AddShadow(preview, batches[k], -1);
        for (int e = 0; e < _elements; e++)
        {
            var el = scene.Elements[e];
            for (int k = el.FirstBatch; k < el.FirstBatch + el.BatchCount; k++)
                if (_caster[batches[k].ObjectId - 1]) AddShadow(preview, batches[k], e);
        }
    }

    private void AddShadow(Scene3DPreview? preview, in Scene3DBatch b, int element)
    {
        int own = element >= 0 ? ElementSlot(element) : 0;
        uint id = b.ObjectId;
        if (preview is null || !preview.IsMoving(id))
        {
            Add(ref ShadowDraws, ref ShadowDrawCount, Scene3DPipeline.ShadowDepth, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, own, obj: id);
            return;
        }
        if (preview.KeepOriginal)
            Add(ref ShadowDraws, ref ShadowDrawCount, Scene3DPipeline.ShadowDepth, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, own, obj: id);
        int copies = Math.Min(preview.Copies.Length, MaxPreviewCopies);
        int start = element >= 0 ? ComboSlots(preview, element, copies) : _copyBase;
        for (int k = 0; k < copies; k++)
            Add(ref ShadowDraws, ref ShadowDrawCount, Scene3DPipeline.ShadowDepth, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, start + k, obj: id);
    }

    /// <summary>R-em3d107-1b — the shadow key: the scene, the light's matrix, the map's size, and every caster draw with the contents of
    /// any per-frame transform slot (a preview's copy) it is drawn under. An element's own slot is written with the scene, so the scene
    /// stands for it. The section plane too, while it is on: fs_depth discards what it cuts away, so moving it changes what casts.</summary>
    private long KeyOf(Scene3DModel scene, ClipPlane3D clip, ReadOnlySpan<float> lvp)
    {
        var h = new HashCode();
        h.Add(scene.Generation);
        h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(scene));
        h.Add(ShadowSize);
        h.Add(clip.Enabled);
        if (clip.Enabled) h.Add(clip.Equation);
        foreach (float f in lvp) h.Add(f);
        for (int i = 0; i < ShadowDrawCount; i++)
        {
            ref var d = ref ShadowDraws[i];
            h.Add(d.First); h.Add(d.Count); h.Add(d.Transform);
            if (d.Transform >= _copyBase)
                foreach (float f in Transforms.AsSpan(TransformFloats * d.Transform, TransformFloats)) h.Add(f);
        }
        long key = h.ToHashCode();
        return key == 0 ? 1 : key;
    }

    /// <summary>R-em3d106-3 — the look block (<see cref="LookFloats"/>): what fs_pbr and fs_backdrop read.</summary>
    private static void FillLook(Span<float> u, Viewer3DViewState view, in Camera3D cam, int width, int height, bool flipY)
    {
        var look = view.Look;
        var env = view.Environment!;
        var light = look.Lighting(env);
        u.Clear();
        // brief-em3d-109 — Glow lowers every lit surface's exposure (and the environment backdrop's) by GlowDim; the field reads none of it
        u[0] = look.SurfaceExposure; u[1] = look.Intensity; u[2] = light.Cos; u[3] = light.Sin;
        u[4] = look.Background switch
        {
            Design.ThreeD.C3dBackgroundKind.Gradient => 1,
            Design.ThreeD.C3dBackgroundKind.Environment => 2,
            _ => 0,
        };
        u[5] = Pbr.EnvironmentLevels - 1;
        u[7] = 1 - look.FieldOpacity;
        Put(u, 8, light.KeyDirectionWorld);
        Put(u, 12, env.KeyRadiance);
        Put(u, 16, look.Backdrop(look.Top)); u[19] = 1;
        Put(u, 20, look.Backdrop(look.Bottom)); u[23] = 1;
        // the backdrop's ray per clip position, as PlaneGrid.WriteRay writes the grid's: direction = bd + x·bdx + y·bdy
        float aspect = MathF.Max(1, width) / MathF.Max(1, height);
        float fov = cam.FovY > 0 && cam.FovY < MathF.PI ? cam.FovY : Camera3D.DefaultFovY;
        Put(u, 24, cam.Forward);
        if (cam.Projection != Projection3D.Orthographic)
        {
            float ty = MathF.Tan(fov * 0.5f);
            Put(u, 28, cam.Right * (ty * aspect));
            Put(u, 32, cam.Up * (ty * (flipY ? -1 : 1)));
        }
        for (int k = 0; k < 9; k++) Put(u, 36 + 4 * k, env.Sh[k]);

        static void Put(Span<float> u, int at, Vector3 v) { u[at] = v.X; u[at + 1] = v.Y; u[at + 2] = v.Z; }
    }

    /// <summary>brief-em3d-43 — the selection: its edges and the selected face on top.</summary>
    private void Selection(Scene3DModel scene, Viewer3DViewState view, Scene3DPreview? preview)
    {
        var batches = scene.Batches;
        // brief-em3d-43 — the selection, last: the edges of each selected object (Object mode) or of each
        // object with a selected face (Face mode), then those objects' triangles again for the selected
        // face drawn on top. The shaders pick out what is selected; the plan only chooses which batches.
        // brief-em3d-67 — an Edge-mode selection is drawn by the 2D overlay, like a vertex: no GPU batch changes.
        if (view.Mode is not (Scene3DSelectMode.Vertex or Scene3DSelectMode.Edge) && view.Selection.Length > 0)
        {
            int limit = Math.Min(view.Selection.Length, SelectionLimit);
            for (int k = 0; k < limit; k++)
            {
                uint id = view.Selection[k].Object;
                if (id < 1 || id > _marked.Length || _marked[id - 1] || !view.IsDrawn(id)) continue;
                _marked[id - 1] = true;
                // A moving object's outline follows its copies: the outline marks what is being placed.
                if (_edgeOf[id - 1] is int e and >= 0)
                {
                    var eb = scene.EdgeBatches[e];
                    int from = DrawCount;
                    AddMoved(preview, id, Scene3DPipeline.Edges, Scene3DBuffer.SceneLines, eb.FirstVertex, eb.VertexCount, identity: false, eb.Element);
                    Thicken(from);
                }
                if (view.Mode == Scene3DSelectMode.Face && _batchOf[id - 1] is int t and >= 0)
                    for (; t < batches.Length && batches[t].ObjectId == id; t++)
                    {
                        var tb = batches[t];
                        AddMoved(preview, id, Scene3DPipeline.OnTop, Scene3DBuffer.Scene, tb.FirstIndex, tb.IndexCount, identity: false, tb.Element);
                    }
            }
            for (int k = 0; k < limit; k++)
            {
                uint id = view.Selection[k].Object;
                if (id >= 1 && id <= _marked.Length) _marked[id - 1] = false;
            }
            for (int k = 0; k < _edgeSlotsUsed; k++) _edgeSlotsOf[_edgeSlotKeys[k]] = -1;
            _edgeSlotsUsed = 0;
        }
    }

    /// <summary>3D editor bugs round 3 — marks the objects selected in Object mode (the first <see cref="SelectionLimit"/>, the
    /// ones the shader fades) for this frame.</summary>
    private void MarkFaded(Viewer3DViewState view)
    {
        _fadedCount = 0;
        if (view.Mode != Scene3DSelectMode.Object) return;
        int limit = Math.Min(view.Selection.Length, SelectionLimit);
        for (int k = 0; k < limit; k++)
        {
            uint id = view.Selection[k].Object;
            if (id < 1 || id > _faded.Length || _faded[id - 1]) continue;
            _faded[id - 1] = true;
            _fadedIds[_fadedCount++] = (int)id - 1;
        }
    }

    private void ClearFaded()
    {
        for (int k = 0; k < _fadedCount; k++) _faded[_fadedIds[k]] = false;
        _fadedCount = 0;
    }

    private bool Faded(uint id) => _fadedCount > 0 && id >= 1 && id <= _faded.Length && _faded[id - 1];

    /// <summary>This frame's hovered object (0 for none, or when the hover draws no tint) and the rest of the group a click on it
    /// takes (Object mode) — what the uniforms tint. Set by <see cref="Plan"/>.</summary>
    private uint _hoverId;
    private uint[] _hoverGroup = [];

    private bool IsHoverTinted(uint id) => id != 0 && (id == _hoverId || Array.IndexOf(_hoverGroup, id) >= 0);

    /// <summary>
    /// The hovered object's opaque triangles drawn a second time, right after the drawing grid: where the object is the nearest
    /// surface the second draw lands on its own depth and passes LessEqual, so its tint covers the grid's lines; anywhere
    /// something else is in front it fails as the first draw did. The grid writes no depth, so nothing else changes. Owner rule
    /// (2026-10-28): the hover highlight wins over the grid, as the grid wins over a face lying in its plane (fs_grid's bias).
    /// </summary>
    private void HoverOverGrid(Scene3DModel scene, Viewer3DViewState view, Scene3DPreview? preview, bool realistic)
    {
        if (_hoverId == 0) return;
        var batches = scene.Batches;
        for (int k = 0; k < batches.Length; k++)
        {
            var b = batches[k];
            if (!IsHoverTinted(b.ObjectId) || b.Translucent || Faded(b.ObjectId) || !view.IsDrawn(b.ObjectId)) continue;
            if (b.Element >= 0 && _boxed[b.Element]) continue;
            if (_surfaceOf[b.ObjectId - 1] >= 0) continue;   // an image sheet's colour is its image
            if (realistic) OpaqueRealistic(scene, view, preview, b, b.Element);
            else AddMoved(preview, b.ObjectId, Scene3DPipeline.Opaque, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount, identity: true,
                          b.Element, TieOf(scene, b));
        }
    }

    private bool AnyFaded(in Scene3DElement el)
    {
        if (_fadedCount == 0) return false;
        for (uint id = el.FirstId; id < el.FirstId + (uint)el.Count; id++)
            if (Faded(id)) return true;
        return false;
    }

    /// <summary>3D editor bugs round 3 — the edge draws from <paramref name="from"/> on, drawn again one pixel right, one
    /// down and one diagonally: each under a slot copying its own (matrix and id offset) with the pass in <c>id.y</c>. A
    /// slot's offset copies are made once a frame, however many draws share it.</summary>
    private void Thicken(int from)
    {
        int to = DrawCount;
        for (int k = from; k < to; k++)
        {
            var d = Draws[k];
            int first = EdgeSlots(d.Transform);
            for (int pass = 1; pass < EdgePasses; pass++)
                Add(ref Draws, ref DrawCount, d.Pipeline, d.Buffer, d.First, d.Count, first + pass - 1);
        }
    }

    private int EdgeSlots(int slot)
    {
        if (slot >= _edgeSlotsOf.Length)
        {
            int old = _edgeSlotsOf.Length;
            Array.Resize(ref _edgeSlotsOf, Math.Max(slot + 1, Math.Max(16, 2 * old)));
            Array.Fill(_edgeSlotsOf, -1, old, _edgeSlotsOf.Length - old);
        }
        if (_edgeSlotsOf[slot] >= 0) return _edgeSlotsOf[slot];
        int start = _nextSlot;
        EnsureSlots(start + EdgePasses - 1);
        var m = ReadMatrix(slot, out uint idOff);
        for (int pass = 1; pass < EdgePasses; pass++)
        {
            WriteSlot(start + pass - 1, m, idOff);
            Transforms[TransformFloats * (start + pass - 1) + 17] = BitConverter.UInt32BitsToSingle((uint)pass);
        }
        _nextSlot = start + EdgePasses - 1;
        _edgeSlotsOf[slot] = start;
        if (_edgeSlotsUsed == _edgeSlotKeys.Length) Array.Resize(ref _edgeSlotKeys, 2 * _edgeSlotKeys.Length);
        _edgeSlotKeys[_edgeSlotsUsed++] = slot;
        return start;
    }

    /// <summary>Whether element <paramref name="el"/> draws its opaque triangles in ONE draw this frame: its group's opaque
    /// range is one contiguous run, every one of its opaque objects is drawn (visible and pickable, in the ID pass) and
    /// none of its objects is moving.</summary>
    private static bool Whole(Scene3DModel scene, Viewer3DViewState view, Scene3DPreview? preview, in Scene3DElement el,
                              in Scene3DInstanceGroup g, bool pickPass)
    {
        if (g.OpaqueCount <= 0 || el.Count != g.Count) return false;
        for (uint id = el.FirstId; id < el.FirstId + (uint)el.Count; id++)
        {
            if (preview?.IsMoving(id) == true) return false;
            // Only the opaque objects are in the one draw; a translucent one is drawn (and sorted) on its own either way.
            if (scene.Objects[id - 1].Translucent) continue;
            // 3D editor bugs round 9 — the one draw carries one polygon offset: an element holding an object that ties
            // otherwise (a via, an opaque dielectric) is drawn object by object.
            if (TieOf(scene, id) != Scene3DDepthTie.None) return false;
            if (pickPass ? !view.IsVisible(id) || !scene.Objects[id - 1].Pickable : !view.IsDrawn(id)) return false;
        }
        return true;
    }

    /// <summary>R-em3d48-3c — which elements are over the triangle budget: none while the whole scene fits; else the
    /// farthest from the eye, until the rest fits.</summary>
    private void ChooseDetail(Scene3DModel scene, Viewer3DViewState view)
    {
        LodBoxedElements = 0;
        if (_elements == 0) return;
        Array.Clear(_boxed);
        long room = TriangleBudget - _ownedTriangles;
        if (_elementTriangles <= room) return;
        var eye = view.Camera.Eye;
        for (int e = 0; e < _elements; e++)
        {
            var el = scene.Elements[e];
            var g = scene.Groups[el.Group];
            var c = (g.Min + g.Max) * 0.5f + el.Offset;
            _elemKeys[e] = Vector3.DistanceSquared(c, eye);
            _elemOrder[e] = e;
        }
        Array.Sort(_elemKeys, _elemOrder, 0, _elements);
        long used = 0;
        for (int k = 0; k < _elements; k++)
        {
            int e = _elemOrder[k];
            used += scene.Groups[scene.Elements[e].Group].Triangles;
            if (used > room) { _boxed[e] = true; LodBoxedElements++; }
        }
    }

    /// <summary>brief-em3d-101 — object <paramref name="k"/>'s (by ID − 1) image surface batch (an index into
    /// <see cref="Scene3DModel.ImageBatches"/>), or −1.</summary>
    private int[] _surfaceOf = [];

    /// <summary>brief-em3d-101 — image batch <paramref name="k"/>, drawn in the opaque or the translucent pass, moved with its object
    /// by a drag's preview like any object's draw.</summary>
    private void AddImage(Scene3DModel scene, Scene3DPreview? preview, int k, bool translucent)
    {
        var ib = scene.ImageBatches[k];
        int from = DrawCount;
        AddMoved(preview, ib.ObjectId, translucent ? Scene3DPipeline.ImageTranslucent : Scene3DPipeline.Image, Scene3DBuffer.Image,
                 ib.FirstVertex, ib.VertexCount, identity: true,
                 tie: ib.OnFace ? Scene3DDepthTie.FaceImage : TieOf(scene, ib.ObjectId));
        for (int d = from; d < DrawCount; d++) Draws[d].Texture = ib.Image;
    }

    private void Size(Scene3DModel scene)
    {
        int need = scene.Batches.Length + scene.LineBatches.Length + scene.ImageBatches.Length + 6 + (1 + EdgePasses) * Math.Min(scene.Objects.Length, SelectionLimit);
        if (Draws.Length < need) Draws = new Scene3DDraw[need];
        if (PickDraws.Length < need) PickDraws = new Scene3DDraw[need];
        if (ShadowDraws.Length < scene.Batches.Length + 1) ShadowDraws = new Scene3DDraw[scene.Batches.Length + 1];
        _keys = new float[scene.Batches.Length];
        _order = new int[scene.Batches.Length];
        _edgeOf = new int[scene.Objects.Length];
        _batchOf = new int[scene.Objects.Length];
        _marked = new bool[scene.Objects.Length];
        _faded = new bool[scene.Objects.Length];
        _fadedCount = 0;
        Array.Fill(_edgeOf, -1);
        Array.Fill(_batchOf, -1);
        for (int k = 0; k < scene.EdgeBatches.Length; k++) _edgeOf[scene.EdgeBatches[k].ObjectId - 1] = k;
        for (int k = scene.Batches.Length - 1; k >= 0; k--) _batchOf[scene.Batches[k].ObjectId - 1] = k;   // an object's first
        _surfaceOf = new int[scene.Objects.Length];
        Array.Fill(_surfaceOf, -1);
        for (int k = 0; k < scene.ImageBatches.Length; k++)
            if (scene.ImageBatches[k].Surface) _surfaceOf[scene.ImageBatches[k].ObjectId - 1] = k;
        SizeRealistic(scene);

        // brief-em3d-48 — the element slots and their boxes' slots are written once per scene; a frame writes only a
        // preview's copies after them.
        _elements = ElementCount = scene.Elements.Length;
        _copyBase = 1 + 2 * _elements;
        _elemKeys = new float[_elements];
        _elemOrder = new int[_elements];
        _boxed = new bool[_elements];
        _comboStart = new int[_elements];
        Array.Fill(_comboStart, -1);
        _ownedTriangles = 0;
        for (int k = 0; k < scene.OwnedBatches; k++) _ownedTriangles += scene.Batches[k].IndexCount / 3;
        _elementTriangles = 0;
        foreach (var el in scene.Elements) _elementTriangles += scene.Groups[el.Group].Triangles;
        EnsureSlots(_copyBase);
        for (int e = 0; e < _elements; e++)
        {
            var el = scene.Elements[e];
            var g = scene.Groups[el.Group];
            WriteSlot(ElementSlot(e), Matrix4x4.CreateTranslation(el.Offset), el.IdOffset);
            var size = Vector3.Max(g.Max - g.Min, new Vector3(1e-12f));
            WriteSlot(BoxSlot(e), Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(g.Min + el.Offset), 0);
        }
        _sized = scene;
    }

    private static void Add(ref Scene3DDraw[] list, ref int count, Scene3DPipeline p, Scene3DBuffer buf, int first, int n, int transform = 0,
                            Scene3DDepthTie tie = Scene3DDepthTie.None, uint obj = 0)
    {
        if (count == list.Length) Array.Resize(ref list, list.Length * 2);
        list[count++] = new Scene3DDraw { Pipeline = p, Buffer = buf, First = first, Count = n, Transform = transform, Tie = tie, Texture = -1, Object = obj };
    }

    /// <summary>A draw of object <paramref name="id"/>'s batch: as it is when nothing moves it (under its element's
    /// transform, for an element's object); under each of the preview's copies when it moves — and also as it is when
    /// the preview keeps the original and <paramref name="identity"/> says the original is drawn in this pass.</summary>
    private void AddMoved(Scene3DPreview? preview, uint id, Scene3DPipeline p, Scene3DBuffer buf, int first, int n, bool identity,
                          int element = -1, Scene3DDepthTie tie = Scene3DDepthTie.None)
    {
        int own = element >= 0 ? ElementSlot(element) : 0;
        if (preview is null || !preview.IsMoving(id))
        {
            Add(ref Draws, ref DrawCount, p, buf, first, n, own, tie, id);
            return;
        }
        if (identity && preview.KeepOriginal) Add(ref Draws, ref DrawCount, p, buf, first, n, own, tie, id);
        int copies = Math.Min(preview.Copies.Length, MaxPreviewCopies);
        int start = element >= 0 ? ComboSlots(preview, element, copies) : _copyBase;
        for (int k = 0; k < copies; k++)
        {
            Add(ref Draws, ref DrawCount, p, buf, first, n, start + k, tie, id);
            TransformBytes += TransformBytesPerDraw;
        }
    }

    /// <summary>The slots of element <paramref name="element"/> under each of the preview's copies: the element's own
    /// translation, then the copy — made on first asking in a frame, after every slot before them.</summary>
    private int ComboSlots(Scene3DPreview preview, int element, int copies)
    {
        if (_comboStart[element] >= 0) return _comboStart[element];
        int start = _nextSlot;
        EnsureSlots(start + copies);
        var own = ReadMatrix(ElementSlot(element), out uint idOff);
        for (int k = 0; k < copies; k++) WriteSlot(start + k, own * preview.Copies[k], idOff);
        _nextSlot = start + copies;
        _comboStart[element] = start;
        _comboUsed.Add(element);
        return start;
    }

    /// <summary>brief-em3d-96 — the slot a field layer's draw takes: the identity, with the layer's colour block in id.y (vs_field
    /// hands it to fs_field). Made on first asking in a frame, all <see cref="Fields.FieldUniforms.MaxLayers"/> at once.</summary>
    private int FieldSlot(int layer)
    {
        if (_fieldSlots < 0)
        {
            _fieldSlots = _nextSlot;
            EnsureSlots(_fieldSlots + Fields.FieldUniforms.MaxLayers);
            for (int k = 0; k < Fields.FieldUniforms.MaxLayers; k++)
            {
                WriteSlot(_fieldSlots + k, Matrix4x4.Identity, 0);
                Transforms[TransformFloats * (_fieldSlots + k) + 17] = BitConverter.UInt32BitsToSingle((uint)k);
            }
            _nextSlot = _fieldSlots + Fields.FieldUniforms.MaxLayers;
        }
        return _fieldSlots + Math.Clamp(layer, 0, Fields.FieldUniforms.MaxLayers - 1);
    }

    private int _fieldSlots = -1;

    /// <summary>brief-em3d-96 — the colour block draw <paramref name="d"/> reads: its slot's id.y (a field draw's layer).</summary>
    public uint LayerOf(in Scene3DDraw d) => BitConverter.SingleToUInt32Bits(Transforms[TransformFloats * d.Transform + 17]);

    /// <summary>Slot 0 the identity, the element slots (written with the scene), then the preview's copies.</summary>
    private void WriteTransforms(Scene3DPreview? preview)
    {
        _fieldSlots = -1;
        foreach (int e in _comboUsed) _comboStart[e] = -1;
        _comboUsed.Clear();
        int copies = Math.Min(preview?.Copies.Length ?? 0, MaxPreviewCopies);
        EnsureSlots(_copyBase + copies);
        for (int k = 0; k < copies; k++) WriteSlot(_copyBase + k, preview!.Copies[k], 0);
        _nextSlot = _copyBase + copies;
    }

    /// <summary>Room for <paramref name="slots"/> slots; a grown array keeps what the old one held.</summary>
    private void EnsureSlots(int slots)
    {
        if (Transforms.Length >= TransformFloats * slots) return;
        var grown = IdentitySlots(Math.Max(slots, 2 * Transforms.Length / TransformFloats));
        Transforms.AsSpan().CopyTo(grown);
        Transforms = grown;
    }

    private void WriteSlot(int slot, in Matrix4x4 m, uint idOffset)
    {
        var dst = Transforms.AsSpan(TransformFloats * slot, TransformFloats);
        WriteMatrix(m, dst);
        dst[16] = BitConverter.UInt32BitsToSingle(idOffset);
        dst[17] = dst[18] = dst[19] = 0;
    }

    private Matrix4x4 ReadMatrix(int slot, out uint idOffset)
    {
        var s = Transforms.AsSpan(TransformFloats * slot, TransformFloats);
        idOffset = BitConverter.SingleToUInt32Bits(s[16]);
        return new Matrix4x4(s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8], s[9], s[10], s[11], s[12], s[13], s[14], s[15]);
    }

    private static float[] IdentitySlots(int slots)
    {
        var t = new float[TransformFloats * slots];
        for (int k = 0; k < slots; k++) WriteMatrix(Matrix4x4.Identity, t.AsSpan(TransformFloats * k, 16));
        return t;
    }

    /// <summary>A row-vector matrix into the WGSL layout: stored row-major, it is the column-vector transpose stored
    /// column-major — the camera's own convention (Camera3D.WriteViewProjection).</summary>
    public static void WriteMatrix(in Matrix4x4 m, Span<float> dst)
    {
        dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
        dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
        dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
        dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
    }

    /// <summary>
    /// 3D editor bugs round 2 — the camera a frame is drawn with: the view's own, its depth range widened to take in a
    /// drag's preview. Near and far bracket the SCENE's sphere (Camera3D.DepthRange), and a preview copy is drawn where
    /// the scene is not yet — so an object dragged past the sphere's rim was cut by the near or far plane and its live
    /// ghost looked clipped beyond some distance. The sphere is grown for this frame only; the view's camera is not
    /// written, so nothing accumulates once the drag ends.
    /// <para>And in perspective, a floor under the near plane (<see cref="Camera3D.NearFloor"/>) from the box of what this frame
    /// draws — an object hidden, or chrome the realistic Look leaves out (the air box), is not in it; a preview copy is. Every point
    /// of that box is at least its distance from the eye, and a point inside the frustum lies at most the corner ray's angle off the
    /// view's axis, so none is nearer along the view than that distance times the angle's cosine. A solver overlay (the mesh, its
    /// section, the grid) can reach the air box's walls, so a frame showing one keeps the sphere's rule alone.</para>
    /// </summary>
    public static Camera3D DepthCamera(Scene3DModel scene, Viewer3DViewState view, int width = 0, int height = 0)
    {
        var cam = view.Camera;
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        bool real = view.DrawsRealistic;
        for (int i = 0; i < scene.Objects.Length; i++)
        {
            var o = scene.Objects[i];
            if (!view.IsVisible(o.Id)) continue;
            if (real && ChromeOfObject(scene, o) is { } row && !view.Look.Shows(row)) continue;
            lo = Vector3.Min(lo, o.Min);
            hi = Vector3.Max(hi, o.Max);
        }
        // An array's elements past its prototype are drawn from the prototype's triangles, offset: bound each one.
        foreach (var el in scene.Elements)
        {
            var g = scene.Groups[el.Group];
            lo = Vector3.Min(lo, g.Min + el.Offset);
            hi = Vector3.Max(hi, g.Max + el.Offset);
        }
        if (view.Preview is { } p && p.Copies.Length > 0)
        {
            var slo = cam.SceneCentre - new Vector3(cam.SceneRadius);
            var shi = cam.SceneCentre + new Vector3(cam.SceneRadius);
            bool grew = false;
            int n = Math.Min(p.Moving.Length, scene.Objects.Length);
            int copies = Math.Min(p.Copies.Length, MaxPreviewCopies);
            for (int i = 0; i < n; i++)
            {
                if (!p.Moving[i]) continue;
                var o = scene.Objects[i];
                for (int k = 0; k < copies; k++)
                    for (int c = 0; c < 8; c++)
                    {
                        var corner = new Vector3((c & 1) == 0 ? o.Min.X : o.Max.X, (c & 2) == 0 ? o.Min.Y : o.Max.Y, (c & 4) == 0 ? o.Min.Z : o.Max.Z);
                        var q = Vector3.Transform(corner, p.Copies[k]);
                        if (!float.IsFinite(q.X) || !float.IsFinite(q.Y) || !float.IsFinite(q.Z)) continue;
                        slo = Vector3.Min(slo, q); shi = Vector3.Max(shi, q);
                        lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q);
                        grew = true;
                    }
            }
            if (grew)
            {
                cam.SceneCentre = (slo + shi) * 0.5f;
                cam.SceneRadius = MathF.Max(cam.SceneRadius, (shi - slo).Length() * 0.5f);
            }
        }
        cam.NearFloor = 0;
        if (cam.Projection != Projection3D.Orthographic && lo.X <= hi.X && !(view.ShowMesh || view.ShowMeshSection || view.ShowGrid))
        {
            var eye = cam.Eye;
            float d = Vector3.Distance(eye, Vector3.Clamp(eye, lo, hi));
            float ty = MathF.Tan((cam.FovY > 0 && cam.FovY < MathF.PI ? cam.FovY : Camera3D.DefaultFovY) * 0.5f);
            float tx = ty * MathF.Max(1, width) / MathF.Max(1, height);
            // A frame of unknown shape is taken as wide as the clamp allows: the corner ray's angle only grows with the aspect.
            if (width <= 0 || height <= 0) tx = ty * 20;
            cam.NearFloor = NearFloorMargin * d / MathF.Sqrt(1 + tx * tx + ty * ty);
        }
        return cam;
    }

    /// <summary>The share of the drawn box's bound the near plane is put at (<see cref="DepthCamera"/>): what float rounding of a
    /// vertex's depth cannot cross.</summary>
    private const float NearFloorMargin = 0.9f;

    private static void Fill(float[] u, Viewer3DViewState view, in Camera3D cam, int w, int h, bool flipY, float px, float py, uint flags, int pickSize = 1,
                             bool quiet = false)
    {
        cam.WriteViewProjection(u.AsSpan(0, 16), w, h, flipY, px, py, pickSize);
        var eye = cam.Eye;
        if (cam.Projection == Projection3D.Orthographic)
        {
            // An orthographic eye is at infinity: shade by the view direction, not a point.
            var back = cam.Back;
            eye = cam.Target + back * (cam.Distance * 1e4f);
        }
        u[16] = eye.X; u[17] = eye.Y; u[18] = eye.Z; u[19] = 1;
        var c = view.Clip.Equation;
        u[20] = c.X; u[21] = c.Y; u[22] = c.Z; u[23] = c.W;
        var bits = MemoryMarshal.Cast<float, uint>(u.AsSpan());
        // brief-em3d-106 R-em3d106-2c — a realistic PICTURE (quiet) draws no hover and no selection.
        bool noHover = quiet || view.HoverHidden;
        bits[24] = noHover ? 0 : view.Hovered;
        bits[25] = noHover || view.HoveredFace < 0 ? Scene3DVertex.NoFace : (uint)view.HoveredFace;
        bits[26] = flags;
        // brief-em3d-67 — the shaders know three modes; Edge mode draws as Vertex mode does (its highlight is the overlay's).
        bits[27] = (uint)(view.Mode == Scene3DSelectMode.Edge ? Scene3DSelectMode.Vertex : view.Mode);
        int nsel = quiet ? 0 : Math.Min(view.Selection.Length, SelectionLimit);
        bits[28] = (uint)nsel;
        // 3D editor bugs round 3 — clip units per pixel (x, y), for the vertex shader's pixel offset of a thickened edge.
        u[29] = w > 0 ? 2f / w : 0;
        u[30] = h > 0 ? 2f / h : 0;
        // 3D editor groups — the rest of the hovered object's group follows the selection in the same slots (Object mode only).
        int nhov = noHover || view.Mode != Scene3DSelectMode.Object ? 0 : Math.Min(view.HoverGroup.Length, SelectionLimit - nsel);
        bits[31] = (uint)nhov;
        for (int k = 0; k < SelectionLimit; k++)
        {
            bool on = k < nsel;
            bool hov = !on && k < nsel + nhov;
            bits[SelectionAt + 2 * k] = on ? view.Selection[k].Object : hov ? view.HoverGroup[k - nsel] : 0;
            bits[SelectionAt + 2 * k + 1] = on && view.Selection[k].Face >= 0 ? (uint)view.Selection[k].Face : Scene3DVertex.NoFace;
        }
        view.Field.AsSpan().CopyTo(u.AsSpan(FieldAt));
    }
}
