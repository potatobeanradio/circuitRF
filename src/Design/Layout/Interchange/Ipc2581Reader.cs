// Reads an IPC-2581 file into a PcbBoard (docs/briefs/brief-ipx-2-reader-artwork.md). Like PcbReader this
// touches nothing but the stream: no cell folders, no technology, no messages outside the board.
//
// Two rules decide the shape of this file. A single file can be a quarter of a gigabyte, so it walks an
// XmlReader and builds only one Set, Layer or dictionary entry at a time as a small tree, never the document.
// And the revision predicts little about what a file contains, so everything is decided by the elements
// present and the revision is only reported: containers may be wrapped or not, functions and sides have
// several spellings, elements may carry any namespace or none.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One step of a file, as a chooser or a listing shows it.</summary>
/// <param name="Name">The step's own name.</param>
/// <param name="LayerCount">The layers the step draws on.</param>
/// <param name="NetCount">The distinct nets it names.</param>
/// <param name="Places">The steps it places by step and repeat.</param>
/// <param name="IsPanel">All it does is place other steps, so it holds no artwork of its own.</param>
public sealed record Ipc2581Step(string Name, int LayerCount, int NetCount, IReadOnlyList<string> Places, bool IsPanel);

public sealed partial class Ipc2581Reader
{
    /// <summary>The root element's name, which is what makes a file this format whatever its extension.</summary>
    private const string RootElement = "IPC-2581";

    /// <summary>The outline layer the step's profile lands on when the file declares none of its own.</summary>
    internal const string SyntheticOutlineLayer = "BOARD_OUTLINE";

    private sealed class Refusal(string message) : Exception(message);

    /// <summary>Reads one step of <paramref name="stream"/> at a destination resolution of
    /// <paramref name="dbuPerMicron"/>.</summary>
    /// <param name="stepName">The step to read. Null reads the file's one step, and a file holding several is a
    /// refusal listing them.</param>
    /// <param name="entityCeiling">Refuse a board that comes to more than this many shapes, and stop reading at twice
    /// this many features.</param>
    /// <param name="control">Progress in megabytes of the stream, and cancellation, which throws
    /// <see cref="OperationCanceledException"/>. Nothing has been written at that point, because a read writes nothing.</param>
    public static PcbReader.ReadResult Read(
        Stream stream, int dbuPerMicron, string? stepName = null, long entityCeiling = PcbReader.EntityHardCeiling,
        RunControl? control = null)
    {
        try
        {
            using var xml = XmlReader.Create(stream, Settings());
            var reader = new Ipc2581Reader(dbuPerMicron, stepName, entityCeiling) { _control = control, _stream = stream };
            control?.BeginStage("reading the file", MegabytesOf(stream), "MB");
            return new PcbReader.ReadResult(reader.Run(xml), null);
        }
        catch (Refusal refusal)
        {
            return new PcbReader.ReadResult(null, refusal.Message);
        }
        catch (XmlException e)
        {
            return new PcbReader.ReadResult(null, NotXml(e));
        }
    }

    private static XmlReaderSettings Settings() => new()
    {
        // As ComponentLibraryXmlReader: a declared DTD is skipped unexpanded and never fetched.
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
    };

    /// <summary>Whether <paramref name="path"/>'s root element makes it this format, whatever it is called. Reads only
    /// as far as the root element.</summary>
    public static bool LooksLikeIpc2581(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var xml = XmlReader.Create(stream, Settings());
            return xml.MoveToContent() == XmlNodeType.Element && xml.LocalName == RootElement;
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException or ArgumentException
                                      or System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The steps a file holds, or the reason it cannot say.</summary>
    public sealed record StepListing(IReadOnlyList<Ipc2581Step>? Steps, string? Refusal);

    /// <summary>Walks the whole file for its steps, building nothing, so a chooser costs a small part of a read.</summary>
    public static StepListing ListSteps(Stream stream, RunControl? control = null)
    {
        try
        {
            using var xml = XmlReader.Create(stream, Settings());
            xml.MoveToContent();
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != RootElement)
                return new StepListing(null, NotThisFormat(xml.LocalName));

            control?.BeginStage("listing the file's steps", MegabytesOf(stream), "MB");
            long reported = 0, elements = 0;
            var steps = new List<Ipc2581Step>();
            string? step = null;
            int stepDepth = 0;
            bool ownContent = false;
            var layers = new HashSet<string>(StringComparer.Ordinal);
            var nets = new HashSet<string>(StringComparer.Ordinal);
            var places = new List<string>();

            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && step is not null && xml.Depth == stepDepth)
                {
                    steps.Add(new Ipc2581Step(step, layers.Count, nets.Count, [.. places.Distinct()], places.Count > 0 && !ownContent));
                    step = null;
                    continue;
                }
                if (xml.NodeType != XmlNodeType.Element) continue;
                if ((++elements & 0xFFF) == 0) reported = Report(control, stream, reported);

                if (step is null)
                {
                    if (xml.LocalName != "Step") continue;
                    step = xml.GetAttribute("name") ?? "";
                    stepDepth = xml.Depth;
                    ownContent = false;
                    layers.Clear();
                    nets.Clear();
                    places.Clear();
                    if (xml.IsEmptyElement)
                    {
                        steps.Add(new Ipc2581Step(step, 0, 0, [], false));
                        step = null;
                    }
                    continue;
                }

                switch (xml.LocalName)
                {
                    case "StepRepeat":
                        if (xml.GetAttribute("stepRef") is { Length: > 0 } placed) places.Add(placed);
                        break;
                    case "LayerFeature":
                        ownContent = true;
                        if (xml.GetAttribute("layerRef") is { Length: > 0 } layer) layers.Add(layer);
                        break;
                    case "PadStack" or "Set":
                        if (xml.LocalName == "PadStack") ownContent = true;
                        if (NetName(xml.GetAttribute("net"), step) is { } stated) nets.Add(stated);
                        break;
                    case "LogicalNet" or "PhyNet":
                        if (NetName(xml.GetAttribute("name"), step) is { } named) nets.Add(named);
                        break;
                }
            }
            return new StepListing(steps, null);
        }
        catch (XmlException e)
        {
            return new StepListing(null, NotXml(e));
        }
    }

    private static long Report(RunControl? control, Stream stream, long reported)
    {
        if (control is null) return reported;
        control.Token.ThrowIfCancellationRequested();
        if (!stream.CanSeek) return reported;
        long now = stream.Position >> 20;
        if (now > reported) control.TickStage(now - reported);
        return Math.Max(now, reported);
    }

    private static long MegabytesOf(Stream stream) => stream.CanSeek ? Math.Max(1, stream.Length >> 20) : 0;

    private static string NotThisFormat(string root) =>
        $"This file's root element is <{root}>, not <{RootElement}>, so it is not an IPC-2581 file. Nothing was imported.";

    private static string NotXml(XmlException e) =>
        $"This file could not be read as XML (line {e.LineNumber}: {e.Message}). It is not an IPC-2581 file, " +
        "or it is cut short. Nothing was imported.";

    private RunControl? _control;
    private Stream? _stream;
    private long _reportedMegabytes;
    private long _ticks;

    /// <summary>Between children of the walk, which is the finest grain the walk has.</summary>
    private void Tick()
    {
        if (_control is null || (++_ticks & 0xFF) != 0) return;
        _reportedMegabytes = Report(_control, _stream!, _reportedMegabytes);
    }

    private readonly int _dbuPerMicron;
    private readonly long _flattenTolDbu;
    private readonly string? _requestedStep;
    private readonly long _ceiling;
    private readonly PcbBoard _board = new();

    private string? _functionMode;
    private readonly List<string> _sections = [];
    private bool _sawEcad;

    /// <summary>DBU per CadHeader unit, null until a CadHeader states one.</summary>
    private double? _ecadScale;
    private string? _ecadUnits;

    private readonly List<LayerDeclaration> _layers = [];
    private readonly List<XElement> _stackups = [];
    private readonly List<StepInfo> _steps = [];
    private StepInfo? _chosen;
    private XElement? _stepProfile;
    private readonly Dictionary<string, LayerOps> _ops = new(StringComparer.Ordinal);
    private long _entities;

    private Ipc2581Reader(int dbuPerMicron, string? stepName, long ceiling)
    {
        _dbuPerMicron = dbuPerMicron;
        _flattenTolDbu = Math.Max(1, dbuPerMicron);
        _requestedStep = stepName;
        _ceiling = ceiling;
    }

    private sealed record LayerDeclaration(string Name, string Function, string Side, bool Negative, XElement Element);

    private sealed class StepInfo(string name)
    {
        public string Name { get; } = name;
        public bool HasOwnContent { get; set; }
        public List<string> Places { get; } = [];
    }

    // ── The walk ───────────────────────────────────────────────────────────────────────────────

    private PcbBoard Run(XmlReader xml)
    {
        xml.MoveToContent();
        if (xml.NodeType != XmlNodeType.Element || xml.LocalName != RootElement)
            throw new Refusal(NotThisFormat(xml.LocalName));

        string? revision = xml.GetAttribute("revision");
        _board.Version = revision;
        _board.Diagnostics.Add(revision is { Length: > 0 }
            ? $"IPC-2581 revision {revision}, read by the elements present and not by the revision."
            : "This IPC-2581 file states no revision. It was read by the elements present.");

        Children(xml, name =>
        {
            switch (name)
            {
                case "Content": ReadContent(xml); break;
                case "Ecad": _sawEcad = true; _sections.Add(name); ReadEcad(xml); break;
                case "LogisticHeader" or "HistoryRecord" or "Avl": xml.Skip(); break;
                case "Bom": _sections.Add(name); xml.Skip(); break;
                default: Unknown(name); xml.Skip(); break;
            }
        });

        Finish();
        return _board;
    }

    /// <summary>Calls <paramref name="onElement"/> for each child element of the element the reader is on. The
    /// callback consumes the whole child, so a tree it builds is released before the next one starts.</summary>
    private void Children(XmlReader xml, Action<string> onElement)
    {
        if (xml.IsEmptyElement) { xml.Read(); return; }
        int depth = xml.Depth;
        xml.Read();
        while (!(xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth) && !xml.EOF)
        {
            Tick();
            if (xml.NodeType == XmlNodeType.Element) onElement(xml.LocalName);
            else xml.Read();
        }
        xml.Read();
    }

    private static XElement Tree(XmlReader xml) => (XElement)XNode.ReadFrom(xml);

    private void ReadContent(XmlReader xml)
    {
        Children(xml, name =>
        {
            switch (name)
            {
                case "FunctionMode": _functionMode = xml.GetAttribute("mode"); xml.Skip(); break;
                case "DictionaryStandard": ReadDictionary(xml, name, _standard); break;
                case "DictionaryUser": ReadDictionary(xml, name, _user); break;
                case "DictionaryLineDesc": ReadDictionary(xml, name, _lineDescs); break;
                case "DictionaryFillDesc": ReadDictionary(xml, name, _fillDescs); break;
                case "StepRef" or "LayerRef" or "BomRef" or "AvlRef" or "StepListRef" or "LayerDescRef" or "DictionaryColor"
                    or "DictionaryFont" or "DictionaryFirmware":
                    xml.Skip();
                    break;
                default: Unknown(name); xml.Skip(); break;
            }
        });
    }

    private void ReadEcad(XmlReader xml)
    {
        Children(xml, name =>
        {
            switch (name)
            {
                case "CadHeader":
                    _ecadUnits = xml.GetAttribute("units");
                    _ecadScale = ScaleOf(_ecadUnits);
                    ReadCadHeader(xml);
                    break;
                case "CadData": ReadCadData(xml); break;
                default: Unknown(name); xml.Skip(); break;
            }
        });
    }

    /// <summary>A CadHeader holds the file's specifications, which a stackup entry refers to by name.</summary>
    private void ReadCadHeader(XmlReader xml)
    {
        Children(xml, name =>
        {
            switch (name)
            {
                case "Spec":
                {
                    var spec = Tree(xml);
                    if (Attr(spec, "name") is { Length: > 0 } specName) _specs.TryAdd(specName, spec);
                    break;
                }
                case "ChangeRec": xml.Skip(); break;
                default: Unknown(name); xml.Skip(); break;
            }
        });
    }

    /// <summary>The layers, stackup and steps, directly under CadData or inside wrappers. A wrapper is walked as if it
    /// were not there.</summary>
    private void ReadCadData(XmlReader xml)
    {
        Children(xml, name =>
        {
            switch (name)
            {
                case "Layer": ReadLayer(Tree(xml)); break;
                case "Stackup": _stackups.Add(Tree(xml)); break;
                case "Step": ReadStep(xml); break;
                case "LayerDesc" or "StepList": ReadCadData(xml); break;
                default: Unknown(name); xml.Skip(); break;
            }
        });
    }

    private void ReadLayer(XElement layer)
    {
        string name = Attr(layer, "name") ?? "";
        if (name.Length == 0) { Skipped("layer with no name"); return; }
        _layers.Add(new LayerDeclaration(
            name,
            (Attr(layer, "layerFunction") ?? "").ToUpperInvariant(),
            NormalizedSide(Attr(layer, "side")),
            string.Equals(Attr(layer, "polarity"), "NEGATIVE", StringComparison.OrdinalIgnoreCase),
            layer));
    }

    /// <summary>Files write an inner layer's side two ways.</summary>
    private static string NormalizedSide(string? side)
    {
        string upper = (side ?? "").ToUpperInvariant();
        return upper == "INNER" ? "INTERNAL" : upper;
    }

    // ── Steps ──────────────────────────────────────────────────────────────────────────────────

    private void ReadStep(XmlReader xml)
    {
        var step = new StepInfo(xml.GetAttribute("name") ?? "");
        _steps.Add(step);

        bool read = _requestedStep is not null ? step.Name == _requestedStep : _steps.Count == 1;
        if (_requestedStep is null && _steps.Count == 2)
        {
            // More than one step and nothing says which. What was read is released, and the rest of the file is
            // only walked for the names the refusal lists.
            _ops.Clear();
            _stepProfile = null;
            _chosen = null;
            ReleaseNetsAndHoles();
        }

        if (read) _chosen = step;

        Children(xml, name =>
        {
            switch (name)
            {
                case "StepRepeat":
                    if (xml.GetAttribute("stepRef") is { Length: > 0 } placed) step.Places.Add(placed);
                    xml.Skip();
                    break;
                case "LayerFeature" or "PadStack" when !read:
                    step.HasOwnContent = true;
                    xml.Skip();
                    break;
                case "LayerFeature":
                    step.HasOwnContent = true;
                    ReadLayerFeature(xml, step);
                    break;
                case "PadStack":
                    step.HasOwnContent = true;
                    ReadPadStack(Tree(xml), step);
                    break;
                case "Profile" when read:
                    _stepProfile = Tree(xml);
                    break;

                // Facts that attach to the copper. S2 of the series: none of these creates a shape.
                case "Component" when read: ReadComponent(Tree(xml), step); break;
                case "LogicalNet" when read: ReadLogicalNet(Tree(xml), step); break;
                case "PhyNetGroup" when read: ReadPhyNetGroup(xml, step); break;
                case "StackupZone" when read: _zones.Add(Tree(xml)); break;

                case "Port" or "Model" or "BendArea" or "DfxMeasurementList" when read:
                    Skipped($"{name} (not modelled)");
                    xml.Skip();
                    break;

                case "Profile" or "Datum" or "Package" or "Component" or "LogicalNet" or "PhyNetGroup" or "PadStackDef"
                    or "NonstandardAttribute" or "BendArea" or "StackupZone" or "Port" or "Model" or "DfxMeasurementList":
                    xml.Skip();
                    break;

                default:
                    if (read) Unknown(name);
                    xml.Skip();
                    break;
            }
        });

        if (read && step.Places.Count > 0)
            Bump(_board.SkippedCounts, "step-and-repeat placement (StepRepeat is not expanded)", step.Places.Count);
    }

    private void ReadLayerFeature(XmlReader xml, StepInfo step)
    {
        string layer = xml.GetAttribute("layerRef") ?? "";
        Children(xml, name =>
        {
            if (name == "Set" && layer.Length > 0) ReadSet(Tree(xml), layer, step);
            else
            {
                if (name != "Set") Unknown(name);
                else Skipped("feature set on a layer feature that names no layer");
                xml.Skip();
            }
        });
    }

    /// <summary>A name unique within one step may be written with that step's name in front. Only that exact
    /// prefix goes: a colon is otherwise an ordinary character in a net name.</summary>
    private static string? StripStepPrefix(string? name, string step)
    {
        if (name is not { Length: > 0 }) return null;
        return step.Length > 0 && name.Length > step.Length + 1
            && name.StartsWith(step, StringComparison.Ordinal) && name[step.Length] == ':'
            ? name[(step.Length + 1)..]
            : name;
    }

    // ── Finishing ──────────────────────────────────────────────────────────────────────────────

    private void Finish()
    {
        if (!_sawEcad)
            throw new Refusal(
                $"This IPC-2581 file holds no board artwork: it has no Ecad section ({HoldsSentence()}). Nothing was imported.");

        ChooseStep();

        if (!_layers.Exists(l => IsCopperFunction(l.Function)))
        {
            var functions = _layers.Select(l => l.Function.Length > 0 ? l.Function : "unstated")
                .Distinct(StringComparer.Ordinal).ToList();
            throw new Refusal(
                "This IPC-2581 file has no conductor layer, so it holds no copper to import " +
                $"({HoldsSentence()}; layer functions: {(functions.Count > 0 ? string.Join(", ", functions) : "none")}). " +
                "Nothing was imported.");
        }

        _control?.Token.ThrowIfCancellationRequested();
        _control?.BeginStage("working out each layer's copper");
        ApplyPadParts();
        ReadStackup();
        ResolveLayers();
        ResolveHoles();
        ReportNetsAndComponents();
        BuildLayerTable();
        if (_board.Shapes.Count > _ceiling) throw OverCeiling();
        _board.ShapesProduced = _board.Shapes.Count;
        _board.EntitiesRead = (int)Math.Min(_entities, int.MaxValue);
    }

    private string HoldsSentence()
    {
        string mode = _functionMode is { Length: > 0 } m ? $"FunctionMode \"{m}\"" : "no FunctionMode stated";
        return _sections.Count > 0 ? $"{mode}; it holds {string.Join(" and ", _sections)}" : mode;
    }

    private void ChooseStep()
    {
        string Listing() => string.Join(", ", _steps.Select(s =>
            IsPanel(s) ? $"'{s.Name}' (a panel placing {string.Join(", ", s.Places.Distinct().Select(p => $"'{p}'"))})" : $"'{s.Name}'"));

        if (_steps.Count == 0)
            throw new Refusal("This IPC-2581 file declares no Step, so it holds no artwork to read. Nothing was imported.");

        if (_requestedStep is not null && _chosen is null)
            throw new Refusal($"This file has no step named '{_requestedStep}'. It holds {Listing()}. Nothing was imported.");

        if (_requestedStep is null && _steps.Count > 1)
            throw new Refusal(
                $"This file holds {_steps.Count} steps: {Listing()}. Name the one to read. Nothing was imported.");

        var chosen = _chosen!;
        if (IsPanel(chosen))
        {
            var boards = chosen.Places.Distinct().ToList();
            var present = boards.Where(b => _steps.Exists(s => s.Name == b)).ToList();
            string names = string.Join(", ", boards.Select(b => $"'{b}'"));
            throw new Refusal(
                $"Step '{chosen.Name}' is a panel: all it does is place {names} by step and repeat, which is not expanded. " +
                (present.Count > 0
                    ? $"Read {string.Join(", ", present.Select(b => $"'{b}'"))} instead."
                    : "This file does not contain that step.") +
                " Nothing was imported.");
        }
    }

    private static bool IsPanel(StepInfo step) => step.Places.Count > 0 && !step.HasOwnContent;

    // ── The layer table ────────────────────────────────────────────────────────────────────────

    private static bool IsCopperFunction(string function) =>
        function is "CONDUCTOR" or "SIGNAL" or "CONDFILM" or "CONDFOIL" or "PLANE" or "MIXED";

    private static bool IsOutlineFunction(string function) => function is "BOARD_OUTLINE" or "BOARDOUTLINE";

    /// <summary>Copper is decided by the function and never by the layer's name.</summary>
    private static string TableType(string function) => function switch
    {
        "CONDUCTOR" or "SIGNAL" or "CONDFILM" or "CONDFOIL" => "signal",
        "PLANE" => "power",
        "MIXED" => "mixed",
        _ => "user",
    };

    /// <summary>What a non-copper layer's function and side make it, in the names the rest of the application
    /// recognises a board's outline and surfaces by. Null when neither says.</summary>
    private static string? InterchangeNameOf(string function, string side)
    {
        if (IsOutlineFunction(function)) return "Edge.Cuts";
        string? prefix = side switch { "TOP" => "F", "BOTTOM" => "B", _ => null };
        if (prefix is null) return null;
        return function switch
        {
            "SILKSCREEN" or "LEGEND" => $"{prefix}.SilkS",
            "SOLDERMASK" => $"{prefix}.Mask",
            "SOLDERPASTE" or "PASTEMASK" => $"{prefix}.Paste",
            _ => null,
        };
    }

    /// <summary>Ordinal is position top to bottom for the layers the stackup names, then document order for the
    /// rest. Document order alone is not physical order.</summary>
    private void BuildLayerTable()
    {
        var order = new List<string>();
        var declared = new HashSet<string>(_layers.Select(l => l.Name), StringComparer.Ordinal);
        foreach (var name in _stackupOrder)
            if (declared.Contains(name) && !order.Contains(name)) order.Add(name);

        var byName = _layers.GroupBy(l => l.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var layer in _layers)
            if (!order.Contains(layer.Name)) order.Add(layer.Name);

        for (int i = 0; i < order.Count; i++)
        {
            var layer = byName[order[i]];
            _board.LayerTable.Add(new PcbLayerTableEntry(i, layer.Name, TableType(layer.Function), null)
            {
                Side = layer.Side.Length > 0 ? layer.Side : null,
                InterchangeName = InterchangeNameOf(layer.Function, layer.Side),
                IsDrill = layer.Function == DrillFunction,
            });
        }
    }

    // ── Small readers ──────────────────────────────────────────────────────────────────────────

    private static string? Attr(XElement element, string name) => element.Attribute(name)?.Value;

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value : null;

    /// <summary>Files write this word in capitals and in lower case, and the format's own text uses the word MIRROR
    /// for its true value in one place.</summary>
    private static bool Bool(string? text) =>
        text is not null && (text.Equals("true", StringComparison.OrdinalIgnoreCase)
                             || text.Equals("mirror", StringComparison.OrdinalIgnoreCase));

    /// <summary>DBU per one of a section's units, or null for a spelling that is not one of the three. A guess would
    /// differ by a factor of 25.4 or 1,000, so nothing here defaults.</summary>
    private double? ScaleOf(string? units) => (units ?? "").ToUpperInvariant() switch
    {
        "MILLIMETER" => _dbuPerMicron * 1000.0,
        "MICRON" => _dbuPerMicron,
        "INCH" => _dbuPerMicron * 25400.0,
        _ => null,
    };

    private double EcadScale()
    {
        if (_ecadScale is { } scale) return scale;
        throw new Refusal(_ecadUnits is { Length: > 0 }
            ? $"CadHeader states the units \"{_ecadUnits}\", which is not MILLIMETER, MICRON or INCH. Guessing would differ by a factor of 25.4 or 1,000. Nothing was imported."
            : "CadHeader states no units and this file has geometry in that section. Guessing would differ by a factor of 25.4 or 1,000. Nothing was imported.");
    }

    private void Unknown(string name) => Bump(_board.UnknownTokenCounts, name, 1);

    private void Skipped(string what) => Bump(_board.SkippedCounts, what, 1);

    private void Degraded(string what) => Bump(_board.DegradedCounts, what, 1);

    private static void Bump(Dictionary<string, int> counts, string key, int by)
    {
        counts.TryGetValue(key, out int count);
        counts[key] = count + by;
    }

    private void CountEntity()
    {
        // A feature read is often not a shape kept: a via's pads collapse into it. So the walk only guards the read's own
        // memory, at twice the ceiling, and the ceiling itself is held on what the board comes to (Finish).
        if (++_entities > _ceiling * 2) throw OverCeiling();
    }

    private Refusal OverCeiling() => new(
        $"This board carries more than {_ceiling:N0} entities, above the import ceiling. Nothing was imported. " +
        "Crop the board in the originating tool and export the region you intend to simulate.");
}
