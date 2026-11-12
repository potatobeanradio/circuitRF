// HZN-1 R-hzn1-1 — typed views over a `.hzn`'s JSON tree.
//
// A view is a window onto the tree, never a second copy of it: every getter reads the tree and
// every setter writes it, so whatever a view does not expose is still in the tree, untouched, when
// the file is written. Each view hands out its object as `Json` for anything it does not name.
//
// What each key means is recorded in src/Design/RESOLVED.md §HZN, from Hertzon's documentation, its
// example files and black-box runs, never from its source (brief-hzn-0 D1). A key that is absent, or
// holds a different JSON kind than expected, reads as null: a view never guesses a default.

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>A design's kind: a 3D model, or a circuit (a schematic of components and wires).</summary>
public enum HznDesignKind { ThreeD, Circuit }

/// <summary>A JSON object of names to string expressions — variables, or a step's or setup's props —
/// in file order.</summary>
public sealed class HznStringMap
{
    private readonly HznJsonObject _owner;
    private readonly string _key;

    internal HznStringMap(HznJsonObject owner, string key)
    {
        _owner = owner;
        _key = key;
    }

    /// <summary>The object itself; null while the key is absent.</summary>
    public HznJsonObject? Json => _owner.GetObject(_key);

    public IReadOnlyList<string> Names => Json is { } o ? [.. o.Keys] : [];

    public int Count => Json?.Count ?? 0;

    public bool Contains(string name) => Json?.ContainsKey(name) ?? false;

    /// <summary>The expression a name holds; null when it is absent or not a string. Setting creates
    /// the object if the key was absent, and a new name goes where sibling objects put it.</summary>
    public string? this[string name]
    {
        get => Json?.GetString(name);
        set
        {
            var o = Json;
            if (o is null)
            {
                o = new HznJsonObject();
                _owner.Set(_key, o);
            }
            o.SetString(name, value);
        }
    }

    public bool Remove(string name) => Json?.Remove(name) ?? false;
}

/// <summary>The project: the root of a <c>.hzn</c>.</summary>
public sealed class HznProject(HznJsonObject json)
{
    public HznJsonObject Json { get; } = json;

    /// <summary><c>format</c> — <c>"hertzon-project"</c> on every file <see cref="HznFile"/> opens.</summary>
    public string? Format => Json.GetString("format");

    /// <summary><c>version</c>, as written.</summary>
    public long? Version => Json["version"] is HznJsonScalar s && s.TryGetInt64(out long v) ? v : null;

    public string? Name
    {
        get => Json.GetString("name");
        set => Json.SetString("name", value);
    }

    /// <summary>Project variables (<c>hertzon-cli</c> reports them with a leading <c>$</c>).</summary>
    public HznStringMap Variables => new(Json, "variables");

    /// <summary>Project materials, in file order.</summary>
    public IReadOnlyList<HznMaterial> Materials => HznViews.Objects(Json, "materials", o => new HznMaterial(o));

    /// <summary>Every design, 3D and circuit, in file order.</summary>
    public IReadOnlyList<HznDesign> Designs => HznViews.Objects(Json, "designs", o => new HznDesign(o));
}

/// <summary>A project material. Its properties are string expressions.</summary>
public sealed class HznMaterial(HznJsonObject json)
{
    public HznJsonObject Json { get; } = json;

    public string? Name
    {
        get => Json.GetString("name");
        set => Json.SetString("name", value);
    }
}

/// <summary>One design in a project.</summary>
public sealed class HznDesign(HznJsonObject json)
{
    public HznJsonObject Json { get; } = json;

    public string? Name
    {
        get => Json.GetString("name");
        set => Json.SetString("name", value);
    }

    /// <summary><c>kind</c>: <c>"circuit"</c> is a circuit; absent or anything else is a 3D design.</summary>
    public HznDesignKind Kind =>
        string.Equals(Json.GetString("kind"), "circuit", StringComparison.Ordinal) ? HznDesignKind.Circuit : HznDesignKind.ThreeD;

    /// <summary><c>units</c> — the unit of a bare number in this design's expressions.</summary>
    public string? Units
    {
        get => Json.GetString("units");
        set => Json.SetString("units", value);
    }

    public HznStringMap Variables => new(Json, "variables");

    public IReadOnlyList<HznObject> Objects => HznViews.Objects(Json, "objects", o => new HznObject(o));

    public IReadOnlyList<HznAssignment> Boundaries => HznViews.Objects(Json, "boundaries", o => new HznAssignment(o));

    public IReadOnlyList<HznAssignment> Excitations => HznViews.Objects(Json, "excitations", o => new HznAssignment(o));

    public IReadOnlyList<HznSetup> Setups => HznViews.Objects(Json, "setups", o => new HznSetup(o));

    public IReadOnlyList<HznCoordSystem> CoordSystems => HznViews.Objects(Json, "coord_systems", o => new HznCoordSystem(o));
}

/// <summary>A 3D object: a named modelling history and how it is treated.</summary>
public sealed class HznObject(HznJsonObject json)
{
    public HznJsonObject Json { get; } = json;

    public string? Name
    {
        get => Json.GetString("name");
        set => Json.SetString("name", value);
    }

    public string? Material
    {
        get => Json.GetString("material");
        set => Json.SetString("material", value);
    }

    /// <summary><c>consumed_by</c>: the object a boolean step used this one as a tool for; null when
    /// it is not consumed (written as JSON <c>null</c>).</summary>
    public string? ConsumedBy
    {
        get => Json.GetString("consumed_by");
        set => Json.SetString("consumed_by", value);
    }

    public string? Group
    {
        get => Json.GetString("group");
        set => Json.SetString("group", value);
    }

    public bool? Visible
    {
        get => Json.GetBool("visible");
        set => HznViews.SetBool(Json, "visible", value);
    }

    /// <summary><c>model</c>: whether the object takes part in the solve.</summary>
    public bool? Model
    {
        get => Json.GetBool("model");
        set => HznViews.SetBool(Json, "model", value);
    }

    public bool? SolveInside
    {
        get => Json.GetBool("solve_inside");
        set => HznViews.SetBool(Json, "solve_inside", value);
    }

    /// <summary>The modelling history, first step first: the first creates the shape, the rest
    /// change it.</summary>
    public IReadOnlyList<HznStep> History => HznViews.Objects(Json, "history", o => new HznStep(o));
}

/// <summary>One step of an object's modelling history.</summary>
public sealed class HznStep(HznJsonObject json)
{
    public HznJsonObject Json { get; } = json;

    /// <summary><c>kind</c> — <c>CreateBox</c>, <c>Subtract</c>, <c>Move</c>, …</summary>
    public string? Kind
    {
        get => Json.GetString("kind");
        set => Json.SetString("kind", value);
    }

    /// <summary>The step's properties, each a string expression.</summary>
    public HznStringMap Props => new(Json, "props");

    public bool? Suppressed
    {
        get => Json.GetBool("suppressed");
        set => HznViews.SetBool(Json, "suppressed", value);
    }
}

/// <summary>A boundary or an excitation: a kind and props, applied to whole objects or to faces
/// and edges by index.</summary>
public sealed class HznAssignment(HznJsonObject json)
{
    public HznJsonObject Json { get; } = json;

    public string? Name
    {
        get => Json.GetString("name");
        set => Json.SetString("name", value);
    }

    public string? Kind
    {
        get => Json.GetString("kind");
        set => Json.SetString("kind", value);
    }

    public HznStringMap Props => new(Json, "props");

    /// <summary>The whole objects it applies to.</summary>
    public IReadOnlyList<string> Objects =>
        Json.GetArray("objects") is { } a ? [.. a.Items.OfType<HznJsonScalar>().Select(s => s.StringValue).OfType<string>()] : [];

    /// <summary>The faces it applies to, by index, with their recorded signatures.</summary>
    public IReadOnlyList<HznFaceRef> Faces => HznViews.Objects(Json, "faces", o => new HznFaceRef(o));

    public bool? Enabled
    {
        get => Json.GetBool("enabled");
        set => HznViews.SetBool(Json, "enabled", value);
    }
}

/// <summary>
/// A face named by index: <c>{obj, face, sig}</c>. <c>sig</c> appears to be the face's centroid,
/// normal and area, in metres, recorded so the face can be found again (brief-hzn-0).
/// </summary>
public sealed class HznFaceRef(HznJsonObject json)
{
    public HznJsonObject Json { get; } = json;

    public string? Object => Json.GetString("obj");

    public long? Face => Json["face"] is HznJsonScalar s && s.TryGetInt64(out long v) ? v : null;

    /// <summary>The signature's numbers, in file order; empty when absent.</summary>
    public IReadOnlyList<double> Signature =>
        Json.GetArray("sig") is { } a
            ? [.. a.Items.OfType<HznJsonScalar>().Select(s => s.TryGetDouble(out double d) ? d : double.NaN)]
            : [];
}

/// <summary>A solution setup. Its frequency sweeps stay in <see cref="Json"/> under <c>sweeps</c>.</summary>
public sealed class HznSetup(HznJsonObject json)
{
    public HznJsonObject Json { get; } = json;

    public string? Name
    {
        get => Json.GetString("name");
        set => Json.SetString("name", value);
    }

    public HznStringMap Props => new(Json, "props");
}

/// <summary>A coordinate system.</summary>
public sealed class HznCoordSystem(HznJsonObject json)
{
    public HznJsonObject Json { get; } = json;

    public string? Name
    {
        get => Json.GetString("name");
        set => Json.SetString("name", value);
    }

    public string? Kind => Json.GetString("kind");

    /// <summary>The system this one is placed in.</summary>
    public string? Reference => Json.GetString("reference");

    public HznStringMap Props => new(Json, "props");
}

internal static class HznViews
{
    /// <summary>Each object element of an array key, wrapped; any other element stays in the tree
    /// and is not listed.</summary>
    public static IReadOnlyList<T> Objects<T>(HznJsonObject owner, string key, Func<HznJsonObject, T> wrap) =>
        owner.GetArray(key) is { } a ? [.. a.Items.OfType<HznJsonObject>().Select(wrap)] : [];

    public static void SetBool(HznJsonObject owner, string key, bool? value) =>
        owner.Set(key, value is { } b ? HznJsonScalar.Bool(b) : HznJsonScalar.Null());
}
