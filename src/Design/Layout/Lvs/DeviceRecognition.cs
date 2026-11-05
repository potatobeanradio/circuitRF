// GEOMETRIC DEVICE RECOGNITION — brief-lvs-14-recognition.md, docs/design/lvs.md §4.1 tier 3.
//
//   the root's own copper ──► DeviceCandidates.Find(Body, Terminals) ──► candidates
//                                                                            │
//                        the one expression engine ◄── Parameters ───────────┤
//                                                                            ▼
//                                                                      LvsDevice[]
//
// ── IT IS A FALLBACK, AND EVERY LINE OF IT IS SUBORDINATE TO THE INSTANCE READING ─────────────
//
// R-lvs14-1a: circuitRF's layout is INSTANCE-BEARING. A device is already a first-class object in
// the file, so the primary reading is correspondence and not recognition — which is what makes the
// whole LVS series small. Recognition exists for artwork that carries no instances at all: a
// hand-drawn MMIC device, a GDSII import whose hierarchy was flattened away, a Gerber board read
// back as polygons.
//
// It SHIPS OFF (R-lvs14-1d), per run and per technology. Turning it on for a design circuitRF
// authored would re-recognise devices it already knows, from geometry, less reliably than reading
// the instance that is right there.
//
// ── WHAT THIS FILE OWNS, AND WHAT IT BORROWS ─────────────────────────────────────────────────
//
// It owns the RULES: which candidate becomes a device, what its terminals are called, what it
// claims and what it refuses to claim. It owns no geometry — `DeviceCandidates` in Extraction does
// that, for `SubCellContact`'s reason and under the same source scan — and no expression engine:
// the parameter formulas go through `Evaluator`, which is the one that resolves a global, a cell
// parameter, an SDD equation and a measurement (R-lvs14-2b). A second of either would be a second
// answer to a question this repository already answers once.
//
// ── AND FOUR THINGS IT MAY NEVER DO (R-lvs14-4) ──────────────────────────────────────────────
//
//   Never override an instance.   Where copper belongs to a placed device the instance wins, so
//                                 the caller hands in the root's own shapes and nothing else.
//   Never invent a parameter.     A rule that states no formula claims nothing; a formula that
//                                 will not evaluate claims nothing and says so.
//   Never silently skip.          Every candidate the deck produced and this rejected is reported
//                                 with its reason.
//   Never claim more than it is.  `lvs.recognize.in-use` is unconditional wherever recognition
//                                 contributed, so a clean report is not mistaken for a stronger
//                                 claim than it is.

using System.Globalization;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>Tier 3: devices read out of copper, for artwork that carries no instances.</summary>
internal static class DeviceRecognition
{
    /// <summary>
    /// How many terminals a rule of this deck describes.
    /// </summary>
    /// <remarks>
    /// <b>Two, and the brief's scope says why</b> (§7): two-terminal passives are what the shipped
    /// technologies describe, and a three-terminal rule is additive to the same deck when a real
    /// design needs one. It is a named constant rather than a literal so that the finding, the
    /// check and the rule all read the same number.
    /// </remarks>
    public const int TerminalsPerDevice = 2;

    /// <summary>
    /// The measured quantities a parameter formula may name, <b>all in SI</b> — metres, square
    /// metres (R-lvs14-2b).
    /// </summary>
    /// <remarks>
    /// <b>SI and not DBU, because the formulas are the process's and processes are stated in SI.</b>
    /// A sheet resistance is ohms per square and a capacitance density is farads per square metre;
    /// handing those a length in database units would be wrong by the resolution — a thousand at the
    /// default — and the answer would look entirely plausible.
    /// </remarks>
    public static readonly IReadOnlySet<string> Measured =
        new HashSet<string>(StringComparer.Ordinal) { "Length", "Width", "Area", "Perimeter" };

    /// <summary>The same names, for a sentence.</summary>
    public static string MeasuredNames => string.Join(", ", Measured.Order(StringComparer.Ordinal));

    /// <summary>Every <see cref="DeviceKind"/> a rule may declare, for the refusal that lists
    /// them.</summary>
    public static string KindNames => string.Join(
        ", ", Enum.GetNames<DeviceKind>().Order(StringComparer.Ordinal));

    /// <summary>
    /// The <see cref="DeviceKind"/> a rule's <c>Kind</c> names — R-lvs14-2d.
    /// </summary>
    /// <remarks>
    /// <b>False rather than a fallback.</b> An unrecognised kind must be a refusal listing the real
    /// ones, which is <c>--tech</c>'s own rule: falling back to <see cref="DeviceKind.Unknown"/>
    /// would make every mistyped rule produce devices that match anything, which is worse than
    /// producing none.
    /// </remarks>
    public static bool TryParseKind(string? text, out DeviceKind kind)
    {
        kind = DeviceKind.Unknown;
        return text is { Length: > 0 } && Enum.TryParse(text, ignoreCase: true, out kind)
            && Enum.IsDefined(kind);
    }

    /// <summary>
    /// The copper every <see cref="DeviceRule.CopperBody"/> rule's bodies occupy, which leaves the
    /// partition BEFORE it is built (AA-2) — or null where the deck has no such rule or the artwork
    /// has no such body, which leaves the partition exactly as it was.
    /// </summary>
    /// <remarks>
    /// <b>Every body, accepted or not.</b> Which candidates become devices depends on their
    /// terminals, and their terminals are read on the cut partition — so the cut cannot wait for
    /// the verdict. A body the run then rejects is still reported by <see cref="Emit"/>, with its
    /// reason, which is where a reader looking at a split net finds out why.
    /// </remarks>
    public static CopperCut? Cut(Technology? tech, IReadOnlyList<LayoutShape> copper)
    {
        if (tech is not { DeviceRules.Count: > 0 } || copper.Count == 0) return null;

        var bodies = new List<DrcLayerExpr>();
        foreach (var rule in tech.DeviceRules)
            if (rule.CopperBody && TryCompile(rule, out _, out var body, out _, out _))
                bodies.Add(body!);

        return bodies.Count == 0 ? null : DeviceCandidates.CutOf(copper, tech, bodies);
    }

    /// <summary>
    /// Runs the technology's deck over <paramref name="copper"/> and appends what it recognised to
    /// <paramref name="devices"/>.
    /// </summary>
    /// <param name="tech">The resolved technology. Its <see cref="Technology.DeviceRules"/> ARE the
    /// deck, and its <see cref="Technology.Constants"/> are what a formula may multiply by.</param>
    /// <param name="copper">The shapes recognition may see — R-lvs14-3d's subset, chosen by the
    /// caller: the root's own, never an instance's.</param>
    /// <param name="pieces">The partition, so a terminal reaches the net its copper is already on.</param>
    /// <param name="nets">The one net table — a recognised terminal and a placed pin must arrive at
    /// the same numbering, or one design has two answers to "is this the input net".</param>
    /// <param name="devices">Appended to, in the deck's own rule order.</param>
    /// <param name="padGeometry">Appended to, so a recognised terminal carries a marker like any
    /// other.</param>
    /// <param name="notes">Appended to.</param>
    /// <param name="naming">How a coordinate and a layer are spelled.</param>
    /// <param name="document">The `.clay` this came from, for provenance.</param>
    public static void Emit(
        Technology? tech, IReadOnlyList<LayoutShape> copper,
        CopperPieces pieces, LayoutRead.NetTable nets,
        List<LvsDevice> devices, List<LvsPadGeometry> padGeometry,
        List<Diagnostic> notes, LvsGeometryNaming naming, string document, CopperCut? cut = null)
    {
        // R-lvs14-1d: a technology with no block cannot recognise anything, and SAYING SO IS NOT AN
        // ERROR. A caller that asked for recognition on a process that describes none gets the
        // ordinary instance reading, unchanged.
        if (tech is not { DeviceRules.Count: > 0 } || copper.Count == 0) return;

        int recognised = 0, rulesUsed = 0;

        foreach (var rule in tech.DeviceRules)
        {
            string ruleName = rule.Name is { Length: > 0 } n ? n : "(unnamed)";

            if (!TryCompile(rule, out var kind, out var body, out var terminals, out string? why))
            {
                notes.Add(LvsDiagnostics.RecognizeRuleInvalid(ruleName, why!));
                continue;
            }

            // The film this rule's body is drawn on decides its R_s and, on a level of its own, how it is contacted: through its
            // via, so a terminal naming metal would read a strap crossing the film as a contact, and a film with no via as a match.
            LayerKey? bodyFilm = body!.ReferencedLayers().Where(k => ResistiveFilms.IsFilm(tech, k)).Cast<LayerKey?>().FirstOrDefault();
            bool viaContacted = bodyFilm is { } filmKey && ResistiveFilms.ConductorOf(tech, filmKey) is { } filmLevel
                                && ResistiveFilms.IsFilmLevel(filmLevel);
            if (viaContacted && !terminals!.All(t => t.ReferencedLayers().All(k => ResistiveFilms.IsContactOf(tech, k, bodyFilm!.Value))))
            {
                notes.Add(LvsDiagnostics.RecognizeRuleInvalid(ruleName,
                    "its body is a film on a level of its own, which is contacted through a via, and its terminals name something else."));
                continue;
            }
            var constants = ConstantsOf(tech, bodyFilm);

            var candidates = DeviceCandidates.Find(copper, tech, body!, terminals!, out var missing, cut);

            // A layer the TECHNOLOGY does not define — not merely one this design has nothing on,
            // which is the ordinary state of most layers of any process. The rule cannot be applied
            // here at all, and saying so is the difference between that and "it matched nothing":
            // left unsaid, a rule whose terminal layer is misspelled produces a terminal-count
            // warning per body and never names the cause.
            if (missing.Count > 0)
            {
                notes.Add(LvsDiagnostics.RecognizeRuleInvalid(ruleName,
                    $"it names layer(s) {string.Join(", ", missing.Select(l => $"{l.Layer}/{l.Datatype}").Order(StringComparer.Ordinal))}, " +
                    $"which '{tech.Name}' does not define."));
                continue;
            }

            if (candidates.Count == 0) continue;

            rulesUsed++;

            foreach (var candidate in candidates)
            {
                string where = naming.Format.Point(candidate.X, candidate.Y);

                // R-lvs14-3b. A candidate whose terminal count is not the rule's is NOT a device —
                // and it is reported, because a recognition pass that quietly dropped half the
                // devices would make a design read as clean. A ground-terminal rule draws one end
                // fewer (AA-2): the plane is the other.
                int drawn = TerminalsPerDevice - (rule.GroundTerminal ? 1 : 0);

                // AA-2. A LINE with one end touching nothing is an open stub, and the schematic
                // draws exactly that: its far end on a net of its own. LayoutReadBodies reads a
                // placed line's open end the same way. Nothing else gets this — a resistor with
                // one pad is a drawing error and stays a rejection.
                bool openEnd = rule.CopperBody && kind == DeviceKind.TransmissionLine
                               && candidate.Terminals.Count == drawn - 1 && drawn > 1;

                if (candidate.Terminals.Count != drawn && !openEnd)
                {
                    notes.Add(viaContacted
                        ? LvsDiagnostics.RecognizeFilmOpen(ruleName, where, candidate.Terminals.Count, drawn)
                        : LvsDiagnostics.RecognizeTerminalCount(ruleName, where, candidate.Terminals.Count, drawn));
                    continue;
                }

                string path = PathOf(kind, candidate, naming);
                int deviceIndex = devices.Count;

                var measured = Measure(candidate, naming.Format.DbuPerMicron);

                // The vias set a via-contacted film's length, and the film runs on past them: between their inner edges.
                if (viaContacted && candidate.Terminals.Count == 2)
                    measured["Length"] = (candidate.Terminals[1].AlongMin - candidate.Terminals[0].AlongMax)
                                         / (1e6 * Math.Max(1, naming.Format.DbuPerMicron));
                var (values, withheld) = Evaluate(
                    rule, ruleName, path, constants, measured, candidate.AxisAmbiguous, notes);

                if (withheld is { Length: > 0 })
                    notes.Add(LvsDiagnostics.RecognizeAmbiguousAxis(ruleName, path, where, withheld));

                var wired = new List<LvsTerminal>(TerminalsPerDevice);
                for (int i = 0; i < candidate.Terminals.Count; i++)
                {
                    var terminal = candidate.Terminals[i];
                    int net = NetUnder(
                        terminal, pieces, nets, notes, naming, path, deviceIndex, i, padGeometry);

                    // 1-based, the port number both sides already use, and the name is the port —
                    // a recognised device has no terminal map and nothing named its pins.
                    wired.Add(new LvsTerminal(i + 1, (i + 1).ToString(CultureInfo.InvariantCulture), net));
                    nets.Attach(net, deviceIndex, i);
                }

                // AA-2: the ends the artwork does not draw — an open stub's far end, then the plane
                // under a via. Numbered on from the drawn ones, exactly as a placed VIAGND's plane is.
                if (openEnd) AddTerminal(wired, nets.Open(), nets, deviceIndex);
                if (rule.GroundTerminal) AddTerminal(wired, nets.Ground(), nets, deviceIndex);

                // R-lvs14-3e: NO DESIGNATOR where nothing named the device, so it can only ever be
                // matched STRUCTURALLY (tier 2) — honest, and exactly what recognition buys. AA-2:
                // where the artwork DID name it — a Component on its body's shapes, which is what a
                // Gerber %TO.C and an agent writing a .clay both state — that name is its designator,
                // and it anchors exactly as a placed part's reference designator does. Two names on
                // one body is no name at all, and is said.
                string designator = candidate.Components.Count == 1 ? candidate.Components[0] : "";
                if (candidate.Components.Count > 1)
                    notes.Add(LvsDiagnostics.RecognizeComponentAmbiguous(
                        ruleName, path, string.Join(", ", candidate.Components)));

                devices.Add(new LvsDevice(
                    path,
                    designator,
                    new DeviceType(kind, null, ruleName),
                    wired, values,
                    new LvsProvenance(document, path, candidate.X, candidate.Y))
                {
                    // R-lvs10-4a's own meaning, and it is literally true here: every value came out
                    // of the artwork's own geometry, so it cannot disagree with the artwork — only
                    // with what the schematic asked for, which is a different sentence pointing at
                    // a different file.
                    ParameterFacts = values.ToDictionary(
                        p => p.Key, p => new LvsParameterFact(DimensionOf(kind, p.Key), Computed: true),
                        StringComparer.Ordinal),
                    Recognized = true,
                });

                recognised++;
            }
        }

        // R-lvs14-5a, and R-lvs14-1d's other half: silence where nothing was recognised, because a
        // technology with no deck is the ordinary case and is not an error.
        if (recognised > 0)
            notes.Add(LvsDiagnostics.RecognizeInUse(
                tech.Name is { Length: > 0 } t ? t : "this technology", recognised, rulesUsed));
    }

    /// <summary>
    /// What a recognised value IS, so it is compared at that dimension's tolerance (AA-2).
    /// </summary>
    /// <remarks>
    /// <b>Only where the schematic does not already say.</b> The comparison takes the schematic
    /// parameter's own dimension first (<c>LvsProperties.Dimension</c>); this answers for a design
    /// whose parameters state none — a hand-written <c>.csch</c> — where an area measured off a
    /// polygon and the same capacitance typed as a number agree to fifteen digits, not to the last
    /// bit, and "exact" would report a correct capacitor as a mismatch. The names are the ones every
    /// built-in two-terminal passive and microstrip element already uses; anything else stays None.
    /// </remarks>
    private static UnitDimension DimensionOf(DeviceKind kind, string parameter) => (kind, parameter) switch
    {
        (DeviceKind.Resistor, "R")  => UnitDimension.Resistance,
        (DeviceKind.Capacitor, "C") => UnitDimension.Capacitance,
        (DeviceKind.Inductor, "L")  => UnitDimension.Inductance,
        (DeviceKind.TransmissionLine, "W" or "L") => UnitDimension.Length,
        _ => UnitDimension.None,
    };

    private static void AddTerminal(List<LvsTerminal> wired, int net, LayoutRead.NetTable nets, int deviceIndex)
    {
        int port = wired.Count + 1;
        wired.Add(new LvsTerminal(port, port.ToString(CultureInfo.InvariantCulture), net));
        nets.Attach(net, deviceIndex, wired.Count - 1);
    }

    // ── One rule, compiled (R-lvs14-2a, R-lvs14-2d) ──────────────────────────────────────────

    /// <summary>
    /// A rule's kind and its region expressions, or the sentence saying which part will not read.
    /// </summary>
    /// <remarks>
    /// <b>Every failure here should already have been a <c>check</c> error</b> — R-lvs14-2c puts
    /// the deck in <c>TechValidation</c>'s table for exactly this reason, so a run reaches this
    /// only when somebody skipped the check. It still refuses by NAME rather than throwing: one
    /// unusable rule must not lose the run's other findings.
    /// </remarks>
    private static bool TryCompile(
        DeviceRule rule, out DeviceKind kind,
        out DrcLayerExpr? body, out IReadOnlyList<DrcLayerExpr>? terminals, out string? why)
    {
        body = null;
        terminals = null;
        why = null;

        if (!TryParseKind(rule.Kind, out kind))
        {
            why = $"\"{rule.Kind}\" is not one of: {KindNames}.";
            return false;
        }

        if (!DrcLayerExprParser.TryParse(rule.Body, out body, out string? bodyError) || body is null)
        {
            why = $"its body region does not read: {bodyError}";
            return false;
        }

        if (rule.Terminals.Count == 0)
        {
            why = "it names no terminal layers.";
            return false;
        }

        var compiled = new List<DrcLayerExpr>(rule.Terminals.Count);
        foreach (string text in rule.Terminals)
        {
            if (!DrcLayerExprParser.TryParse(text, out var expr, out string? error) || expr is null)
            {
                why = $"terminal region \"{text}\" does not read: {error}";
                return false;
            }
            compiled.Add(expr);
        }

        terminals = compiled;
        return true;
    }

    // ── What the artwork measures (R-lvs14-3c) ───────────────────────────────────────────────

    /// <summary>
    /// The candidate's geometry in SI, by the names a formula uses.
    /// </summary>
    /// <remarks>
    /// One DBU is <c>1 / (1000 · DbuPerMicron)</c> of a millimetre, so a metre is
    /// <c>1e6 · DbuPerMicron</c> DBU. Doing the conversion once, here, is what keeps every formula
    /// in the deck written in the units a process datasheet states.
    /// </remarks>
    private static Dictionary<string, double> Measure(DeviceCandidate candidate, int dbuPerMicron)
    {
        double perMetre = 1e6 * Math.Max(1, dbuPerMicron);
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Length"]    = candidate.LengthDbu / perMetre,
            ["Width"]     = candidate.WidthDbu / perMetre,
            ["Area"]      = candidate.AreaDbu2 / (perMetre * perMetre),
            ["Perimeter"] = candidate.PerimeterDbu / perMetre,
        };
    }

    // ── What the rule claims (R-lvs14-2b, R-lvs14-4b) ────────────────────────────────────────

    /// <summary>
    /// Every parameter this rule states, evaluated on this candidate's own measurements — and the
    /// names that were WITHHELD because the body's axis is undecidable.
    /// </summary>
    /// <remarks>
    /// <b>A fresh <see cref="Evaluator"/> per candidate, and that is load-bearing.</b> An evaluator
    /// memoizes by <c>scope::name</c>, so one shared across candidates would answer the second
    /// body's <c>Length</c> with the first body's — every device after the first silently carrying
    /// the wrong geometry, with every value plausible.
    ///
    /// <para><b>The ambiguity is judged per FORMULA, not per candidate</b> (R-lvs14-3c). A square
    /// MIM capacitor is the ordinary case and its <c>C = CapDensity · Area</c> does not care which
    /// way is along; only a formula that actually reads <c>Length</c> or <c>Width</c> is affected,
    /// and withholding the others would report a fault where there is none.</para>
    /// </remarks>
    private static (IReadOnlyDictionary<string, object?> Values, string Withheld) Evaluate(
        DeviceRule rule, string ruleName, string path, Scope constants,
        Dictionary<string, double> measured, bool axisAmbiguous, List<Diagnostic> notes)
    {
        if (rule.Parameters.Count == 0) return (EmptyParameters, "");

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var withheld = new List<string>();

        foreach (var (name, formula) in rule.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(formula)) continue;

            bool readsAxis;
            try
            {
                readsAxis = AstWalker.CollectRefs(Parser.Parse(formula))
                                     .Any(r => r is "Length" or "Width");
            }
            catch (Exception ex)
            {
                notes.Add(LvsDiagnostics.RecognizeParameterFailed(path, ruleName, name, ex.Message));
                continue;
            }

            if (axisAmbiguous && readsAxis) { withheld.Add(name); continue; }

            // A scope per candidate AND a scope per parameter would be wasteful; a scope per
            // candidate is enough, because the measured names are literals and cannot cycle. What a
            // formula CAN cycle through is the constants, and that is the engine's own detection.
            var scope = new Scope("device", constants);
            foreach (var (key, value) in measured)
                scope.Bind(key, value.ToString("R", CultureInfo.InvariantCulture));

            try
            {
                var value = new Evaluator().Eval(formula, scope);
                object? resolved = value.Kind switch
                {
                    ValueKind.Real    => value.AsReal(),
                    ValueKind.Complex => value.AsComplex(),
                    ValueKind.Bool    => value.AsBool(),
                    ValueKind.String  => value.AsString(),
                    _                 => null,
                };

                // R-lvs14-4b. A value that is not finite is not a value: a formula dividing by a
                // width the artwork measured as zero produces an infinity that would compare
                // against a real schematic number and report a mismatch nobody can act on.
                if (resolved is double d && !double.IsFinite(d))
                {
                    notes.Add(LvsDiagnostics.RecognizeParameterFailed(
                        path, ruleName, name, $"It came out {d}."));
                    continue;
                }

                if (resolved is not null) values[name] = resolved;
            }
            catch (Exception ex)
            {
                notes.Add(LvsDiagnostics.RecognizeParameterFailed(path, ruleName, name, ex.Message));
            }
        }

        return (values.Count == 0 ? EmptyParameters : values, string.Join(", ", withheld));
    }

    // ── Where a terminal lands (R-lvs14-3b) ──────────────────────────────────────────────────

    /// <summary>
    /// The net one recognised terminal is on, through the same point-in-piece lookup a placed pin
    /// uses.
    /// </summary>
    /// <remarks>
    /// <b>On the terminal expression's OWN layers, never any-layer</b> — <c>LayoutRead</c>'s
    /// R-ab2-2d, which applies identically here: a terminal on the top metal asked any-layer would
    /// answer with the net of whatever sits under it on the bottom.
    /// </remarks>
    private static int NetUnder(
        DeviceTerminalCandidate terminal, CopperPieces pieces, LayoutRead.NetTable nets,
        List<Diagnostic> notes, LvsGeometryNaming naming, string path,
        int deviceIndex, int terminalIndex, List<LvsPadGeometry> padGeometry)
    {
        foreach (var layer in terminal.Layers)
        {
            int index = pieces.IndexAt(terminal.X, terminal.Y, layer);
            if (index < 0) continue;

            padGeometry.Add(new LvsPadGeometry(
                deviceIndex, terminalIndex, terminal.X, terminal.Y,
                layer, terminal.WidthDbu, index));

            return nets.Of(pieces.NetOfPiece(index));
        }

        // The terminal IS copper by construction — it came out of a layer region — so landing on
        // no PIECE means the partition never saw that layer: the stackup does not call it a
        // conductor. That is the existing finding, and it already names the layer for exactly this
        // reason, so it is carried rather than given a second spelling.
        var named = terminal.Layers.Count > 0 ? terminal.Layers[0] : default;
        padGeometry.Add(new LvsPadGeometry(
            deviceIndex, terminalIndex, terminal.X, terminal.Y, named, terminal.WidthDbu, -1));

        notes.Add(LvsDiagnostics.PinOnNoCopper(
            path, (terminalIndex + 1).ToString(CultureInfo.InvariantCulture),
            named, naming.NameOfLayer(named)));

        return nets.Open();
    }

    // ── What it is called (R-lvs14-3e) ───────────────────────────────────────────────────────

    /// <summary>
    /// A recognised device's path — <c>R@1.204,3.881</c>.
    /// </summary>
    /// <remarks>
    /// <b>Derived from the coordinate because NOTHING NAMED IT.</b> It is stable across runs on
    /// unchanged artwork, which is R-lvs3-1c's requirement, and it moves when the artwork moves —
    /// which is correct: it is a different piece of copper.
    ///
    /// <para>The number is in the layout's OWN display unit. A path reading <c>R@1204000,3881000</c>
    /// on a board drawn in millimetres is a number nobody can place, which is the trap every unit
    /// note in this repository is about.</para>
    /// </remarks>
    private static string PathOf(DeviceKind kind, DeviceCandidate candidate, LvsGeometryNaming naming)
    {
        string x, y;
        if (naming.Format.IsRawDbu)
        {
            x = candidate.X.ToString(CultureInfo.InvariantCulture);
            y = candidate.Y.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            x = LayoutUnits.Format(candidate.X, naming.Format.Unit, naming.Format.DbuPerMicron);
            y = LayoutUnits.Format(candidate.Y, naming.Format.Unit, naming.Format.DbuPerMicron);
        }

        return $"{Prefix(kind)}@{x},{y}";
    }

    /// <summary>The letter a designer would have used. Cosmetic — the coordinate is the
    /// identity.</summary>
    private static string Prefix(DeviceKind kind) => kind switch
    {
        DeviceKind.Resistor         => "R",
        DeviceKind.Capacitor        => "C",
        DeviceKind.Inductor         => "L",
        DeviceKind.Diode            => "D",
        DeviceKind.Transistor       => "Q",
        DeviceKind.TransmissionLine => "T",
        DeviceKind.Via              => "V",
        DeviceKind.Crossover        => "B",
        _                           => "X",
    };

    /// <summary>
    /// The names a deck formula may use: the technology's own <see cref="Technology.Constants"/>,
    /// over two the process already states elsewhere — <c>TfrSheetResistance</c>, the
    /// <see cref="StackupFilm.SheetResistanceOhmPerSq"/> of the film <paramref name="film"/> names or else the first, and
    /// <c>MimCapDensity</c>, ε0·εr/t of
    /// the stackup's capacitor dielectric — both found by the same <see cref="MmicStackResolver"/>
    /// the MIMCAP and TFR models read.
    /// </summary>
    /// <remarks>
    /// <b>Derived rather than declared</b> so the deck and the circuit models cannot disagree: a
    /// second copy of either number in <c>Constants</c> is what an edit to the layer or the film
    /// would leave behind. A constant of the same name still wins, because it binds in the inner
    /// frame — a process whose film is not ideal states its measured density there.
    /// </remarks>
    internal static Scope ConstantsOf(Technology tech, LayerKey? film = null)
    {
        var derived = new Scope("technology (derived)");
        foreach (var (name, expression, unit) in MmicStackResolver.DeckConstants(tech, film))
            derived.Bind(name, expression, EngineUnit(unit));

        var constants = new Scope("technology", derived);
        foreach (var constant in tech.Constants)
            if (constant.Name is { Length: > 0 })
                constants.Bind(constant.Name, constant.Expression ?? "", EngineUnit(constant.Unit));
        return constants;
    }

    private static string? EngineUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return null;
        string engine = UnitNormalizer.ToEngineUnit(unit);
        return engine.Length > 0 ? engine : null;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
