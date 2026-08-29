using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Matching;

/// <summary>The three files one flatten writes, and the cell folder holding them.</summary>
/// <param name="CellDir">The new cell folder.</param>
/// <param name="CellName">Its leaf name — also the cell's identity.</param>
/// <param name="Files">Every path written, in creation order.</param>
public sealed record MatchFlattenResult(string CellDir, string CellName, IReadOnlyList<string> Files);

/// <summary>
/// <b>Flatten to Cell</b> (match.md §11): turns a designed <c>Match</c> into an ordinary cell whose
/// schematic is the LC network it synthesised.
///
/// <h3>What it is for</h3>
/// <para>Two things, and the second is the one people will actually use. It <b>hands the design
/// over</b> — after flattening every L and C is an ordinary parameter that can be swept, expressed,
/// optimised or replaced by a microstrip equivalent, which is the escape hatch for match.md §7.5's
/// deliberate refusal to let a <c>Match</c> participate in sweeps. And it <b>keeps the design's
/// memory</b>: the terminations travel with it disabled, and so does the <c>Design</c> blob, so a
/// flattened cell six months later still knows what it was designed for.</para>
///
/// <h3>Why the terminations are written DISABLED rather than omitted</h3>
/// <para>Omitted, the design intent is gone the moment the cell is opened. Enabled, the cell
/// short-circuits its own ports the moment it is placed. Disabled, it simulates correctly against
/// whatever it is wired into <i>and</i> a user can enable the two <c>Term</c>s and run an
/// S-parameter analysis on the cell alone to reproduce the Designer's own plot — which is the first
/// thing anyone wants to do after flattening. The annotation says so, in one line.</para>
///
/// <h3>Series arms are TWO components</h3>
/// <para>An <c>L</c> and a <c>C</c>, never one <c>L</c> carrying a <c>C=</c> parameter, because the
/// user's next action is to edit, sweep or replace individual elements. <c>InductorModel</c> would
/// stamp the combined form identically, which is exactly why someone will eventually "simplify" it;
/// <c>MatchFlattenTests</c> holds it shut.</para>
/// </summary>
public static class MatchFlatten
{
    // ── Geometry (world units; 100 = one grid square) ─────────────────────────
    // The spine runs left to right at y = 0. Shunt arms drop below it; the two termination annexes
    // sit clear of the ladder on either side, reached by a lead that goes UP and over — nothing else
    // is ever above the spine, so that route cannot cross a shunt leg however long the ladder is.

    private const double SeriesPitch = 600.0;   // one series element, lead to lead
    // 700, matching MatchLadderLayout.Pitch: a shunt element's labels now sit BESIDE it rather than
    // under it (see Element), and 400 is not wide enough for one to clear the next column.
    private const double ShuntPitch  = 700.0;   // two shunt elements sharing one node
    private const double ShuntY      = 400.0;   // a shunt element's centre, below the spine
    private const double ShuntGroundY = 700.0;  // where that arm's own Ground sits
    private const double LeadRun     = 100.0;   // wire stub between a node and a pin
    private const double AnnexGap    = 500.0;   // interface pin to the first termination column
    private const double AnnexLift   = 400.0;   // how far above the spine the annex lead runs

    /// <summary>
    /// The parameter name a <c>Match</c> component carries its design under. A flattened CELL keeps
    /// the same blob on <see cref="CcellFile.MatchDesign"/> instead of on a declared parameter — see
    /// that field for why a base64 blob cannot be one.
    /// </summary>
    public const string DesignParameter = MatchEmbedding.DesignParameter;

    // ── The default name ──────────────────────────────────────────────────────

    /// <summary>The dialog's seeded name, <c>MN1_match</c>.</summary>
    public static string DefaultCellName(string instanceName) =>
        (instanceName.Length == 0 ? "MN" : instanceName) + "_match";

    /// <summary>
    /// The first free <c>name</c>, <c>name_2</c>, <c>name_3</c> … under <paramref name="parentDir"/>.
    /// Only a SUGGESTION: <see cref="Write"/> still refuses an existing folder outright, because
    /// silently writing beside a cell the user named is how the wrong one gets instantiated.
    /// </summary>
    public static string SuggestFreeName(string parentDir, string seed)
    {
        if (!Directory.Exists(Path.Combine(parentDir, seed))) return seed;
        for (int n = 2; n <= 999; n++)
        {
            string candidate = $"{seed}_{n.ToString(CultureInfo.InvariantCulture)}";
            if (!Directory.Exists(Path.Combine(parentDir, candidate))) return candidate;
        }
        return seed;
    }

    // ── The schematic ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the cell's schematic: two <c>Pin</c>s, the ladder as ordinary <c>L</c>/<c>C</c>
    /// instances with wires and <c>Ground</c>s, both terminations disabled, and the design
    /// annotation.
    /// </summary>
    /// <param name="rebuild">The rebuilt design — its network is what gets written.</param>
    /// <param name="design">The design, for the annotation and the echo parameters.</param>
    /// <param name="instanceName">The <c>Match</c> being flattened, named in the annotation.</param>
    /// <param name="stamped">When supplied, the timestamp the annotation quotes (UTC).</param>
    /// <param name="significantDigits">
    /// The Designer's own readout digit count (<c>MatchDesignerSettings.SignificantDigits</c>) — every
    /// element value is written at it. See <see cref="Value"/> for why.
    /// </param>
    public static SchematicEditModel BuildSchematic(
        MatchRebuildResult rebuild, MatchDesign design, string instanceName, DateTime? stamped = null,
        int significantDigits = MatchDesignerSettings.DefaultSignificantDigits)
    {
        ArgumentNullException.ThrowIfNull(rebuild);
        ArgumentNullException.ThrowIfNull(design);
        if (rebuild.Network is null)
            throw new InvalidOperationException(
                "Flatten to Cell: this design does not synthesise, so there is no ladder to write. " +
                (rebuild.Refusal?.Message ?? ""));

        var network = rebuild.Network;
        var plan = MatchFlattenPlan.Build(network);
        var model = new SchematicEditModel();

        // ── the spine ─────────────────────────────────────────────────────────
        double cursor = 0.0;
        int shuntAtNode = 0;

        model.Components.Add(Pin(1, -LeadRun, 0.0, SymbolRotation.R0));

        foreach (var placed in plan.Elements)
        {
            var e = placed.Element;
            if (e.IsShunt)
            {
                if (shuntAtNode > 0)
                {
                    model.Wires.Add(Wire((cursor, 0), (cursor + ShuntPitch, 0)));
                    cursor += ShuntPitch;
                }
                model.Components.Add(Element(e, cursor, ShuntY, SymbolRotation.R0, significantDigits,
                                             shuntGroundOffsetY: ShuntGroundY - ShuntY));
                model.Wires.Add(Wire((cursor, 0), (cursor, 200)));
                model.Components.Add(Ground(cursor, ShuntGroundY));
                model.Wires.Add(Wire((cursor, 600), (cursor, 700)));
                shuntAtNode++;
            }
            else
            {
                model.Wires.Add(Wire((cursor, 0), (cursor + LeadRun, 0)));
                model.Components.Add(Element(e, cursor + SeriesPitch / 2.0, 0.0,
                                             MatchSchematicModel.SeriesRotation, significantDigits));
                model.Wires.Add(Wire((cursor + SeriesPitch - LeadRun, 0), (cursor + SeriesPitch, 0)));
                cursor += SeriesPitch;
                shuntAtNode = 0;
            }
        }

        model.Components.Add(Pin(2, cursor + LeadRun, 0.0, SymbolRotation.R180));

        // ── the two terminations, every part of them Open ─────────────────────
        AddTermination(model, plan.Terminations[0], design.Term1, -AnnexGap, -1.0, 0.0, significantDigits);
        AddTermination(model, plan.Terminations[1], design.Term2, cursor + AnnexGap, +1.0, cursor,
                       significantDigits);

        // ── the design record ─────────────────────────────────────────────────
        model.CanvasObjects.Add(new EditableText
        {
            Text = Annotation(rebuild, design, instanceName, stamped ?? DateTime.UtcNow),
            X = cursor / 2.0,
            Y = 1900.0,
            Width = Math.Max(2600.0, cursor + 1600.0),
            Height = 900.0,
            FontSize = 11f,
        });

        return model;
    }

    /// <summary>
    /// Hangs one end's termination off the ladder: a lead up and over to an annex column, then the
    /// absorbed reactance and the <c>Term</c>, everything <see cref="DisableState.Open"/>.
    /// </summary>
    /// <param name="xa">The annex column's x.</param>
    /// <param name="dir">−1 for the port-1 side, +1 for port 2 — columns grow AWAY from the ladder.</param>
    /// <param name="xPort">Where on the spine the lead leaves.</param>
    private static void AddTermination(
        SchematicEditModel model, FlattenedTermination t, Termination declared,
        double xa, double dir, double xPort, int digits)
    {
        // Up from the spine and across — the one route that cannot cross a shunt leg.
        model.Wires.Add(Wire((xPort, 0), (xPort, -AnnexLift), (xa, -AnnexLift)));

        if (t.Absorbed is { } absorbed && !absorbed.IsShunt)
        {
            // SERIES: the reactance sits between the interface net and the reference resistance,
            // which is where the synthesis assumes it — enabling both reproduces the Designer's own
            // response, not a resistively-terminated approximation of it.
            model.Components.Add(Disabled(
                Element(absorbed, xa, -AnnexLift + 200.0, SymbolRotation.R0, digits)));
            model.Components.Add(Disabled(Term(t.End, t.R, xa, 400.0, digits)));
            model.Wires.Add(Wire((xa, 0), (xa, 200)));
            model.Components.Add(Disabled(Ground(xa, 700.0)));
            model.Wires.Add(Wire((xa, 600), (xa, 700)));
            return;
        }

        model.Components.Add(Disabled(Term(t.End, t.R, xa, -AnnexLift + 200.0, digits)));
        model.Components.Add(Disabled(Ground(xa, 100.0)));
        model.Wires.Add(Wire((xa, 0), (xa, 100)));

        if (t.Absorbed is { } shunt)
        {
            // PARALLEL: a second column beside the Term, on the same interface net.
            double xb = xa + dir * ShuntPitch;
            model.Wires.Add(Wire((xa, -AnnexLift), (xb, -AnnexLift)));
            model.Components.Add(Disabled(
                Element(shunt, xb, -AnnexLift + 200.0, SymbolRotation.R0, digits)));
            model.Components.Add(Disabled(Ground(xb, 100.0)));
            model.Wires.Add(Wire((xb, 0), (xb, 100)));
        }
        else if (declared.Kind != ReactanceKind.None)
        {
            // A termination that declares a reactance the synthesis did not absorb is a state worth
            // nothing here: the ladder is what was written, so nothing is invented to match the
            // declaration. Left deliberately empty.
        }
    }

    // ── Component builders ────────────────────────────────────────────────────

    private static EditableComponent Disabled(EditableComponent c)
    {
        c.Disable = DisableState.Open;
        return c;
    }

    private static EditableComponent Pin(int num, double x, double y, SymbolRotation rot)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Pin, X = x, Y = y, Rotation = rot };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString(CultureInfo.InvariantCulture) });
        c.Parameters.Add(new EditableParameter { Name = "Name", Expression = "", ShowOnSchematic = false });
        return c;
    }

    private static EditableComponent Ground(double x, double y) =>
        new() { Symbol = SymbolKind.Ground, X = x, Y = y, ShowTypeLabel = false, ShowInstanceName = false };

    private static EditableComponent Term(int num, double r, double x, double y, int digits)
    {
        var c = new EditableComponent
        {
            InstanceName = $"T{num.ToString(CultureInfo.InvariantCulture)}",
            Symbol = SymbolKind.Term,
            X = x, Y = y,
        };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString(CultureInfo.InvariantCulture) });
        var (text, unit) = Value(r, MatchQuantity.Resistance, digits);
        c.Parameters.Add(new EditableParameter
        {
            Name = "Z", Expression = text, Unit = unit, Dimension = UnitDimension.Resistance,
        });
        return c;
    }

    /// <summary>
    /// One ladder element as an ordinary <c>L</c> or <c>C</c>. <b>The element keeps MN-1's own
    /// name</b> — <c>L1</c>, <c>C2</c>, <c>CFano</c>, <c>MN1_N1_2</c> — because those are already
    /// unique and already meaningful, and renumbering them would break the only correspondence
    /// between the flattened cell and the Designer that produced it.
    /// </summary>
    /// <param name="shuntGroundOffsetY">
    /// For a LADDER shunt arm, how far below this element its own <c>Ground</c> sits — which is where
    /// the label block goes when it is too wide to sit beside the symbol. Null for the two termination
    /// annexes: nothing stands to their right to bleed into, and their grounds are not a uniform
    /// distance away, so the rule has nothing to measure there.
    /// </param>
    private static EditableComponent Element(
        MatchElement e, double x, double y, SymbolRotation rot, int digits,
        double? shuntGroundOffsetY = null)
    {
        bool isL = e.Type == ElementType.L;
        var c = new EditableComponent
        {
            InstanceName = e.Name,
            Symbol = isL ? SymbolKind.Inductor : SymbolKind.Capacitor,
            X = x, Y = y, Rotation = rot,
        };

        var quantity = isL ? MatchQuantity.Inductance : MatchQuantity.Capacitance;
        var (text, unit) = Value(e.Value, quantity, digits);
        string type = isL ? "L" : "C";
        c.Parameters.Add(new EditableParameter
        {
            Name = type,
            Expression = text,
            Unit = unit,
            Dimension = isL ? UnitDimension.Inductance : UnitDimension.Capacitance,
        });

        // A SHUNT element's three label rows sit beside the symbol and centred on it, exactly as the
        // Designer's own pane places them (owner, 2026-08-20: "adjust the vertical alignment such
        // that the center of all 3 rows of text is at the same y coordinate as the center of the
        // component symbol … do this for the flattened cell too"). The offsets are the pane's own
        // decision rather than a second pair of numbers: the two drawings are meant to be the same
        // drawing, and the whole point of flattening is that the user recognises what they designed.
        // They are ordinary per-label offsets, so the user can still drag any of them.
        //
        // … including the fallback: a block too wide for the gap to the next column goes UNDER the
        // arm's ground instead (owner, same round: "the flatten to cell should also do this"). Each
        // drawing measures the rows IT draws, which is why the strings below are built the way
        // EditableComponent.BuildRenderModel will build them — at the Designer's own digit count the
        // two rows are the same width, and a flatten asked for more digits than the pane shows has a
        // genuinely wider value row that genuinely does bleed at the same pitch.
        if (rot == SymbolRotation.R0)
        {
            var (dx, dy) = shuntGroundOffsetY is { } groundDy
                ? MatchShuntLabels.Offsets(
                      [type, e.Name, $"{type} = {text} {unit}"], ShuntPitch, groundDy)
                : (MatchSchematicModel.ShuntLabelDx, MatchSchematicModel.ShuntLabelDy);
            for (int i = 0; i < 3; i++)
                c.LabelOffsets.Add((dx, dy));
        }

        return c;
    }

    private static EditableWire Wire(params (double X, double Y)[] points)
    {
        var w = new EditableWire();
        w.Points.AddRange(points);
        return w;
    }

    // ── Values ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A value in an engineering unit, at the <b>Designer's own significant digits</b>.
    /// </summary>
    /// <remarks>
    /// <b>The flattened cell reads exactly as the Designer's pane did</b> (owner, 2026-08-28: an
    /// inductor shown as 1.201 pH must not land in the cell as 1.20099999999304 pH). The digit count
    /// is the readout setting, and the same <see cref="MatchValueFormat.Significant"/> the pane and
    /// the value grid format with — so the two drawings show one number, not two spellings of it.
    ///
    /// <para><b>This is a deliberate trade against exactness, made by the owner.</b> The values
    /// written are ROUNDED ones, so the flattened cell no longer reproduces the <c>Match</c>
    /// component's S-parameters to machine precision — at the default three digits it agrees to
    /// roughly one part in a thousand, which is the rounding and nothing else. The earlier writer
    /// used 15 digits precisely to hold that agreement at 1e-12; <c>MatchFlattenTests</c> still
    /// gates it there by asking for 15 digits explicitly, which is what separates a placement or
    /// wiring bug (orders of magnitude) from the rounding (relative 10^−digits).</para>
    /// </remarks>
    private static (string Text, string Unit) Value(double value, MatchQuantity quantity, int digits)
    {
        string unit = MatchValueFormat.AutoUnitFor(value, quantity);
        // The Designer's Auto ladder has one rung the Parameter Editor's unit picker does not offer;
        // writing it would give the user a unit they could read but not re-select.
        if (unit == "fH") unit = "pH";
        double scaled = value / MatchValueFormat.Scale(unit);
        return (MatchValueFormat.Significant(scaled, digits), unit);
    }

    // ── The annotation ────────────────────────────────────────────────────────

    /// <summary>
    /// The design record the cell carries in plain text (match.md §11.1) — band, order, response,
    /// both terminations, the achieved return loss, insertion loss and ripple, Π N², and the date.
    /// </summary>
    public static string Annotation(
        MatchRebuildResult rebuild, MatchDesign design, string instanceName, DateTime stampedUtc)
    {
        ArgumentNullException.ThrowIfNull(rebuild);
        ArgumentNullException.ThrowIfNull(design);

        var network = rebuild.Network!;
        var eff = design.Effective;
        double worst = MatchResponse.WorstReturnLossDb(network, design.Bands);
        var (il, ripple) = MatchResponse.InsertionLoss(network, eff.F1, eff.F2);

        // The EFFECTIVE bands (match.md §18.3), because they are what the ladder in the cell was
        // built to; the requested pair follows on its own line when symmetrisation moved one, so the
        // record states both and a reader can see which is which.
        string band = design.BandCount >= 2
            ? string.Join(" & ", design.Bands.Select(b => $"{Ghz(b.Lo)} – {Ghz(b.Hi)}")) + " GHz"
            : $"{Ghz(design.F1)} – {Ghz(design.F2)} GHz";
        var lines = new List<string>
        {
            $"Matching network flattened from {instanceName}.",
            $"Band {band} · order {design.Order.ToString(CultureInfo.InvariantCulture)} · "
            + $"{BandWord(design)} · "
            + $"{ResponseName(design.Response)}",
            $"Termination 1 (pin 1): {TerminationLine(design, design.Term1)}",
            $"Termination 2 (pin 2): {TerminationLine(design, design.Term2)}",
            $"Worst in-band return loss {F(-worst, "0.00")} dB · insertion loss {F(il, "0.000")} dB · "
                + $"ripple {F(ripple, "0.000")} dB",
            $"Π N² {F(rebuild.Achieved, "0.#####")} / {F(rebuild.Required, "0.#####")}"
                + (rebuild.OnTarget ? "" : "  (not reached)"),
        };

        if (design.BandCount >= 2 && eff.Widened)
        {
            var requested = design.BandCount >= 3
                ? new[] { (design.F1, design.F2), (design.F3, design.F4), (design.F5, design.F6) }
                : [(design.F1, design.F2), (design.F3, design.F4)];
            lines.Insert(2,
                "Requested "
                + string.Join(" & ", requested.Select(b => $"{Ghz(b.Item1)} – {Ghz(b.Item2)}"))
                + " GHz. " + eff.Note);
        }

        // The Terms carry the LADDER's own port resistances, because reproducing the Designer's plot
        // is what they are for and that is what the plot is referenced to. In a finished design the
        // two are the same number — the transforms exist precisely to bring the ladder's end up to
        // the termination's. In an unfinished one they are not, and a user reading 1.68 Ω beside a
        // 200 Ω termination deserves the sentence rather than the puzzle.
        if (!rebuild.OnTarget)
            lines.Add(
                $"The Terms carry the ladder's own reference — {Ohms(network.R1)} and "
                + $"{Ohms(network.R2)} — not the terminations above: Π N² has not reached its target, "
                + "so the transforms have not yet brought the ladder's ends up to them.");

        lines.AddRange(new[]
        {
            "The two Term components and the terminations' absorbed reactances are DISABLED (Open), so "
                + "this cell simulates against whatever it is wired into. Enable both Terms and run an "
                + "S-parameter analysis on the cell alone to reproduce the Match Designer's own response.",
            $"Flattened {stampedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.",
        });
        return string.Join("\n", lines);
    }

    private static string TerminationLine(MatchDesign design, Termination t)
    {
        string r = MatchValueFormat.FormatWithUnit(t.R, MatchQuantity.Resistance, MatchValueFormat.AutoUnit, 5);
        if (t.Kind == ReactanceKind.None) return $"{r}, resistive";

        var quantity = t.Kind == ReactanceKind.L ? MatchQuantity.Inductance : MatchQuantity.Capacitance;
        string x = MatchValueFormat.FormatWithUnit(t.Value, quantity, MatchValueFormat.AutoUnit, 5);
        string how = t.Topology == TerminationTopology.Series ? "series" : "parallel";
        return $"{r} {how} {x} (Q {F(t.QAt(design.Omega0), "0.###")})"
               + (t.Probed ? ", probed from the schematic" : "");
    }

    /// <summary>What a record calls the network: its FORM, or its band count while multiband.</summary>
    /// <remarks>
    /// Every multiband row is bandpass (match.md §18.6), so the form word would say nothing there and
    /// the band count is what distinguishes the design.
    /// </remarks>
    private static string BandWord(MatchDesign design) => design.BandCount switch
    {
        >= 3 => "tri-band",
        2 => "dual-band",
        _ => FormName(design.Form),
    };

    private static string FormName(NetworkForm form) => form switch
    {
        NetworkForm.Lowpass  => "lowpass",
        NetworkForm.Highpass => "highpass",
        _                    => "bandpass",
    };

    private static string ResponseName(ResponseShape shape) => shape switch
    {
        ResponseShape.ChebyshevFano     => "Chebyshev — single-match (optimum)",
        ResponseShape.ChebyshevTwoEnded => "Chebyshev — double-match (exact)",
        ResponseShape.Butterworth       => "Butterworth",
        _                               => "Bessel",
    };

    private static string Ohms(double r) =>
        MatchValueFormat.FormatWithUnit(r, MatchQuantity.Resistance, MatchValueFormat.AutoUnit, 5);

    private static string Ghz(double hz) => (hz / 1e9).ToString("0.####", CultureInfo.InvariantCulture);
    private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    // ── The symbol ────────────────────────────────────────────────────────────

    /// <summary>
    /// A <b>copy</b> of the built-in <c>Match</c> glyph — same primitives, same two pins at the same
    /// places, which is what lets §11.2's in-place replacement keep every wire.
    /// </summary>
    /// <remarks>
    /// <b>Copied, never shared.</b> <c>BuiltInSymbols</c> caches ONE <see cref="Symbol"/> instance per
    /// kind and its primitives are mutable classes, so handing those objects on would leave the
    /// generated cell's symbol aliased to the application's own glyph. The round-trip through the
    /// <c>.csym</c> serializer is the deep copy, and it is the same bytes the file gets.
    /// </remarks>
    public static Symbol MatchSymbolCopy() => MatchSymbolCopy(NetworkForm.Bandpass, 1);

    /// <summary>
    /// The glyph <paramref name="design"/> renders as — the flattened cell wears the same symbol the
    /// Match wore, so the in-place replacement of §11.2 changes what the component IS and not what it
    /// looks like. A lowpass ladder that flattened into a bandpass glyph would be the schematic
    /// telling a reader something untrue about the cell it just gained.
    /// </summary>
    /// <inheritdoc cref="MatchSymbolCopy()"/>
    public static Symbol MatchSymbolCopy(MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return MatchSymbolCopy(design.Form, design.BandCount);
    }

    private static Symbol MatchSymbolCopy(NetworkForm form, int bandCount) =>
        SymbolPersistence.Deserialize(
            SymbolPersistence.Serialize(BuiltInSymbols.PrimitivesForMatch(form, bandCount)));

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the cell folder: <c>.ccell</c>, <c>schematic/&lt;name&gt;.csch</c>,
    /// <c>symbol/&lt;name&gt;.csym</c>.
    /// </summary>
    /// <remarks>
    /// <b>All or nothing.</b> The folder is created here and removed again if any later step throws,
    /// so a failure part-way leaves no partial cell behind — a half-written cell is worse than none,
    /// because the workspace scanner would list it and a user would place it. An EXISTING folder is
    /// refused outright rather than merged into: flattening twice must prompt, never overwrite.
    /// </remarks>
    /// <param name="parentDir">Where the cell folder goes — normally the workspace root.</param>
    /// <param name="cellName">The new cell's name; validated by <c>NameValidator</c>.</param>
    /// <param name="schematic">What <see cref="BuildSchematic"/> produced.</param>
    /// <param name="design">Carried onto the <c>.ccell</c> so the cell remembers what it was.</param>
    public static MatchFlattenResult Write(
        string parentDir, string cellName, SchematicEditModel schematic, MatchDesign design)
        => Write(parentDir, cellName, schematic, design, faultAfterSchematic: null);

    /// <param name="faultAfterSchematic">
    /// <b>A test seam, null everywhere in the application.</b> The all-or-nothing guarantee above is
    /// about a failure PART-WAY — after the folder and the schematic exist and before the cell is
    /// complete — and there is no portable way to make a filesystem write fail at that exact point.
    /// A test that could only ever fail on the first step would be testing the argument check.
    /// </param>
    /// <inheritdoc cref="Write(string,string,SchematicEditModel,MatchDesign)"/>
    internal static MatchFlattenResult Write(
        string parentDir, string cellName, SchematicEditModel schematic, MatchDesign design,
        Action? faultAfterSchematic)
    {
        ArgumentNullException.ThrowIfNull(schematic);
        ArgumentNullException.ThrowIfNull(design);

        string cellDir = Path.Combine(parentDir, cellName);
        if (Directory.Exists(cellDir))
            throw new IOException(
                $"A cell named '{cellName}' already exists here. Choose another name — flattening " +
                "never writes over a cell that is already in the workspace.");

        var written = new List<string>();
        try
        {
            CellFolder.CreateCellFolder(parentDir, cellName);

            string schematicFile = cellName + CellFolder.ViewExtension(ViewType.Schematic);
            string schematicPath = Path.Combine(
                CellFolder.SubFolderPath(cellDir, ViewType.Schematic), schematicFile);
            SchematicPersistence.SaveToFile(schematicPath, schematic, cellName: cellName);
            written.Add(schematicPath);

            faultAfterSchematic?.Invoke();

            string symbolFile = cellName + CellFolder.ViewExtension(ViewType.Symbol);
            string symbolPath = Path.Combine(
                CellFolder.SubFolderPath(cellDir, ViewType.Symbol), symbolFile);
            SymbolPersistence.SaveToFile(symbolPath, MatchSymbolCopy(design));
            written.Add(symbolPath);

            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            ccell.PrimarySchematic = schematicFile;
            ccell.PrimarySymbol = symbolFile;
            ccell.NumPorts = 2;
            ccell.MatchDesign = MatchEmbedding.Encode(design);
            CellPersistence.SaveToFile(ccellPath, ccell);
            written.Add(ccellPath);

            return new MatchFlattenResult(cellDir, cellName, written);
        }
        catch
        {
            TryDeleteFolder(cellDir);
            throw;
        }
    }

    /// <summary>
    /// Removes a folder this class created. Best effort by design: the caller is already reporting a
    /// failure, and a cleanup that threw would replace that report with a less useful one.
    /// </summary>
    internal static void TryDeleteFolder(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* leave it */ }
    }

    // ── Reading the design back ───────────────────────────────────────────────

    /// <summary>
    /// The design a flattened cell folder carries, or null when it is not one (match.md §11.1's
    /// "a flattened cell that has forgotten what it was is a dead end").
    /// </summary>
    public static MatchDesign? TryReadDesign(string cellDir)
    {
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return null;

        try
        {
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            return MatchEmbedding.TryDecode(ccell.MatchDesign, out var design) ? design : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// The design a <c>Match</c> COMPONENT carries on its own <c>Design</c> parameter. A flattened
    /// cell's instance carries none — the blob lives on the cell (see <see cref="CcellFile"/>) — so
    /// re-opening a flattened design goes through the cell-folder overload above.
    /// </summary>
    public static MatchDesign? TryReadDesign(EditableComponent comp)
    {
        ArgumentNullException.ThrowIfNull(comp);
        string? payload = comp.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, DesignParameter, StringComparison.Ordinal))
            ?.Expression;
        return MatchEmbedding.TryDecode(payload, out var design) ? design : null;
    }
}
