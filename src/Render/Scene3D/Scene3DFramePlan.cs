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
}

/// <summary>Which buffer a draw reads: the scene's, or one of the overlay slots.</summary>
public enum Scene3DBuffer { Scene, SceneLines, Overlay0, Overlay1, Overlay2 }

/// <summary>One draw: <see cref="Count"/> indices (triangles) or vertices (lines) from <see cref="First"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Scene3DDraw
{
    public Scene3DPipeline Pipeline;
    public Scene3DBuffer Buffer;
    public int First, Count;
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
    public uint Selected;
    /// <summary>The cursor, device pixels from the top-left; negative when it is not over the view.</summary>
    public float CursorX = -1, CursorY = -1;
    public bool ShowMesh, ShowMeshSection, ShowGrid;
    public bool ShowAxisIndicator = true;
    /// <summary>A camera gesture moved the camera since the last frame.</summary>
    public bool Orbiting;
    public (float R, float G, float B) Background = (0.12f, 0.13f, 0.15f);

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
        if (Hovered > v.Length) Hovered = 0;
        if (Selected > v.Length) Selected = 0;
    }

    public bool IsVisible(uint id) => id >= 1 && id <= Visible.Length && Visible[id - 1];
}

/// <summary>One frame's uniform blocks and draw lists. Reused from frame to frame.</summary>
public sealed class Scene3DFramePlan
{
    /// <summary>Floats in the uniform block — the WGSL <c>U</c>: vp (16), eye (4), clip (4), hover,
    /// selection, flags, pad. 112 bytes.</summary>
    public const int UniformFloats = 28;
    public const int UniformBytes = UniformFloats * 4;

    /// <summary>Flag bits in the uniform block's <c>flags</c>.</summary>
    public const uint FlagClip = 1, FlagCapBackFaces = 2;

    public readonly float[] Uniforms = new float[UniformFloats];
    public readonly float[] PickUniforms = new float[UniformFloats];
    public Scene3DDraw[] Draws = new Scene3DDraw[16];
    public int DrawCount;
    public Scene3DDraw[] PickDraws = new Scene3DDraw[16];
    public int PickDrawCount;
    /// <summary>Whether this frame carries an ID pass, and the pixel it reads.</summary>
    public bool Pick;
    public int PickX, PickY;
    public int Width, Height;
    public (float R, float G, float B) Clear;
    public long SceneGeneration = -1;

    private float[] _keys = [];
    private int[] _order = [];
    private Scene3DModel? _sized;

    /// <summary>
    /// Plans a <paramref name="width"/> × <paramref name="height"/> frame of <paramref name="scene"/>.
    /// <paramref name="flipY"/> for an API whose framebuffer y runs down. <paramref name="pick"/> asks
    /// for an ID pass at the cursor. Allocates only when the scene changed.
    /// </summary>
    public void Plan(Scene3DModel scene, Viewer3DViewState view, int width, int height, bool flipY, bool pick,
                     Scene3DOverlay mesh, Scene3DOverlay section, Scene3DOverlay grid)
    {
        if (!ReferenceEquals(_sized, scene)) Size(scene);
        Width = width; Height = height;
        Clear = view.Background;
        SceneGeneration = scene.Generation;
        uint flags = view.Clip.Enabled ? FlagClip | FlagCapBackFaces : 0;
        Fill(Uniforms, view, width, height, flipY, -1, -1, flags);

        DrawCount = 0;
        foreach (var b in scene.Batches)
            if (!b.Translucent && view.IsVisible(b.ObjectId))
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Opaque, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount);
        foreach (var lb in scene.LineBatches)
            if (view.IsVisible(lb.ObjectId))
                Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.SceneLines, lb.FirstVertex, lb.VertexCount);
        if (view.ShowMesh && mesh.Lines.Length > 0)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay0, 0, mesh.Lines.Length);
        if (view.ShowMeshSection && view.Clip.Enabled && section.Lines.Length > 0)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay1, 0, section.Lines.Length);
        if (view.ShowGrid && grid.Lines.Length > 0)
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Lines, Scene3DBuffer.Overlay2, 0, grid.Lines.Length);

        // Translucent objects back to front, one draw each (brief 27 §2.4: per object, not per triangle).
        var eye = view.Camera.Eye;
        int n = 0;
        var batches = scene.Batches;
        for (int i = 0; i < batches.Length; i++)
        {
            if (!batches[i].Translucent || !view.IsVisible(batches[i].ObjectId)) continue;
            _order[n] = i;
            _keys[n] = -Vector3.DistanceSquared(scene.Objects[batches[i].ObjectId - 1].Centroid, eye);
            n++;
        }
        Array.Sort(_keys, _order, 0, n);
        for (int k = 0; k < n; k++)
        {
            var b = batches[_order[k]];
            Add(ref Draws, ref DrawCount, Scene3DPipeline.Translucent, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount);
        }

        Pick = pick && view.CursorX >= 0 && view.CursorY >= 0 && view.CursorX < width && view.CursorY < height;
        PickDrawCount = 0;
        if (Pick)
        {
            PickX = (int)view.CursorX; PickY = (int)view.CursorY;
            Fill(PickUniforms, view, width, height, flipY, PickX, PickY, view.Clip.Enabled ? FlagClip : 0);
            foreach (var b in batches)
                if (view.IsVisible(b.ObjectId) && scene.Objects[b.ObjectId - 1].Pickable)
                    Add(ref PickDraws, ref PickDrawCount, Scene3DPipeline.Pick, Scene3DBuffer.Scene, b.FirstIndex, b.IndexCount);
        }
    }

    private void Size(Scene3DModel scene)
    {
        int need = scene.Batches.Length + scene.LineBatches.Length + 4;
        if (Draws.Length < need) Draws = new Scene3DDraw[need];
        if (PickDraws.Length < need) PickDraws = new Scene3DDraw[need];
        _keys = new float[scene.Batches.Length];
        _order = new int[scene.Batches.Length];
        _sized = scene;
    }

    private static void Add(ref Scene3DDraw[] list, ref int count, Scene3DPipeline p, Scene3DBuffer buf, int first, int n)
    {
        if (count == list.Length) Array.Resize(ref list, list.Length * 2);
        list[count++] = new Scene3DDraw { Pipeline = p, Buffer = buf, First = first, Count = n };
    }

    private static void Fill(float[] u, Viewer3DViewState view, int w, int h, bool flipY, float px, float py, uint flags)
    {
        view.Camera.WriteViewProjection(u.AsSpan(0, 16), w, h, flipY, px, py);
        var eye = view.Camera.Eye;
        if (view.Camera.Projection == Projection3D.Orthographic)
        {
            // An orthographic eye is at infinity: shade by the view direction, not a point.
            var back = view.Camera.Back;
            eye = view.Camera.Target + back * (view.Camera.Distance * 1e4f);
        }
        u[16] = eye.X; u[17] = eye.Y; u[18] = eye.Z; u[19] = 1;
        var c = view.Clip.Equation;
        u[20] = c.X; u[21] = c.Y; u[22] = c.Z; u[23] = c.W;
        var bits = MemoryMarshal.Cast<float, uint>(u.AsSpan());
        bits[24] = view.Hovered;
        bits[25] = view.Selected;
        bits[26] = flags;
        bits[27] = 0;
    }
}
