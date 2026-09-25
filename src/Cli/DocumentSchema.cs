using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The shape of one of circuitRF's JSON documents, GENERATED from the type the reader deserialises
/// into (AUT-10 R-aut10-3, R-aut10-4).
///
/// <para><b>Why generated rather than written.</b> Two of the formats a client must be able to
/// author have no page at all: an exercise that had to plot a result succeeded only because an
/// unrelated <c>.cdd</c> happened to exist on the machine to copy from, and a client in a fresh
/// workspace has nothing. A hand-written description of a 60-field DTO tree is a second copy of the
/// reader that goes stale silently — the exact failure this whole surface exists to remove — so the
/// page is a walk over the reader's OWN types, with every default read off a freshly-constructed
/// instance rather than transcribed.</para>
///
/// <para><b>What it deliberately does not claim.</b> It states the field names, their types, their
/// defaults and every legal member of an enum. It does not state what a field MEANS: where the type
/// carries no summary there is nothing here to read one from, and an invented meaning is worse than
/// none (R-aut6-8's rule, applied to a format instead of to a parameter). The prose chapter beside
/// each format answers that half.</para>
///
/// <para><b>Descent stops at the declaring assembly.</b> A property whose type lives elsewhere is
/// named and not expanded, because following it reaches the whole result model. Enums are the
/// exception and are expanded wherever they come from: their members ARE the answer, and a client
/// that cannot spell one writes a file the reader refuses.</para>
/// </summary>
internal static class DocumentSchema
{
    /// <summary>One JSON document format this reference can describe.</summary>
    /// <param name="Topic">The reference topic name.</param>
    /// <param name="Title">Its title in the topic list.</param>
    /// <param name="Extension">The file extension it describes.</param>
    /// <param name="Root">The type the reader deserialises the whole file into.</param>
    /// <param name="Preamble">The authored half: what the format is FOR and a minimal working
    /// example. Everything after it is generated.</param>
    internal sealed record Format(string Topic, string Title, string Extension, Type Root, string Preamble);

    // ── the formats ──────────────────────────────────────────────────────────

    /// <summary>
    /// The formats served as generated topics. All are here for the same reason: they are documents
    /// a client has to write and that <c>create</c> does not make (or makes only empty). The
    /// <c>.clay</c>/<c>.cem</c>/<c>.wBond</c> three are what EM and wirebond work needs headlessly,
    /// and docs/design/em-3d.md points at them as the authoring surface a 3D setup extends.
    /// </summary>
    public static readonly Format[] All =
    [
        new("data-display", "The .cdd data-display format", ".cdd",
            typeof(CircuitRF.Render.DataDisplay.DataDisplayConfig), CddPreamble),
        new("technology", "The .ctech technology format", ".ctech",
            typeof(CircuitRF.Design.Layout.CtechFile), CtechPreamble),
        new("layout", "The .clay layout format", ".clay",
            typeof(CircuitRF.Design.Layout.ClayFile), ClayPreamble),
        new("em-setup", "The .cem EM setup format", ".cem",
            typeof(CircuitRF.Design.Layout.Em.CemFile), CemPreamble),
        new("wbond", "The .wBond wirebond format", ".wBond",
            typeof(CircuitRF.WBond.WBondIo.WBondDocument), WBondPreamble),
    ];

    public static Format? Find(string topic)
        => All.FirstOrDefault(f => string.Equals(f.Topic, topic, StringComparison.OrdinalIgnoreCase));

    private const string CddPreamble = """
        A data display is a document, like a schematic. It is JSON, it is what `render` draws when
        given a .cdd, and this is how to write one.

        For ONE plot you do not need to write one at all: `circuitrf plot <result> -o out.svg
        --trace cube=S,i=2,j=1,y=db` draws it, and `--write-cdd out.cdd` hands you the document it
        built — which is a correct starting point to edit rather than a blank page.

        A display holds tabs; a tab holds plots; a plot holds traces; a trace names a cube in a
        result file and how to slice it. The result files themselves are NOT in the document: the
        `SourceAliases` map names them, and `render --data <file>` binds one at draw time. A .cdd
        whose sources do not resolve is a refusal, not an empty plot.

        This is a whole working display — one tab, one plot, |S21| in dB against frequency. It was
        written out, rendered and checked, not composed from the field list below:

            {
              "FormatVersion": 2,
              "SelectedDataSource": "pad.npy",
              "SourceAliases": { "pad.npy": "pad" },
              "ActiveTabIndex": 0,
              "Tabs": [
                {
                  "Name": "Tab 1",
                  "Plots": [
                    {
                      "Left": 0, "Top": 0, "Width": 800, "Height": 600,
                      "PlotType": "Rect",
                      "Traces": [
                        { "SourcePath": "pad.npy", "Row": 1, "Col": 0,
                          "MatrixType": "S", "YAxis": "Db" }
                      ]
                    }
                  ]
                }
              ]
            }

            circuitrf render pad.cdd --data pad.npy -o pad.svg

        Four things that are not obvious from the field list:

          * FormatVersion must be 2. A file saying anything else is refused, by design — there is
            no back-compatibility path in this format, and a silently migrated file is worse than
            a refusal.
          * A trace is NETWORK-bound when CubeName is null: Row and Col are ZERO-BASED, so S21 is
            Row 1, Col 0, and MatrixType picks S, Z or Y. It is CUBE-bound when CubeName names a
            cube in the source, and CubeSlice then pins or keeps each of that cube's axes. The two
            are not mixed, and a non-null Expression supersedes both.
          * Plot geometry is in logical pixels on a free canvas, not a grid. Left/Top/Width/Height
            are the plot's rectangle inside the tab, and a plot of zero width draws nothing.
          * SourcePath is the source's key in SourceAliases, not necessarily a path on disk —
            relative to the workspace's results root where the file lives under it, absolute
            otherwise. `render --data` supplies the actual file.

        Every field the reader understands follows, with its default. A field this list does not
        name is IGNORED by the reader rather than refused, so check a spelling here before writing
        it: System.Text.Json matches names case-insensitively and drops what it does not know.
        """;

    private const string CtechPreamble = """
        A technology says what a layout's shapes are MADE of: the drawing layers and their colours,
        the default grid and via sizes, the physical stackup an EM run solves, and the design rules
        `check` enforces. It is JSON, one file, and `new workspace` copies a shipped one into the
        workspace it creates — `--tech` names which, and an unknown name is a refusal LISTING the
        shipped ids, which is how to find out what they are. The copy lands in <workspace>/tech/.

        Writing one from scratch is rarely the right move: start from the copy in a workspace and
        change it. What a caller usually needs from this page is which field to change, and what a
        layer's Key means — the (Layer, Datatype) pair is the GDSII number pair, and it is the
        identity a shape stores. Renaming a layer keeps its shapes; renumbering it does not.

        A minimal technology with one drawing layer. This is a whole file, and `check` passes it:

            {
              "FormatVersion": 1,
              "Name": "One layer",
              "DefaultDisplayUnit": "Um",
              "DefaultSnapDbu": 5,
              "Layers": [
                {
                  "Key": { "Layer": 1, "Datatype": 0 },
                  "Name": "Metal1",
                  "Color": { "r": 224, "g": 176, "b": 64, "a": 255 },
                  "ZOrder": 8,
                  "Visible": true,
                  "Selectable": true,
                  "Purpose": "drawing"
                }
              ]
            }

        A DBU is circuitRF's integer layout unit; the workspace's own DbuPerUm decides what one is
        worth, so DefaultSnapDbu is not a length until that is known. An EM run needs the Stackup
        as well as the layers — a technology with layers and no stackup draws correctly and cannot
        be solved.

        DeviceRules and Constants are the geometric device-recognition deck, which `lvs --recognize`
        reads and nothing else does. It is for artwork carrying no instances: a rule's Body is a
        layer expression whose connected components are candidate devices, its Terminals are the
        layers a terminal may be on, and its Parameters are formulas over Length, Width, Area and
        Perimeter — all SI — plus the Constants this file declares. A technology stating none
        recognises nothing, which is the ordinary case and is not an error. `check` validates the
        deck, so a rule that will not read is refused before any run reads it.

        Every field the reader understands follows, with its default.
        """;

    private const string ClayPreamble = """
        A layout is a cell's physical artwork: shapes on drawing layers, instances of other cells,
        and the EM ports a .cem solves between. It is JSON, one file per cell, at
        <cell>/layout/<cell>.clay. `new cell <workspace> <name> --views layout` writes an empty one
        whose DbuPerMicron, DisplayUnit and SnapDbu come from the workspace's technology; start from
        that rather than a blank file.

        A 50 ohm microstrip through-line on the shipped pcb-2layer_RO4350B_20mil_1oz technology,
        10 mm long and 1.1 mm wide, with an EM port at each end. This is a whole file; `check`
        passes it, `render` draws it, and the em-setup topic's example solves it:

            {
              "FormatVersion": 1,
              "DbuPerMicron": 1000,
              "DisplayUnit": "Mm",
              "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X1": 0, "Y1": -550000, "X2": 10000000, "Y2": 550000 },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 0, "Y": 0, "Text": "1", "Height": 400000,
                  "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 10000000, "Y": 0, "Text": "2", "Height": 400000,
                  "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }

            circuitrf check  ws/thru/layout/thru.clay
            circuitrf render ws/thru/layout/thru.clay -o thru.svg

        Six things that are not obvious from the field list:

          * Every coordinate and size is an integer DBU. DbuPerMicron says what one is worth: at the
            default 1000, one DBU is one nanometre, so 1.1 mm is 1100000. y is UP.
          * A shape's Layer is the (Layer, Datatype) Key of a drawing layer in the technology, never
            its name. `reference technology` explains the Key; the workspace's own .ctech lists them.
          * "$type" picks the kind of shape and MUST be the first field of each shape. Written
            anywhere else, the whole file is refused with "must specify a type discriminator".
          * Poly, Curve and Path store their vertices as ONE flat list, x0, y0, x1, y1, ... A Poly's
            Holes are further flat lists of the same kind.
          * An EM port is a Label with IsPort true, placed ON the conductor's edge. Its Text is the
            port number. PortDirection is the way current flows INTO the metal (R0 = +x, R90 = +y,
            R180 = -x, R270 = -y); omitted, it is inferred from the geometry, and an ambiguous
            inference is refused at run time rather than guessed.
          * DisplayUnit and SnapDbu are editor conveniences. Nothing is computed from them.

        Every field the reader understands follows, with its default. Shapes are listed once per
        kind, each with the "$type" value that selects it.
        """;

    private const string CemPreamble = """
        An EM setup is one electromagnetic run: which layout, which conductor of the stackup carries
        the signal, the frequency plan, the ports' reference impedances and the mesh. It is JSON; by
        convention it lives at <cell>/em/<name>.cem. `circuitrf em <file.cem>` runs it with no other
        argument and writes exactly what the GUI's Simulate writes: <workspace>/results/<Name>.sNp
        and <Name>_em.npy (`-o` moves the Touchstone only).

        This solves the layout topic's microstrip through-line, 1 to 10 GHz at 4 points, in about
        5 seconds on a laptop; |S11| comes back below -35 dB. It is a whole file:

            {
              "FormatVersion": 1,
              "Name": "thru",
              "LayoutRef": "thru/layout/thru.clay",
              "SignalStackupLayerName": "Top Copper (1 oz)",
              "Frequency": {
                "StartExpr": "1", "StopExpr": "10", "NumPoints": 4,
                "Mode": "PointCount", "Kind": "Linear",
                "StartUnit": "GHz", "StopUnit": "GHz"
              },
              "DispersionCorrection": true,
              "AnalysisKind": "Planar"
            }

            circuitrf check ws
            circuitrf em    ws/thru/em/thru.cem

        Six things that are not obvious from the field list:

          * LayoutRef is relative to the WORKSPACE folder (the one holding .cws), not to the .cem.
            The technology is the layout's own, resolved from its workspace; a .cem names none.
          * SignalStackupLayerName is a STACKUP entry's Name ("Top Copper (1 oz)"), not a drawing
            layer's ("Top Copper"). The technology's Stackup lists them.
          * Frequency: StartExpr and StopExpr are expressions in StartUnit and StopUnit. Mode
            PointCount uses NumPoints; Mode StepSize uses StepExpr in StepUnit.
          * Port impedances: Port1Z0Real/Imag and Port2Z0Real/Imag cover two ports. For more, PortZ0s
            is a flat [re, im, re, im, ...] list in port-number order, and wins where present.
          * AnalysisKind: Planar is the full-wave planar solver; CrossSection is the 2-D per-unit-
            length solve of a uniform line; omitted means Auto, which picks between them and says
            which in the run's notes.
          * DispersionCorrection: a new setup made in the GUI writes true, but a file that OMITS it
            reads false. Write it. Every other omitted flag reads as the GUI's default.

        Every field the reader understands follows, with its default.
        """;

    private const string WBondPreamble = """
        A wirebond design is a set of named ARRAYS of bond wires, each wire a 3-D polyline. It is
        JSON, self-contained, and what the wBond editor saves. A schematic's wBond component usually
        CARRIES its wires embedded in its Design parameter; a hand-written netlist names a .wBond
        file instead.

        Two 1 mil gold wires, 1 mm span, 100 um apart, feet 10 mil above the ground plane and crests
        at 16 mil. This is a whole file:

            {
              "FormatVersion": 1,
              "Arrays": [
                {
                  "Name": "G1",
                  "Wires": [
                    { "DiameterNm": 25400, "Material": "Gold",
                      "Points": [ [0, 0, 254000], [254000, 0, 406400], [1016000, 0, 254000] ] },
                    { "DiameterNm": 25400, "Material": "Gold",
                      "Points": [ [0, 101600, 254000], [254000, 101600, 406400], [1016000, 101600, 254000] ] }
                  ]
                }
              ]
            }

        Simulated through a netlist (`reference components WBond` lists every parameter):

            Port:P1 in  0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            WBond:WB1 in out File="pair.wBond"
            analysis SP1 type=sparam start=1 stop=10 npts=4 Unit=GHz

        Six things that are not obvious from the field list:

          * Points are [x, y, z] in integer NANOMETRES, whatever unit the editor displays; z is
            measured from the ground plane at z = 0. Points[0] is where current ENTERS the wire,
            and reversing the list changes the sign of every mutual inductance it takes part in.
          * EVERY array in the file is two nets on the WBond instance, its input end then its output
            end, in the file's array order. The instance's Arrays parameter ("G1|G2") only RECORDS
            the order it was wired against, so a reordered file is reported rather than rewired.
          * Material names a metal: Gold, Aluminium, Copper and Silver are built in. A Materials list
            REPLACES the built-in metals rather than adding to them, so a file that defines one
            custom metal must also list every built-in one its wires name.
          * A relative File= resolves against the .cnl's own folder, exactly as an SnP's does, so
            the netlist above expects pair.wBond beside it.
          * Omitted fields take the editor's defaults: OperatingTempC 85 (degrees C), ground plane
            on, capacitance on, OvermoldEr 1 (air).
          * EmbeddedGeometry and ViewState are the editor's own; leave them out when writing a file.

        Every field the reader understands follows, with its default.
        """;

    // ── rendering ────────────────────────────────────────────────────────────

    /// <summary>The topic's text. Pure — the topic list and the resource listing measure it by
    /// rendering it, exactly as they measure the component catalogue.</summary>
    public static string Render(Format format)
    {
        var sb = new StringBuilder();
        sb.Append(format.Title).Append(" — ").Append(format.Extension).AppendLine();
        sb.AppendLine();
        sb.AppendLine(format.Preamble);
        sb.AppendLine();

        foreach (var (type, fields) in Walk(format.Root))
        {
            sb.AppendLine(type);
            foreach (var f in fields)
                sb.AppendLine($"  {f.Name,-24} {f.Type,-26} {f.Default}".TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The same walk as a document, for <c>--json</c>.</summary>
    public static IReadOnlyList<ReferenceSchemaTypeJson> Json(Format format)
        => [.. Walk(format.Root).Select(t => new ReferenceSchemaTypeJson(
                t.Type, [.. t.Fields.Select(f => new ReferenceSchemaFieldJson(f.Name, f.Type, f.Default))]))];

    private sealed record Field(string Name, string Type, string Default);

    private sealed record Block(string Type, IReadOnlyList<Field> Fields);

    /// <summary>
    /// Every object type reachable from the root, breadth first, root first — which is the order a
    /// reader needs them in, since a nested type is only interesting once the field naming it has
    /// been read. Enums come last, all together, because they are referenced from everywhere.
    /// </summary>
    private static IReadOnlyList<Block> Walk(Type root)
    {
        var asm     = root.Assembly;
        var objects = new List<Block>();
        var enums   = new List<Block>();
        var seen    = new HashSet<Type> { root };
        var queue   = new Queue<Type>([root]);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            var rows = new List<Field>();

            // A default-constructed instance is where every default comes from. A type with no
            // parameterless constructor simply has no defaults to report — never a guessed one.
            object? blank = null;
            try { blank = Activator.CreateInstance(type); } catch { /* no default to report */ }

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;

                string name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;
                rows.Add(new Field(name, Spell(p.PropertyType), DefaultOf(p, blank)));

                foreach (var t in Referenced(p.PropertyType))
                {
                    if (t.IsEnum) { if (seen.Add(t)) enums.Add(EnumBlock(t)); continue; }
                    if (t.Assembly != asm || !seen.Add(t)) continue;
                    queue.Enqueue(t);
                }
            }

            // A polymorphic base (a .clay's shapes) is written as one of its DERIVED types, picked by
            // a discriminator — and the base alone names neither the discriminator nor any derived
            // type's fields, which are most of what a shape is. Both are read off the same
            // attributes System.Text.Json reads, so the page states what the reader accepts.
            var derived = type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).ToList();
            if (derived.Count > 0)
            {
                string key = type.GetCustomAttribute<JsonPolymorphicAttribute>()?.TypeDiscriminatorPropertyName
                             ?? "$type";
                rows.Insert(0, new Field(key, "string (which kind)",
                    string.Join(" | ", derived.Select(d => d.TypeDiscriminator))));

                foreach (var d in derived)
                    if (seen.Add(d.DerivedType)) queue.Enqueue(d.DerivedType);
            }

            string title = type.Name;
            if (type.BaseType?.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
                    .FirstOrDefault(d => d.DerivedType == type) is { } self)
                title += $"   ({BaseKey(type.BaseType)}: \"{self.TypeDiscriminator}\")";

            objects.Add(new Block(title, rows));
        }

        return [.. objects, .. enums.OrderBy(e => e.Type, StringComparer.Ordinal)];
    }

    private static string BaseKey(Type polymorphicBase)
        => polymorphicBase.GetCustomAttribute<JsonPolymorphicAttribute>()?.TypeDiscriminatorPropertyName ?? "$type";

    private static Block EnumBlock(Type t) => new(
        t.Name + "   (one of)",
        [.. Enum.GetNames(t).Select(n => new Field(n, "", ""))]);

    /// <summary>The types a property could pull in: itself, and whatever a list or dictionary holds.
    /// Nullable&lt;T&gt; is unwrapped, so a <c>double?</c> reaches the same place a <c>double</c>
    /// does.</summary>
    private static IEnumerable<Type> Referenced(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsArray)
        {
            foreach (var inner in Referenced(t.GetElementType()!)) yield return inner;
            yield break;
        }
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments())
                foreach (var inner in Referenced(arg)) yield return inner;
            yield break;
        }
        if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal)) yield break;
        yield return t;
    }

    /// <summary>How a type is written on the page: the JSON shape, not the CLR name.</summary>
    private static string Spell(Type t)
    {
        var nullable = Nullable.GetUnderlyingType(t);
        if (nullable is not null) return Spell(nullable) + "?";

        if (t == typeof(string))                          return "string";
        if (t == typeof(bool))                            return "bool";
        if (t == typeof(int) || t == typeof(long))        return "integer";
        if (t == typeof(byte))                            return "integer (0-255)";
        if (t == typeof(double) || t == typeof(float))    return "number";
        if (t == typeof(uint))                            return "integer (ARGB)";

        if (t.IsArray) return "[" + Spell(t.GetElementType()!) + "]";

        if (t.IsGenericType)
        {
            var args = t.GetGenericArguments();
            if (args.Length == 1) return "[" + Spell(args[0]) + "]";
            if (args.Length == 2) return "{" + Spell(args[0]) + ": " + Spell(args[1]) + "}";
        }
        return t.Name;
    }

    /// <summary>
    /// The default this field carries when a file omits it — read off the constructed instance,
    /// never written down. An empty answer means the property could not be read at all, which is
    /// the honest report and not a zero.
    /// </summary>
    private static string DefaultOf(PropertyInfo p, object? blank)
    {
        if (blank is null) return "";
        object? v;
        try { v = p.GetValue(blank); } catch { return ""; }

        return v switch
        {
            null            => "null",
            string s        => s.Length == 0 ? "\"\"" : "\"" + s + "\"",
            bool b          => b ? "true" : "false",
            Enum e          => e.ToString(),
            // A collection's default is its emptiness — printing its element count would say the
            // same thing less clearly, and a non-empty default collection does not occur here.
            IDictionary d   => d.Count == 0 ? "{}" : "{…}",
            IEnumerable c   => c.Cast<object?>().Any() ? "[…]" : "[]",
            // A nested object default-constructed by its owner is not "absent": omitting the field
            // gets you one of these, which is a different statement from null.
            not null when v.GetType().IsClass && v is not IFormattable => "(a " + v.GetType().Name + ")",
            IFormattable f  => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _               => v.ToString() ?? "",
        };
    }
}
