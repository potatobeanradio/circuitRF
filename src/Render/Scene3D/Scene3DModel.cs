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
public enum Scene3DKind { Conductor, Dielectric, Air, Body, Wire, Via, Sheet, Port, Boundary }

/// <summary>A vertex, 20 bytes: position (scene-local metres), the object's ID, its colour (RGBA8).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Scene3DVertex(float x, float y, float z, uint id, uint rgba)
{
    public const int Stride = 20;
    public float X = x, Y = y, Z = z;
    public uint Id = id;
    /// <summary>R in the low byte, A in the high byte — RGBA8 unorm in memory order.</summary>
    public uint Rgba = rgba;

    public static uint Pack(byte r, byte g, byte b, byte a) => (uint)(r | (g << 8) | (b << 16) | (a << 24));
}

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

    /// <summary>Whether hover and click can land on it: everything but the air and the box's faces,
    /// which enclose everything else.</summary>
    public bool Pickable => Kind is not (Scene3DKind.Air or Scene3DKind.Boundary);
}

/// <summary>An object's triangles: <see cref="IndexCount"/> indices from <see cref="FirstIndex"/>.</summary>
public readonly record struct Scene3DBatch(uint ObjectId, int MaterialSlot, int FirstIndex, int IndexCount, bool Translucent);

/// <summary>An object's lines (a port's arrow, the box's edges): a line list of
/// <see cref="VertexCount"/> vertices from <see cref="FirstVertex"/> in the line buffer.</summary>
public readonly record struct Scene3DLineBatch(uint ObjectId, int FirstVertex, int VertexCount);

/// <summary>The scene. Immutable once built; a new generation is a new instance.</summary>
public sealed class Scene3DModel
{
    /// <summary>The regeneration number this scene was built for (R-em3d28-2d).</summary>
    public required long Generation { get; init; }
    /// <summary>World position of the scene-local origin, metres.</summary>
    public required (double X, double Y, double Z) Origin { get; init; }
    public required Scene3DVertex[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required Scene3DVertex[] LineVertices { get; init; }
    public required Scene3DObject[] Objects { get; init; }
    /// <summary>Opaque batches first, then translucent — each one object.</summary>
    public required Scene3DBatch[] Batches { get; init; }
    public required Scene3DLineBatch[] LineBatches { get; init; }
    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }
    /// <summary>The bounds of what is not air, box or boundary — what Fit frames.</summary>
    public required Vector3 ContentMin { get; init; }
    public required Vector3 ContentMax { get; init; }
    /// <summary>The problem the scene was built from — kept for the tooltip, the grid and the tree.</summary>
    public Em3dProblem? Problem { get; init; }
    /// <summary>The problem's notes and warnings, or the refusal when there is no problem.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public int TriangleCount => Indices.Length / 3;
    public long VertexBytes => (long)Vertices.Length * Scene3DVertex.Stride;
    public long IndexBytes => (long)Indices.Length * sizeof(uint);
    public long LineBytes => (long)LineVertices.Length * Scene3DVertex.Stride;

    /// <summary>The object with ID <paramref name="id"/>, or null (0, or out of range).</summary>
    public Scene3DObject? Object(uint id) => id >= 1 && id <= Objects.Length ? Objects[id - 1] : null;

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
