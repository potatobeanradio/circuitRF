using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Cells;

namespace CircuitRF.Ui.Schematic;

/// <summary>What one <c>.model</c> file holds, once read.</summary>
/// <param name="Translations">Every card in the file, supported or not, in file order.</param>
/// <param name="Notes">The reader's own notes — lines it skipped, definitions it could not use.</param>
/// <param name="Error">Why nothing could be read at all. Null when the file was read.</param>
public sealed record ModelCardScan(
    IReadOnlyList<ModelCardTranslation> Translations,
    IReadOnlyList<SpiceNetlistNote>     Notes,
    string?                             Error)
{
    /// <summary>The cards that can actually become a cell.</summary>
    public IReadOnlyList<ModelCardTranslation> Supported
        => [.. Translations.Where(t => t.IsSupported)];
}

/// <summary>Where a built cell landed, and what is worth saying about it.</summary>
/// <param name="CellDir">The cell folder.</param>
/// <param name="SchematicPath">Its primary schematic — what to open afterwards.</param>
/// <param name="Report">
/// The lines to post to Messages: how many parameters were carried, which were not, and every
/// decision the translation made. <b>Never empty</b> — an import that says only "created" leaves the
/// user to discover a dropped parameter from a wrong answer three days later.
/// </param>
public sealed record ModelCardCellResult(
    string CellDir, string SchematicPath, IReadOnlyList<string> Report);

/// <summary>
/// Builds a circuitRF cell around one SPICE <c>.model</c> card — a schematic holding the native
/// component with the card's parameters on it, pins already wired to its terminals, and a symbol
/// copied from that component's own artwork.
///
/// <para><b>Why a CELL and not just a placed component.</b> A card is a parameter set, and a
/// parameter set the user has to re-type onto every instance is the problem this exists to remove.
/// A cell is the thing circuitRF already knows how to place, reference, sweep and version, and the
/// copied <c>.csym</c> is what lets a user redraw the part later without losing the parameters —
/// which is the whole point of copying the artwork rather than referencing it.</para>
///
/// <para><b>The pins are the deliverable.</b> A cell whose schematic holds a device and no pins
/// resolves to zero ports and cannot be placed in another schematic at all, so wiring them is not a
/// convenience — it is the difference between an import and a file on disk.</para>
///
/// <para>Writing is <see cref="MatchFlatten"/>'s all-or-nothing shape, deliberately: the folder is
/// removed again if any later step throws, because a half-written cell is worse than none — the
/// workspace scanner lists it and a user places it.</para>
/// </summary>
public static class ModelCardCellBuilder
{
    /// <summary>Grid pitch. Everything electrical sits on it, as it must to connect.</summary>
    private const double P = 100.0;

    /// <summary>How far a pin's own connection point sits from the device terminal it serves.</summary>
    private const double Lead = 100.0;

    /// <summary>Half the device glyph, in world units — every built-in device's terminals sit here.</summary>
    private const double Half = 200.0;

    /// <summary>
    /// A <c>Pin</c>'s connection point is 100 to the RIGHT of its own origin at R0
    /// (<c>SymbolPortDefs</c>). Placing one therefore means placing its ORIGIN, and this is the
    /// offset that has to be undone to land the point where it is wanted.
    /// </summary>
    private const double PinReach = 100.0;

    // ─────────────────────────────────────────────────────────────────────────
    //  Reading
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extensions the project tree offers the command on. Deliberately narrow: the item appears on
    /// a bookmarked file without anything reading it, so the extension is the whole of what decides,
    /// and offering it on every <c>.txt</c> would put a dead menu item on most of a workspace.
    /// File ▸ Import ▸ Model Card… is where a wider net belongs — there the user has already said
    /// what the file is by choosing it.
    /// </summary>
    public static bool IsModelCardFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".model" or ".mod";

    /// <summary>
    /// Reads every <c>.model</c> card in <paramref name="path"/> and says what circuitRF can make of
    /// each. Never throws for a file it cannot read — that is the <c>Error</c> field, because this
    /// runs behind a menu item and a stack trace is not an answer.
    /// </summary>
    public static ModelCardScan Scan(string path)
    {
        SpiceNetlistResult result;
        try
        {
            result = SpiceNetlistReader.ReadFile(path);
        }
        catch (Exception ex)
        {
            return new ModelCardScan([], [], $"{Path.GetFileName(path)} could not be read: {ex.Message}");
        }

        return new ModelCardScan(
            SpiceModelCardTranslation.TranslateAll(result.ModelCards),
            result.Notes,
            result.ModelCards.Count > 0
                ? null
                : $"{Path.GetFileName(path)} contains no '.model' cards.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Component identity
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The palette component that implements an engine reference.
    ///
    /// <para><b>Written out rather than routed through <c>ComponentTypeRegistry.TryParseCode</c>,</b>
    /// which is the <c>.csch</c> LOAD path: it resolves "Diode" and "BJT_NPN" only by falling
    /// through to <c>Enum.TryParse</c> on the enum member name, and "FET_Statz" does not match
    /// <c>FetStatz</c> and never will. Relying on that coincidence would make this feature depend on
    /// how the enum members happen to be spelled.</para>
    /// </summary>
    public static SymbolKind? SymbolFor(string engineReference) => engineReference switch
    {
        "Diode"        => SymbolKind.Diode,
        "BJT_NPN"      => SymbolKind.BjtNpn,
        "BJT_PNP"      => SymbolKind.BjtPnp,
        "FET_Statz"    => SymbolKind.FetStatz,
        "FET_Curtice"  => SymbolKind.FetCurtice,
        "PFET_Statz"   => SymbolKind.PFetStatz,
        "PFET_Curtice" => SymbolKind.PFetCurtice,
        "JFET_N"       => SymbolKind.JfetN,
        "JFET_P"       => SymbolKind.JfetP,
        "VDMOS_N"      => SymbolKind.VdmosN,
        "VDMOS_P"      => SymbolKind.VdmosP,
        "MOS1_N"       => SymbolKind.Mos1N,
        "MOS3_N"       => SymbolKind.Mos3N,
        "MOS3_P"       => SymbolKind.Mos3P,
        "MOS1_P"       => SymbolKind.Mos1P,
        "Bead"         => SymbolKind.Bead,
        "R"            => SymbolKind.Resistor,
        "C"            => SymbolKind.Capacitor,
        "L"            => SymbolKind.Inductor,
        _              => null,
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  The schematic
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cell's schematic: the device at the origin, its card's parameters on it, and one
    /// <c>Pin</c> per terminal on a short lead.
    ///
    /// <para>Pin NUMBERS follow the device's own terminal order — the diode's anode is port 1, the
    /// transistor's collector/drain is port 1 — so the cell's ports line up index-for-index with the
    /// symbol copied from the same component. Renumbering them would give the cell a symbol whose
    /// pins point at the wrong nets, which reads as correct and is not.</para>
    /// </summary>
    public static SchematicEditModel BuildSchematic(ModelCardTranslation translation, string cellName)
    {
        ArgumentNullException.ThrowIfNull(translation);
        if (translation.Binding is not { } binding)
            throw new InvalidOperationException(
                "A refused card has no component to build a schematic from.");

        if (SymbolFor(binding.EngineReference) is not { } kind)
            throw new InvalidOperationException(
                $"No palette component implements '{binding.EngineReference}'.");

        var model = new SchematicEditModel();

        var device = new EditableComponent
        {
            InstanceName = ComponentTypeRegistry.Get(kind).InstancePrefix + "1",
            Symbol       = kind,
            X            = 0,
            Y            = 0,
        };
        ApplyParameters(device, kind, binding.Parameters);
        model.Components.Add(device);

        // The card's own name, written beside the device. A cell that is renamed afterwards still
        // says which card it was built from, which is the one fact the parameters themselves
        // cannot carry.
        model.CanvasObjects.Add(new EditableText
        {
            Text     = Annotation(translation, cellName),
            X        = 0,
            Y        = 900,
            Width    = 2400,
            Height   = 400,
            FontSize = 11f,
        });

        var terminals = SymbolPortDefs.For(kind);

        // A MESFET card's RD/RS are real series resistors and circuitRF's FET has no parameter for
        // them, so they go in the schematic — which is exactly what a cell is for. Nothing else in
        // the supported set needs this: the diode's Rs, the BJT's Rb/Re/Rc and the MOS and JFET
        // families' Rd/Rs are model parameters the elaborator mints internal nodes for. Those two
        // families are the ones to watch — they state RD and RS under exactly the same names, and
        // placing them a second time in the schematic would put the resistance in the device AND
        // beside it.
        var (rd, rs) = kind is SymbolKind.FetStatz or SymbolKind.FetCurtice
                            or SymbolKind.PFetStatz or SymbolKind.PFetCurtice
            ? SpiceModelCardTranslation.MesfetLeadResistance(translation.Card)
            : (null, null);

        for (int i = 0; i < terminals.Length; i++)
        {
            var (name, lx, ly) = terminals[i];
            string? series = name switch
            {
                "d" => rd,
                "s" => rs,
                _   => null,
            };
            AddTerminal(model, i + 1, name, lx, ly, series);
        }

        return model;
    }

    /// <summary>
    /// Wires one device terminal out to its own pin, optionally through a series resistor.
    ///
    /// <para>The four terminal positions a built-in device uses are (0,−200) top, (0,+200) bottom,
    /// (−200,0) left and (+200,0) right, and each gets the pin rotation that puts the pin's own
    /// connection point ON the lead rather than beside it. Only the MOS family uses the fourth —
    /// it is where the bulk terminal sits.</para>
    /// </summary>
    private static void AddTerminal(
        SchematicEditModel model, int portNumber, string terminalName,
        double lx, double ly, string? seriesResistance)
    {
        // Outward from the glyph — the direction the terminal already points.
        (double ux, double uy) = (Math.Sign(lx), Math.Sign(ly));

        // A series element takes two grid squares of the lead; without one the pin sits one square
        // out. Both keep every point on P.
        double run = seriesResistance is not null ? 4 * P : Lead;

        double px = lx + ux * run, py = ly + uy * run;

        if (seriesResistance is not null)
        {
            // Mid-lead, so its own ±200 terminals land on the two wire segments either side.
            double mx = lx + ux * 2 * P, my = ly + uy * 2 * P;
            var r = new EditableComponent
            {
                InstanceName = $"R{terminalName.ToUpperInvariant()}",
                Symbol       = SymbolKind.Resistor,
                X            = mx,
                Y            = my,
                // A resistor's own terminals are vertical; a horizontal lead needs it turned.
                Rotation     = uy != 0 ? SymbolRotation.R0 : SymbolRotation.R90,
            };
            r.Parameters.Add(new EditableParameter
            {
                Name = "R", Expression = seriesResistance, Unit = "Ω",
                Dimension = UnitDimension.Resistance, ShowOnSchematic = true,
            });
            model.Components.Add(r);
        }
        else
        {
            model.Wires.Add(Wire(lx, ly, px, py));
        }

        // The pin's ORIGIN, placed so its connection point lands on (px,py). R90 puts the point
        // BELOW the origin, R270 above, R0 to the right, R180 to the left.
        var (rot, ox, oy) = (ux, uy) switch
        {
            (0, < 0) => (SymbolRotation.R90,  0.0,      -PinReach),  // top terminal
            (0, > 0) => (SymbolRotation.R270, 0.0,      +PinReach),  // bottom terminal
            (< 0, 0) => (SymbolRotation.R0,   -PinReach, 0.0),       // left terminal
            _        => (SymbolRotation.R180, +PinReach, 0.0),       // right terminal
        };

        var pin = new EditableComponent
        {
            Symbol   = SymbolKind.Pin,
            X        = px + ox,
            Y        = py + oy,
            Rotation = rot,
        };
        pin.Parameters.Add(new EditableParameter
        {
            Name = "Num", Expression = portNumber.ToString(CultureInfo.InvariantCulture),
        });
        // The terminal's own letter — 'a'/'c', 'g'/'d'/'s', 'c'/'b'/'e'. A three-pin cell whose pins
        // are called 1, 2 and 3 is one whose user has to remember which is the gate.
        pin.Parameters.Add(new EditableParameter
        {
            Name = "Name", Expression = terminalName, ShowOnSchematic = false,
        });
        model.Components.Add(pin);
    }

    private static EditableWire Wire(double x1, double y1, double x2, double y2)
    {
        var w = new EditableWire();
        w.Points.Add((x1, y1));
        w.Points.Add((x2, y2));
        return w;
    }

    /// <summary>
    /// Puts the card's parameters onto the device, in <b>base SI units</b>.
    ///
    /// <para><b>The unit token is the whole of this method's difficulty.</b> A schematic row carries
    /// a value AND a unit, and the registry's defaults use convenient ones — the diode's Cj0 is in
    /// picofarads, the inductor's L in nanohenries. A card states everything unscaled, so writing a
    /// card's <c>CJO=2e-12</c> into a row that says "pF" is a capacitance a trillion times too
    /// small, and it simulates. Every imported row therefore gets the BASE unit for its dimension,
    /// taken from the registry's own declaration of what that dimension is.</para>
    ///
    /// <para>Rows the card does not state are left off entirely rather than written at their
    /// defaults: an absent row IS the default, and writing them out would make a later change to a
    /// default silently not reach cells imported before it.</para>
    /// </summary>
    private static void ApplyParameters(
        EditableComponent device, SymbolKind kind, IReadOnlyList<ModelCardParameter> parameters)
    {
        var defaults = ComponentTypeRegistry.DefaultParameters(kind, 0)
            .ToDictionary(d => d.Name, StringComparer.Ordinal);

        foreach (var p in parameters)
        {
            // A name with no declared row is dimensionless by construction here: every dimensioned
            // parameter of every supported device HAS a row, and the ones that do not (a resistor's
            // TC1, a temperature in °C) carry no unit token in circuitRF's scheme either.
            var dimension = defaults.TryGetValue(p.Name, out var d) ? d.Dimension : UnitDimension.None;

            device.Parameters.Add(new EditableParameter
            {
                Name            = p.Name,
                Expression      = p.Expression,
                Unit            = BaseUnit(dimension),
                Dimension       = dimension,
                ShowOnSchematic = d.ShowOnSchematic,
            });
        }
    }

    /// <summary>
    /// The unit token that means "unscaled" for a dimension — the one a <c>.model</c> card's numbers
    /// are already in. Every token here is a member of that dimension's own closed option list
    /// (<c>ComponentTypeRegistry.UnitOptions</c>), so an imported row's unit combo shows a real
    /// choice rather than a value the control has to reject.
    /// </summary>
    private static string BaseUnit(UnitDimension d) => d switch
    {
        UnitDimension.Resistance  => "Ω",
        UnitDimension.Inductance  => "H",
        UnitDimension.Capacitance => "F",
        UnitDimension.Frequency   => "Hz",
        UnitDimension.Voltage     => "V",
        UnitDimension.Current     => "A",
        UnitDimension.Conductance => "S",
        UnitDimension.Power       => "W",
        UnitDimension.Length      => "metre",
        UnitDimension.Angle       => "rad",
        _                         => "",
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  The symbol
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A private copy of the native component's artwork.
    ///
    /// <para>Round-tripped through <see cref="SymbolPersistence"/> — the same thing
    /// <c>MatchFlatten.MatchSymbolCopy</c> does, and for the same reason: <c>BuiltInSymbols</c>
    /// hands back a STATIC cached instance shared by every renderer in the process, and a cell that
    /// held a reference to it would let the symbol editor mutate the palette's own glyph. Serialising
    /// and reading back is the deep copy, and it costs one string on an import.</para>
    ///
    /// <para>Copying it at all — rather than pointing the cell at the built-in — is what the user
    /// gets out of this: the symbol becomes the cell's own file, editable afterwards without
    /// touching the parameters underneath it.</para>
    /// </summary>
    public static Symbol BuildSymbol(SymbolKind kind) =>
        SymbolPersistence.Deserialize(
            SymbolPersistence.Serialize(BuiltInSymbols.Primitives(kind)));

    // ─────────────────────────────────────────────────────────────────────────
    //  Writing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the cell folder — <c>.ccell</c>, <c>schematic/&lt;name&gt;.csch</c>,
    /// <c>symbol/&lt;name&gt;.csym</c> — and returns what to tell the user.
    /// </summary>
    /// <remarks>
    /// <b>All or nothing</b>, exactly as <c>MatchFlatten.Write</c> is: the folder is removed again if
    /// any later step throws. An EXISTING folder is refused rather than merged into — importing the
    /// same card twice must prompt, never overwrite a cell someone has since edited.
    /// </remarks>
    public static ModelCardCellResult Write(
        string parentDir, string cellName, ModelCardTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(translation);
        if (translation.Binding is not { } binding)
            throw new InvalidOperationException($"'{translation.Card.Name}' was refused; nothing to write.");
        if (SymbolFor(binding.EngineReference) is not { } kind)
            throw new InvalidOperationException($"No palette component implements '{binding.EngineReference}'.");

        string cellDir = Path.Combine(parentDir, cellName);
        if (Directory.Exists(cellDir))
            throw new IOException(
                $"A cell named '{cellName}' already exists here. Choose another name — importing a "
                + "model card never writes over a cell that is already in the workspace.");

        try
        {
            CellFolder.CreateCellFolder(parentDir, cellName);

            var schematic = BuildSchematic(translation, cellName);

            string schematicFile = cellName + CellFolder.ViewExtension(ViewType.Schematic);
            string schematicPath = Path.Combine(
                CellFolder.SubFolderPath(cellDir, ViewType.Schematic), schematicFile);
            SchematicPersistence.SaveToFile(schematicPath, schematic, cellName: cellName);

            string symbolFile = cellName + CellFolder.ViewExtension(ViewType.Symbol);
            string symbolPath = Path.Combine(
                CellFolder.SubFolderPath(cellDir, ViewType.Symbol), symbolFile);
            SymbolPersistence.SaveToFile(symbolPath, BuildSymbol(kind));

            int ports = SymbolPortDefs.For(kind).Length;

            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            ccell.PrimarySchematic = schematicFile;
            ccell.PrimarySymbol    = symbolFile;
            ccell.NumPorts         = ports;
            CellPersistence.SaveToFile(ccellPath, ccell);

            return new ModelCardCellResult(cellDir, schematicPath, Report(translation, kind, cellName));
        }
        catch
        {
            TryDeleteFolder(cellDir);
            throw;
        }
    }

    /// <summary>
    /// Removes a folder this class created. <b>Best effort by design</b>: the caller is already
    /// reporting a failure, and a cleanup that threw would replace that report with a less useful
    /// one. Written here rather than borrowed from <c>MatchFlatten</c>'s identical helper because
    /// importing a model card does not otherwise depend on the Match designer, and three lines of
    /// recursive delete is not worth a dependency in that direction.
    /// </summary>
    private static void TryDeleteFolder(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* leave it */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Reporting
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the import did, in the order that matters: what was built, what was NOT carried, then
    /// every decision the translation made.
    ///
    /// <para><b>The unmapped list is the important line and is never suppressed.</b> A card's
    /// substrate junction, its flicker-noise coefficients and its level number are all real, all
    /// silently absent from the cell, and a user who is not told has no way to find out except from
    /// an answer that is wrong by an amount they cannot attribute.</para>
    /// </summary>
    private static IReadOnlyList<string> Report(
        ModelCardTranslation translation, SymbolKind kind, string cellName)
    {
        var binding = translation.Binding!;
        var lines = new List<string>
        {
            $"'{cellName}' built from .model {translation.Card.Name} "
            + $"{translation.Card.ModelType.Trim()} as a {ComponentTypeRegistry.Get(kind).DisplayName} "
            + $"with {binding.Parameters.Count} parameter(s) and "
            + $"{SymbolPortDefs.For(kind).Length} pin(s).",
        };

        if (binding.Unmapped.Count > 0)
            lines.Add(
                $"NOT carried — circuitRF's {ComponentTypeRegistry.Get(kind).DisplayName} has no "
                + $"parameter for these, and they are absent from the cell: "
                + string.Join(", ", binding.Unmapped) + ".");

        lines.AddRange(binding.Notes);
        return lines;
    }

    /// <summary>The annotation written onto the cell's own schematic.</summary>
    private static string Annotation(ModelCardTranslation translation, string cellName)
    {
        var binding = translation.Binding!;
        string text =
            $"{cellName} — imported from SPICE model card '{translation.Card.Name}' "
            + $"({translation.Card.ModelType.Trim()}), {binding.Parameters.Count} parameter(s) carried.";

        if (binding.Unmapped.Count > 0)
            text += $"\nNot carried: {string.Join(", ", binding.Unmapped)}.";

        foreach (string note in binding.Notes)
            text += "\n" + note;

        return text;
    }
}
