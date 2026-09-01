using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for importing a SPICE <c>.model</c> card as a cell.
///
/// <para>The three that matter are the ones a user would otherwise discover from a wrong answer:
/// the pins are electrically CONNECTED (not merely drawn near the device), every value lands in
/// BASE SI units (the registry's own rows are in pF and nH, so a card's farads written into one
/// would be off by a factor of 1e12 and would simulate), and a type circuitRF has no model for is
/// refused BY NAME rather than approximated onto the nearest thing that compiles.</para>
/// </summary>
public class ModelCardImportTests : IDisposable
{
    private readonly string _root;

    public ModelCardImportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-modelcard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string WriteCard(string fileName, string text)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, text);
        return path;
    }

    private static ModelCardTranslation One(string text)
    {
        var cards = SpiceNetlistReader.Read(text).ModelCards;
        Assert.Single(cards);
        return SpiceModelCardTranslation.Translate(cards[0]);
    }

    /// <summary>
    /// A parameter row's value in BASE SI units — its expression evaluated with its own unit token
    /// applied, which is exactly what the elaborator does with it. This is the form every assertion
    /// about an imported value is written in: the expression string alone cannot distinguish a
    /// correct import from one whose unit is still the registry's picofarads.
    /// </summary>
    private static double Si(string name, EditableComponent component)
    {
        var p = component.Parameters.Single(q => q.Name == name);
        // UnitNormalizer is the SAME boundary the elaborator crosses: the editor's unit tokens use
        // "Ω" and "µ" and the expression engine's table is keyed "Ohm" and "u". Skipping it here
        // would make this helper reject the very tokens the registry declares.
        string? unit = p.Unit.Length > 0 ? UnitNormalizer.ToEngineUnit(p.Unit) : null;
        return new Evaluator().Eval(p.Expression, new Scope("import"), unit).AsReal();
    }

    /// <summary>
    /// Compares against a base-SI value with a RELATIVE tolerance, because the quantities involved
    /// span twenty orders of magnitude — a fixed decimal count that is meaningful for a 75 V
    /// breakdown is meaningless for a 1e-14 A saturation current, and xUnit's decimal-place overload
    /// only reaches 15.
    /// </summary>
    private static void AssertSi(double expected, double actual)
        => Assert.Equal(expected, actual, Math.Abs(expected) * 1e-12);

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A file that is nothing but cards reads as cards. The reader is a full netlist reader and a
    /// SPICE deck's first line is conventionally a title, so this pins that a bare model file does
    /// not lose its first card to that convention.
    /// </summary>
    [Fact]
    public void AFileOfNothingButCards_ReadsEveryOneOfThem_IncludingTheFirstLine()
    {
        string path = WriteCard("kit.model", """
            .model DFIRST D (IS=1e-14 N=1.05)
            .model QSECOND NPN (IS=1e-16 BF=120)
            """);

        var scan = ModelCardCellBuilder.Scan(path);

        Assert.Null(scan.Error);
        Assert.Equal(2, scan.Translations.Count);
        Assert.Equal("DFIRST",  scan.Translations[0].Card.Name);
        Assert.Equal("QSECOND", scan.Translations[1].Card.Name);
    }

    // ── The unit trap ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The single most dangerous thing this feature could get wrong.</b> A schematic row carries a
    /// value AND a unit; the registry's Diode declares <c>Cj0</c> in PICOFARADS. A card states
    /// farads. Writing <c>CJO=2e-12</c> into a "pF" row is 2e-12 pF — a factor of 1e12 out, and it
    /// simulates perfectly.
    /// </summary>
    [Fact]
    public void EveryDimensionedParameter_LandsInBaseSiUnits_NotTheRegistrysConvenienceUnit()
    {
        var t = One(".model DMOD D (IS=2.5e-14 RS=0.8 CJO=2e-12 BV=75 VJ=0.7)");
        var schematic = ModelCardCellBuilder.BuildSchematic(t, "DMOD");

        var device = schematic.Components.Single(c => c.Symbol == SymbolKind.Diode);

        string UnitOf(string name) => device.Parameters.Single(p => p.Name == name).Unit;

        Assert.Equal("F", UnitOf("Cj0"));      // NOT "pF" — the registry default's unit
        Assert.Equal("A", UnitOf("Is"));
        Assert.Equal("Ω", UnitOf("Rs"));
        Assert.Equal("V", UnitOf("Bv"));

        // The gate that actually matters: the row's VALUE AND UNIT TOGETHER resolve to the SI
        // quantity the card stated. Comparing the expression string alone would pass just as
        // happily with the unit left at "pF", which is the whole failure this test exists for.
        AssertSi(2e-12, Si("Cj0", device));
        AssertSi(2.5e-14, Si("Is",  device));
        AssertSi(0.8, Si("Rs",  device));
        AssertSi(75.0, Si("Bv",  device));

        // Every unit written must be a member of that dimension's own closed option list, or the
        // parameter dialog's combo has nothing to select.
        foreach (var p in device.Parameters.Where(p => p.Unit.Length > 0))
            Assert.Contains(p.Unit, ComponentTypeRegistry.UnitOptions(p.Dimension));
    }

    /// <summary>
    /// The reader normalises the dialect's own suffixes before this layer sees them, so a card
    /// written the way real kits write it arrives as a plain literal rather than as "2p".
    /// </summary>
    [Fact]
    public void SpiceSuffixes_AreAlreadyNormalised_SoNoSecondScalingIsApplied()
    {
        var t = One(".model DMOD D (CJO=2p IS=10f RS=1.5)");
        var device = ModelCardCellBuilder.BuildSchematic(t, "DMOD").Components
            .Single(c => c.Symbol == SymbolKind.Diode);

        AssertSi(2e-12, Si("Cj0", device));
        AssertSi(10e-15, Si("Is",  device));
    }

    // ── The pins ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>"The pins are automatically connected" is the deliverable.</b> A cell whose pins merely sit
    /// near the device resolves to ports that are not on the device's nets — it places, it wires up,
    /// and it is an open circuit. This asserts the schematic's own connectivity pass, which is the
    /// same one the editor draws its connection dots from.
    /// </summary>
    [Theory]
    [InlineData(".model DMOD D (IS=1e-14)",                    SymbolKind.Diode,  2)]
    [InlineData(".model QMOD NPN (IS=1e-16 BF=100)",           SymbolKind.BjtNpn, 3)]
    [InlineData(".model QMOD PNP (IS=1e-16 BF=100)",           SymbolKind.BjtPnp, 3)]
    [InlineData(".model FMOD NMF (VTO=-2 BETA=0.02 B=0.3)",    SymbolKind.FetStatz,   3)]
    [InlineData(".model FMOD NMF (VTO=-2 BETA=0.02 ALPHA=2)",  SymbolKind.FetCurtice, 3)]
    [InlineData(".model RMOD RES (R=50 TC1=1e-3)",             SymbolKind.Resistor, 2)]
    public void EveryDeviceTerminal_AndEveryPin_ComesOutConnected(
        string card, SymbolKind expectedKind, int expectedPins)
    {
        var t = One(card);
        var schematic = ModelCardCellBuilder.BuildSchematic(t, "Part");

        var device = schematic.Components.Single(c => c.Symbol == expectedKind);
        var pins   = schematic.Components.Where(c => c.Symbol == SymbolKind.Pin).ToList();
        Assert.Equal(expectedPins, pins.Count);

        var (render, _) = schematic.BuildRenderModel();

        foreach (var comp in render.Components.Where(
                     c => c.Symbol == expectedKind || c.Symbol == SymbolKind.Pin))
            foreach (var port in comp.Ports)
                Assert.Equal(PortConnectionState.Connected, port.State);
    }

    /// <summary>
    /// Pin NUMBERS follow the device's own terminal order, so the cell's ports line up index-for-
    /// index with the symbol copied from the same component. Getting this wrong produces a cell
    /// whose symbol pins point at the wrong nets — which reads as correct.
    /// </summary>
    [Fact]
    public void PinNumbersAndNames_FollowTheDevicesOwnTerminalOrder()
    {
        var t = One(".model QMOD NPN (IS=1e-16 BF=100)");
        var schematic = ModelCardCellBuilder.BuildSchematic(t, "Q");

        var byNum = schematic.Components
            .Where(c => c.Symbol == SymbolKind.Pin)
            .ToDictionary(
                c => c.Parameters.Single(p => p.Name == "Num").Expression,
                c => c.Parameters.Single(p => p.Name == "Name").Expression);

        // SymbolPortDefs.For(BjtNpn) — [0] collector, [1] base, [2] emitter.
        Assert.Equal("c", byNum["1"]);
        Assert.Equal("b", byNum["2"]);
        Assert.Equal("e", byNum["3"]);
    }

    /// <summary>
    /// The cell's declared port count is what a placing schematic reads. It has to agree with both
    /// the pin count and the copied symbol's pin count, or the placed instance has ports the symbol
    /// cannot show.
    /// </summary>
    [Fact]
    public void TheCellsPortCount_AgreesWithBothThePinsAndTheCopiedSymbol()
    {
        var t = One(".model QMOD NPN (IS=1e-16)");
        string cellDir = ModelCardCellBuilder.Write(_root, "Q2N", t).CellDir;

        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));
        Assert.Equal(3, ccell.NumPorts);

        var symbol = SymbolPersistence.LoadFromFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), "Q2N.csym"));
        Assert.Equal(3, symbol.Pins.Count);
        Assert.Equal([0, 1, 2], symbol.Pins.Select(p => p.PortIndex).OrderBy(i => i));
    }

    // ── MESFET lead resistance ────────────────────────────────────────────────

    /// <summary>
    /// circuitRF's FET family has no RD/RS parameter, so a MESFET card's lead resistances go into
    /// the SCHEMATIC as the series resistors they physically are. Dropping them would leave an
    /// imported device with no access resistance and a gain that is visibly too high.
    /// </summary>
    [Fact]
    public void AMesfetCardsLeadResistances_BecomeRealSeriesResistors_StillFullyConnected()
    {
        var t = One(".model FMOD NMF (VTO=-2 BETA=0.02 B=0.3 RD=2.5 RS=1.8)");
        var schematic = ModelCardCellBuilder.BuildSchematic(t, "F");

        var resistors = schematic.Components.Where(c => c.Symbol == SymbolKind.Resistor).ToList();
        Assert.Equal(2, resistors.Count);
        Assert.Contains(resistors, r => Si("R", r) == 2.5);
        Assert.Contains(resistors, r => Si("R", r) == 1.8);

        // The gate lead has no resistor and the drain/source ones did not break the chain.
        var (render, _) = schematic.BuildRenderModel();
        foreach (var comp in render.Components.Where(c => c.Symbol != SymbolKind.Pin))
            foreach (var port in comp.Ports)
                Assert.Equal(PortConnectionState.Connected, port.State);
    }

    /// <summary>
    /// Which MESFET law a card states is decided from its PARAMETERS, never its LEVEL — the level
    /// numbering is not portable between dialects, so honouring it would make the choice depend on
    /// which simulator the file was written for, a fact the file does not record.
    /// </summary>
    [Fact]
    public void TheMesfetLaw_IsChosenFromB_AndAStatedLevelIsReportedAsNotHonoured()
    {
        var statz   = One(".model F1 NMF (LEVEL=1 VTO=-2 BETA=0.02 B=0.3)");
        var curtice = One(".model F2 NMF (LEVEL=2 VTO=-2 BETA=0.02 ALPHA=2)");

        // B present → Statz, DESPITE LEVEL=1; B absent → Curtice, DESPITE LEVEL=2.
        Assert.Equal("FET_Statz",   statz.Binding!.EngineReference);
        Assert.Equal("FET_Curtice", curtice.Binding!.EngineReference);

        Assert.Contains("LEVEL", statz.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(statz.Binding.Notes, n => n.Contains("Statz", StringComparison.Ordinal));
    }

    // ── Nothing vanishes ──────────────────────────────────────────────────────

    /// <summary>
    /// A card parameter circuitRF has no home for is REPORTED, not dropped. A substrate junction and
    /// a flicker-noise coefficient are both real, both silently absent from the built cell, and a
    /// user who is not told has no way to find out except from an answer that is wrong by an amount
    /// they cannot attribute.
    /// </summary>
    [Fact]
    public void ParametersWithNoCircuitRfHome_AreReported_NeverSilentlyDropped()
    {
        var t = One(".model QMOD NPN (IS=1e-16 BF=100 CJS=1e-13 KF=1e-16 AF=1 PTF=0)");

        Assert.Contains("CJS", t.Binding!.Unmapped, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("KF",  t.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AF",  t.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("PTF", t.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);

        // …and it reaches the user, both in the message and on the cell's own annotation.
        var result = ModelCardCellBuilder.Write(_root, "Q", t);
        Assert.Contains(result.Report, l => l.Contains("CJS", StringComparison.OrdinalIgnoreCase));

        string csch = File.ReadAllText(
            Path.Combine(CellFolder.SubFolderPath(result.CellDir, ViewType.Schematic), "Q.csch"));
        Assert.Contains("CJS", csch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>C2 and C4 are NOT aliases of ISE and ISC.</b> They are MULTIPLIERS of IS in old SPICE, so
    /// reading one as the other is off by roughly fourteen orders of magnitude on a card that looks
    /// entirely ordinary.
    /// </summary>
    [Fact]
    public void OldSpiceMultipliers_AreNeverReadAsTheCurrentsTheyScale()
    {
        var t = One(".model QMOD NPN (IS=1e-16 C2=1e3 C4=5)");

        Assert.DoesNotContain(t.Binding!.Parameters, p => p.Name is "Ise" or "Isc");
        Assert.Contains("C2", t.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("C4", t.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One quantity, several published spellings — and a target-first map is what stops the last one
    /// read from winning by file order. CJO/CJ0/CJ all mean the zero-bias junction capacitance.
    /// </summary>
    [Theory]
    [InlineData("CJO")]
    [InlineData("CJ0")]
    [InlineData("CJ")]
    public void EveryPublishedSpellingOfAQuantity_ReachesTheOneCircuitRfParameter(string spelling)
    {
        var t = One($".model DMOD D ({spelling}=3e-12)");
        var device = ModelCardCellBuilder.BuildSchematic(t, "D").Components
            .Single(c => c.Symbol == SymbolKind.Diode);
        AssertSi(3e-12, Si("Cj0", device));
    }

    // ── The JFET and p-channel MESFET families ────────────────────────────────

    /// <summary>
    /// An <c>NJF</c>/<c>PJF</c> card becomes the Shichman-Hodges JFET of the matching channel.
    /// <c>RD</c>/<c>RS</c> are carried as MODEL parameters here — circuitRF's JFET has them, so
    /// they belong in the device — which is the opposite of what happens to the identically-spelled
    /// parameters on a MESFET card, and is why the report says so.
    /// </summary>
    [Theory]
    [InlineData("NJF", "JFET_N", SymbolKind.JfetN, -2.0)]
    [InlineData("PJF", "JFET_P", SymbolKind.JfetP,  2.0)]
    public void AJfetCard_BecomesTheSquareLawJfetOfItsOwnChannel(
        string type, string reference, SymbolKind kind, double vto)
    {
        var t = One($".model JPART {type} (VTO={vto.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                  + "BETA=1.2e-3 LAMBDA=0.03 IS=2e-14 CGS=4e-12 CGD=1.5e-12 PB=0.9 RD=8 RS=6)");

        Assert.True(t.IsSupported);
        Assert.Equal(reference, t.Binding!.EngineReference);

        var schematic = ModelCardCellBuilder.BuildSchematic(t, "JPART");
        var device = schematic.Components.Single(c => c.Symbol == kind);

        AssertSi(vto,     Si("Vto",  device));
        AssertSi(1.2e-3,  Si("Beta", device));
        AssertSi(4e-12,   Si("Cgs",  device));      // NOT the registry's picofarads
        AssertSi(8.0,     Si("Rd",   device));

        // Model parameters, so no resistor is placed beside the device.
        Assert.DoesNotContain(schematic.Components, c => c.Symbol == SymbolKind.Resistor);
        Assert.Contains(t.Binding.Notes, n => n.Contains("MODEL parameters", StringComparison.Ordinal));
    }

    /// <summary>
    /// A JFET card carrying a higher published level's parameters is READ as the square law with
    /// those parameters named, not refused and not folded into <c>Lambda</c> — which is a different
    /// quantity, and putting one where the other belongs is the failure this whole layer avoids.
    /// </summary>
    [Fact]
    public void AJfetCardsHigherLevelParameters_AreNamedRatherThanFoldedIntoLambda()
    {
        var t = One(".model JPART NJF (VTO=-2 BETA=1.2e-3 B=0.5 ALPHA=1.2 VK=100)");

        Assert.True(t.IsSupported);
        Assert.DoesNotContain(t.Binding!.Parameters, p => p.Name == "Lambda");
        foreach (string p in new[] { "B", "ALPHA", "VK" })
            Assert.Contains(p, t.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(t.Binding.Notes, n => n.Contains("higher", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>A <c>PMF</c> card is no longer refused.</b> Both laws a MESFET card can be read as — the
    /// Curtice quadratic and Statz — have an unambiguous p-channel form, so a p-channel card gets
    /// the mirrored device with its <c>VTO</c> carried exactly as stated.
    /// </summary>
    [Theory]
    [InlineData("NMF", "",       "FET_Curtice",  SymbolKind.FetCurtice)]
    [InlineData("NMF", "B=0.3 ", "FET_Statz",    SymbolKind.FetStatz)]
    [InlineData("PMF", "",       "PFET_Curtice", SymbolKind.PFetCurtice)]
    [InlineData("PMF", "B=0.3 ", "PFET_Statz",   SymbolKind.PFetStatz)]
    public void AMesfetCard_PicksItsLawFromBAndItsChannelFromItsType(
        string type, string b, string reference, SymbolKind kind)
    {
        double vto = type == "PMF" ? 2.0 : -2.0;
        var t = One($".model QPART {type} ({b}VTO={vto.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                  + "BETA=0.02 ALPHA=2 CGS=4e-13)");

        Assert.True(t.IsSupported);
        Assert.Equal(reference, t.Binding!.EngineReference);

        var device = ModelCardCellBuilder.BuildSchematic(t, "QPART").Components
            .Single(c => c.Symbol == kind);
        AssertSi(vto, Si("Vto", device));

        if (type == "PMF")
            Assert.Contains(t.Binding.Notes, n => n.Contains("p-channel", StringComparison.OrdinalIgnoreCase));
    }

    // ── The power devices and the bead ────────────────────────────────────────

    /// <summary>
    /// <b>A <c>VDMOS</c> card's CHANNEL comes from a bare keyword, not from the type name</b>, which
    /// is unlike every other family here. That is why the reader keeps a card's bare words at all: a
    /// reader that kept only <c>key=value</c> pairs would import a p-channel part as an n-channel
    /// one, silently, with every number right.
    /// </summary>
    [Theory]
    [InlineData("",       "VDMOS_N", SymbolKind.VdmosN)]
    [InlineData("pchan ", "VDMOS_P", SymbolKind.VdmosP)]
    public void AVdmosCardsChannel_ComesFromItsBareKeyword(
        string flag, string reference, SymbolKind kind)
    {
        var t = One($".model MPART VDMOS ({flag}VTO=3.2 KP=12 RG=2 RD=0.03 CGDMAX=1.5e-9 "
                  + "CGDMIN=2.5e-11 CGS=1.8e-9 IS=5e-13 BV=60 TT=8e-8 CJO=9e-10)");

        Assert.True(t.IsSupported);
        Assert.Equal(reference, t.Binding!.EngineReference);

        var device = ModelCardCellBuilder.BuildSchematic(t, "MPART").Components
            .Single(c => c.Symbol == kind);
        AssertSi(3.2,     Si("Vto",    device));
        AssertSi(1.5e-9,  Si("Cgdmax", device));      // NOT the registry's picofarads
        AssertSi(2.5e-11, Si("Cgdmin", device));
        AssertSi(60.0,    Si("Bv",     device));
        AssertSi(2.0,     Si("Rg",     device));

        // The body diode's parameters are carried onto the device, because on this card they
        // describe the intrinsic diode rather than a substrate junction.
        AssertSi(5e-13,   Si("Is",     device));
        AssertSi(8e-8,    Si("Tt",     device));
    }

    /// <summary>
    /// A negative threshold with no channel keyword is what a p-channel part looks like — and also
    /// what a depletion-mode n-channel part looks like. Nothing on the card separates the two, so it
    /// is read as n-channel (which is what the absence of a keyword means) and the ambiguity is
    /// REPORTED rather than guessed at.
    /// </summary>
    [Fact]
    public void ANegativeThresholdWithNoChannelKeyword_IsReportedRatherThanGuessedAt()
    {
        var t = One(".model MPART VDMOS (VTO=-3.2 KP=12)");
        Assert.Equal("VDMOS_N", t.Binding!.EngineReference);
        Assert.Contains(t.Binding.Notes,
            n => n.Contains("NEGATIVE threshold", StringComparison.OrdinalIgnoreCase));

        // …and a card that says pchan carries no such note, because there is nothing ambiguous.
        var p = One(".model MPART VDMOS (pchan VTO=-3.2 KP=12)");
        Assert.DoesNotContain(p.Binding!.Notes,
            n => n.Contains("NEGATIVE threshold", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A <c>BEAD</c> card becomes the four-element bead. <b>One that states nothing but a DC
    /// resistance is refused</b>, for the same reason a resistor card stating only a sheet
    /// resistance is: what would be built is a few milliohms of resistor, which is not a bead and
    /// simulates perfectly.
    /// </summary>
    [Fact]
    public void ABeadCard_BecomesTheFourElementBead_UnlessItDescribesNoFerrite()
    {
        var t = One(".model FB1 BEAD (RDC=0.05 L=2.5e-7 RP=600 CP=8e-13)");
        Assert.True(t.IsSupported);
        Assert.Equal("Bead", t.Binding!.EngineReference);

        var device = ModelCardCellBuilder.BuildSchematic(t, "FB1").Components
            .Single(c => c.Symbol == SymbolKind.Bead);
        AssertSi(0.05,   Si("Rdc", device));
        AssertSi(2.5e-7, Si("L",   device));          // NOT the registry's microhenries
        AssertSi(600.0,  Si("Rp",  device));
        AssertSi(8e-13,  Si("Cp",  device));

        // Saturation cannot be modelled by a linear element and the report says so, because a bead
        // chosen from a small-signal impedance curve can behave quite differently in the rail it was
        // chosen for.
        Assert.Contains(t.Binding.Notes, n => n.Contains("SATURATION", StringComparison.Ordinal));

        var bare = One(".model FB1 BEAD (RDC=0.05)");
        Assert.False(bare.IsSupported);
        Assert.Contains("ferrite", bare.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A bead card with no parallel loss is BUILT, with a note — that is a bead whose impedance
    /// rises for ever and never peaks, which is an under-specified card read honestly rather than a
    /// wrong one. The difference from the refusal above is that something was said about the
    /// ferrite.
    /// </summary>
    [Fact]
    public void ABeadCardWithNoParallelLoss_IsBuiltWithANoteRatherThanRefused()
    {
        var t = One(".model FB1 BEAD (RDC=0.05 L=2.5e-7)");
        Assert.True(t.IsSupported);
        Assert.Contains(t.Binding!.Notes, n => n.Contains("no parallel loss", StringComparison.OrdinalIgnoreCase));
    }

    // ── The MOS family ────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>NMOS</c>/<c>PMOS</c> card becomes the level-1 MOS transistor of the matching channel,
    /// with its geometry and its process parameters carried in BASE SI units. <c>VTO</c> is taken
    /// exactly as the card states it — negative for an ordinary p-channel part — because the model
    /// applies the channel sign itself.
    /// </summary>
    [Theory]
    [InlineData("NMOS", "MOS1_N", SymbolKind.Mos1N,  0.7)]
    [InlineData("PMOS", "MOS1_P", SymbolKind.Mos1P, -0.7)]
    public void AMosCard_BecomesTheLevel1TransistorOfItsOwnChannel(
        string type, string reference, SymbolKind kind, double vto)
    {
        var t = One($".model MPART {type} (VTO={vto.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                  + "KP=8e-5 GAMMA=0.55 PHI=0.7 LAMBDA=0.02 TOX=2e-8 W=2e-5 L=1e-6 CGSO=3e-10 RD=12)");

        Assert.True(t.IsSupported);
        Assert.Equal(reference, t.Binding!.EngineReference);

        var device = ModelCardCellBuilder.BuildSchematic(t, "MPART").Components
            .Single(c => c.Symbol == kind);

        AssertSi(vto,   Si("Vto",   device));
        AssertSi(8e-5,  Si("Kp",    device));
        AssertSi(0.55,  Si("Gamma", device));
        AssertSi(0.7,   Si("Phi",   device));
        // Lengths are the trap this whole file exists for: the registry declares W, L and Tox in
        // micrometres and nanometres, and a card states metres.
        AssertSi(2e-8,  Si("Tox",   device));
        AssertSi(2e-5,  Si("W",     device));
        AssertSi(1e-6,  Si("L",     device));
        AssertSi(12.0,  Si("Rd",    device));
    }

    /// <summary>
    /// <b>A MOS card's <c>RD</c>/<c>RS</c> must NOT become placed series resistors.</b> They are
    /// spelled exactly as a MESFET card spells its own lead resistances — which DO become resistors,
    /// because circuitRF's MESFET has no parameter for them — but the MOS transistor carries them as
    /// model parameters on an internal node the elaborator mints. Placing them a second time would
    /// put the resistance in the device AND beside it, and the schematic would look entirely
    /// ordinary.
    /// </summary>
    [Fact]
    public void AMosCardsLeadResistances_StayInTheDevice_AndAreNotAlsoPlacedBesideIt()
    {
        var t = One(".model MPART NMOS (VTO=0.7 KP=8e-5 RD=12 RS=9)");
        var schematic = ModelCardCellBuilder.BuildSchematic(t, "MPART");

        Assert.DoesNotContain(schematic.Components, c => c.Symbol == SymbolKind.Resistor);

        var device = schematic.Components.Single(c => c.Symbol == SymbolKind.Mos1N);
        AssertSi(12.0, Si("Rd", device));
        AssertSi(9.0,  Si("Rs", device));
    }

    /// <summary>
    /// The cell has FOUR pins, because circuitRF's MOS transistor has four terminals. A cell built
    /// with three would leave the substrate floating in every schematic it is placed into — which
    /// solves, and is a different circuit.
    /// </summary>
    [Fact]
    public void TheBuiltCell_HasAPinForTheBulk()
    {
        var t = One(".model MPART NMOS (VTO=0.7 KP=8e-5 GAMMA=0.5 PHI=0.65)");
        var schematic = ModelCardCellBuilder.BuildSchematic(t, "MPART");

        var pinNames = schematic.Components
            .Where(c => c.Symbol == SymbolKind.Pin)
            .Select(c => c.Parameters.Single(p => p.Name == "Name").Expression)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["b", "d", "g", "s"], pinNames);
        Assert.Contains(t.Binding!.Notes, n => n.Contains("bulk", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>A MOS card's <c>LEVEL</c> IS read</b> — unlike a MESFET card's, which is not, and the
    /// difference is not an inconsistency. A MESFET card's level selects between laws that different
    /// dialects number differently; a MOS card's numbering for the classical levels is the one thing
    /// about it that is portable, because 1, 2 and 3 mean the same three published models wherever
    /// they appear.
    /// </summary>
    [Theory]
    [InlineData("",          "MOS1_N", SymbolKind.Mos1N)]
    [InlineData("LEVEL=1 ",  "MOS1_N", SymbolKind.Mos1N)]
    [InlineData("LEVEL=3 ",  "MOS3_N", SymbolKind.Mos3N)]
    public void AMosCardsLevel_SelectsTheLaw(string level, string reference, SymbolKind kind)
    {
        var t = One($".model MPART NMOS ({level}VTO=0.7 KP=8e-5 TOX=2e-8 ETA=0.05 THETA=0.08 VMAX=2e5)");

        Assert.True(t.IsSupported);
        Assert.Equal(reference, t.Binding!.EngineReference);
        Assert.NotNull(ModelCardCellBuilder.BuildSchematic(t, "MPART").Components
            .SingleOrDefault(c => c.Symbol == kind));
    }

    /// <summary>
    /// <b>A short-channel parameter must not be carried onto a level-1 device</b>, which has no home
    /// for it. Landing it there would look exactly like it had been honoured — the parameter row
    /// would be on the transistor, with the card's value in it, read by nothing. Reported as not
    /// carried is the only honest place for it.
    /// </summary>
    [Fact]
    public void ShortChannelParameters_ReachLevel3_AndAreReportedAsDroppedOnLevel1()
    {
        var l3 = One(".model MPART NMOS (LEVEL=3 VTO=0.7 KP=8e-5 TOX=2e-8 ETA=0.05 THETA=0.08 VMAX=2e5 XJ=1.5e-7)");
        foreach (string p in new[] { "Eta", "Theta", "Vmax", "Xj" })
            Assert.Contains(l3.Binding!.Parameters, q => q.Name == p);
        Assert.DoesNotContain("ETA", l3.Binding!.Unmapped, StringComparer.OrdinalIgnoreCase);

        var l1 = One(".model MPART NMOS (LEVEL=1 VTO=0.7 KP=8e-5 TOX=2e-8 ETA=0.05 THETA=0.08 VMAX=2e5 XJ=1.5e-7)");
        foreach (string p in new[] { "Eta", "Theta", "Vmax", "Xj" })
            Assert.DoesNotContain(l1.Binding!.Parameters, q => q.Name == p);
        foreach (string p in new[] { "ETA", "THETA", "VMAX", "XJ" })
            Assert.Contains(p, l1.Binding!.Unmapped, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>And the rule runs the OTHER way too: <c>LAMBDA</c> must not be carried onto a level-3
    /// device.</b> Level 3 computes its output slope from a real shortening of the channel instead
    /// of fitting one, which is the whole difference between the two laws — so
    /// <c>MosfetLevel3Model</c> has no such constructor parameter and the level-3 palette tile
    /// leaves the row off as well.
    ///
    /// <para>This is the exact mirror of the short-channel case above and it failed while that one
    /// passed: the level-1 branch stripped its six, and nothing stripped level 3's one. A card
    /// stating LAMBDA imported with a Lambda row on the transistor, carrying the card's own value,
    /// read by nothing — the failure this pair of tests exists to make impossible in either
    /// direction.</para>
    /// </summary>
    [Fact]
    public void Lambda_ReachesLevel1_AndIsReportedAsDroppedOnLevel3()
    {
        var l1 = One(".model MPART NMOS (LEVEL=1 VTO=0.7 KP=8e-5 TOX=2e-8 LAMBDA=0.03)");
        Assert.Contains(l1.Binding!.Parameters, q => q.Name == "Lambda");
        Assert.DoesNotContain("LAMBDA", l1.Binding!.Unmapped, StringComparer.OrdinalIgnoreCase);

        var l3 = One(".model MPART NMOS (LEVEL=3 VTO=0.7 KP=8e-5 TOX=2e-8 LAMBDA=0.03 ETA=0.05)");
        Assert.DoesNotContain(l3.Binding!.Parameters, q => q.Name == "Lambda");
        Assert.Contains("LAMBDA", l3.Binding!.Unmapped, StringComparer.OrdinalIgnoreCase);

        // And it is gone from the built cell, not merely from the binding — the row on the device is
        // what a user would have read as honoured.
        Assert.DoesNotContain(
            ModelCardCellBuilder.BuildSchematic(l3, "MPART").Components
                .Single(c => c.Symbol == SymbolKind.Mos3N).Parameters,
            q => q.Name == "Lambda");
    }

    /// <summary>
    /// A card stating a level between the two circuitRF has is READ as level 1 with a note, because
    /// every parameter the classical levels share means the same thing in all of them. A card at
    /// level 4 or above is REFUSED, because those are the compact-model families and their
    /// parameters are a different vocabulary — almost nothing would be carried, and what came out
    /// would be this transistor wearing default numbers under the card's name.
    /// </summary>
    [Fact]
    public void ALevelCircuitRfDoesNotHave_IsReadAsLevel1_UntilTheVocabularyChanges()
    {
        var l2 = One(".model MPART NMOS (LEVEL=2 VTO=0.7 KP=8e-5 TOX=2e-8 UCRIT=1e4 UEXP=0.15)");
        Assert.True(l2.IsSupported);
        Assert.Equal("MOS1_N", l2.Binding!.EngineReference);
        Assert.Contains(l2.Binding.Notes, n => n.Contains("LEVEL=2", StringComparison.Ordinal));
        foreach (string p in new[] { "LEVEL", "UCRIT", "UEXP" })
            Assert.Contains(p, l2.Binding.Unmapped, StringComparer.OrdinalIgnoreCase);

        var l49 = One(".model MPART NMOS (LEVEL=49 VTH0=0.42 TOXE=2e-9 U0=0.03)");
        Assert.False(l49.IsSupported);
        Assert.Contains("LEVEL=49", l49.Refusal!, StringComparison.Ordinal);
        Assert.Contains("VerilogA", l49.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A card with no <c>TOX</c> has no oxide capacitance and therefore no intrinsic gate charge.
    /// That is the published rule and nothing is invented to fill it — but it is worth saying out
    /// loud, because the imported device then has almost no gate capacitance and that is the kind of
    /// thing found much later by wondering where the gain went.
    /// </summary>
    [Fact]
    public void AMosCardWithNoOxideThickness_SaysThatTheGateChargeIsAbsent()
    {
        var noTox = One(".model MPART NMOS (VTO=0.7 KP=8e-5)");
        Assert.Contains(noTox.Binding!.Notes,
            n => n.Contains("TOX", StringComparison.Ordinal) && n.Contains("overlap", StringComparison.OrdinalIgnoreCase));

        var withTox = One(".model MPART NMOS (VTO=0.7 KP=8e-5 TOX=2e-8)");
        Assert.DoesNotContain(withTox.Binding!.Notes, n => n.Contains("states no TOX", StringComparison.Ordinal));
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A type circuitRF has no model for is refused BY NAME.</b> The nearest-native temptation is
    /// real — a ferrite bead looks like a parallel RLC, an IGBT looks like a bipolar with a gate —
    /// and every one of those produces a cell that simulates and is quantitatively wrong with
    /// nothing anywhere reporting it.
    ///
    /// <para>The list shrinks as engine models land, and that is the point: each type leaves this
    /// theory only when there is a real model behind it, never because the refusal was inconvenient.
    /// <c>NJF</c>/<c>PJF</c>, <c>PMF</c>, <c>NMOS</c>/<c>PMOS</c>, <c>VDMOS</c> and <c>BEAD</c> were
    /// all here and are now covered by their own tests above.</para>
    ///
    /// <para><b><c>NIGBT</c>/<c>PIGBT</c> are the interesting case: circuitRF HAS an IGBT and the
    /// card is still refused.</b> That is a different refusal from "no model exists" and the text
    /// has to say so — the card's parameters describe the silicon under the ambipolar transport
    /// model and circuitRF's is an equivalent-circuit model parameterised by data-sheet quantities,
    /// and neither set can be derived from the other by renaming.</para>
    /// </summary>
    [Theory]
    [InlineData("NIGBT", "IGBT")]
    [InlineData("PIGBT", "IGBT")]
    public void ATypeWithNoNativeModel_IsRefusedAndTheRefusalNamesIt(string type, string expectedPhrase)
    {
        var t = One($".model PART {type} (VTO=-1 KP=1e-4)");

        Assert.False(t.IsSupported);
        Assert.NotNull(t.Refusal);
        Assert.Contains(type, t.Refusal!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedPhrase, t.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PART", t.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A passive card that states no value has nothing to build from, and inventing one would give a
    /// component that simulates. This is the same refusal <c>SpicePassiveModelBinding</c> already
    /// gives on an instance, for the same reason.
    /// </summary>
    [Theory]
    [InlineData(".model RMOD RES (RSH=50 TC1=1e-3)")]
    [InlineData(".model CMOD CAP (CJ=1e-4 CJSW=1e-10)")]
    [InlineData(".model LMOD IND (TC1=1e-3)")]
    public void APassiveCardStatingNoValue_IsRefusedRatherThanGivenOne(string card)
    {
        var t = One(card);
        Assert.False(t.IsSupported);
        Assert.Contains("nothing to build", t.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Importing the same card twice must prompt, never overwrite a cell someone has since edited.
    /// </summary>
    [Fact]
    public void AnExistingCell_IsRefusedOutright_AndTheFolderIsLeftExactlyAsItWas()
    {
        var t = One(".model DMOD D (IS=1e-14)");
        ModelCardCellBuilder.Write(_root, "D1", t);

        string marker = Path.Combine(_root, "D1", "edited-by-hand.txt");
        File.WriteAllText(marker, "mine");

        Assert.Throws<IOException>(() => ModelCardCellBuilder.Write(_root, "D1", t));
        Assert.True(File.Exists(marker));
    }

    /// <summary>
    /// The symbol is a private COPY of the palette's artwork, not a reference to it. Copying is the
    /// whole point — the user edits the cell's symbol afterwards — and <c>BuiltInSymbols</c> hands
    /// back a static cached instance shared by every renderer in the process, so a shallow copy
    /// would let the cell's editor mutate the palette's own glyph.
    /// </summary>
    [Fact]
    public void TheCopiedSymbol_IsADeepCopy_NotTheProcessWideCachedInstance()
    {
        var cached = BuiltInSymbols.Primitives(SymbolKind.Diode);
        var copy   = ModelCardCellBuilder.BuildSymbol(SymbolKind.Diode);

        Assert.Equal(cached.Primitives.Count, copy.Primitives.Count);
        Assert.Equal(cached.Pins.Count,       copy.Pins.Count);

        for (int i = 0; i < copy.Primitives.Count; i++)
            Assert.NotSame(cached.Primitives[i], copy.Primitives[i]);
        for (int i = 0; i < copy.Pins.Count; i++)
            Assert.NotSame(cached.Pins[i], copy.Pins[i]);
    }

    // ── The registry gap the import exposed ───────────────────────────────────

    /// <summary>
    /// <b>Every diode parameter the ENGINE reads has a schematic row.</b> The two lists had drifted:
    /// <c>CreateDiodeModel</c> has always read Xti, Eg, Tnom, Area, Isr, Nr and Nbv, and the
    /// registry declared none of them — so they were live, invisible, and a model card's XTI had
    /// nowhere to land (owner, 2026-09-01). This is what keeps them in step.
    /// </summary>
    [Fact]
    public void EveryDiodeParameterTheEngineReads_HasASchematicRow()
    {
        var declared = ComponentTypeRegistry.DefaultParameters(SymbolKind.Diode, 0)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Exactly the names CreateDiodeModel's P(...) calls read, less Gmin — which the DC engine
        // supplies per node and no user sets.
        string[] engineReads =
        [
            "Is", "N", "Cj0", "Vj", "M", "Fc", "Bv", "Ibv", "Tt", "Temp", "Rs",
            "Isr", "Nr", "Nbv", "Area", "Tnom", "Xti", "Eg",
        ];

        Assert.Empty(engineReads.Where(n => !declared.Contains(n)));
    }
    // ── UI wiring ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The two File-menu surfaces are hand-maintained and must not drift.</b> macOS renders the
    /// <c>NativeMenu</c> and every other platform the in-window <c>Menu</c>; a command added to one
    /// is simply absent on the other, with nothing failing anywhere. This repo has been bitten by
    /// exactly that before, which is why <c>FileMenuRestructureTests</c> exists — this is the same
    /// check narrowed to the command this feature adds.
    /// </summary>
    [Fact]
    public void BothFileMenuSurfaces_OfferImportModelCard()
    {
        string axaml = File.ReadAllText(RepoFile("src", "Ui", "Views", "WorkspaceWindow.axaml"));

        Assert.Equal(2, CountOf(axaml, "ImportModelCardCommand"));
        Assert.Contains("<NativeMenuItem Header=\"Model Card…\"", axaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"_Model Card…\"", axaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The project tree's own door: the item binds the command AND is gated on the extension test,
    /// so it appears on a bookmarked <c>.model</c> and on nothing else.
    /// </summary>
    [Fact]
    public void TheProjectTreeMenuItem_BindsTheCommandAndIsGatedOnTheExtension()
    {
        string axaml = File.ReadAllText(
            RepoFile("src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml"));

        Assert.Contains("CreateCellFromModelCardCommand", axaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsModelCardFile}\"", axaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The extension test the tree item is gated on. Narrow on purpose: it decides a menu item's
    /// visibility with nothing having read the file, so a wide net would put a dead item on most of
    /// a workspace. The File ▸ Import picker is where the wider filter belongs.
    /// </summary>
    [Theory]
    [InlineData("qnpna.model",   true)]
    [InlineData("QNPNA.MODEL",   true)]
    [InlineData("diodes.mod",    true)]
    [InlineData("kit.lib",       false)]
    [InlineData("board.kicad_pcb", false)]
    [InlineData("notes.txt",     false)]
    public void TheTreeItemsExtensionTest_IsNarrow(string fileName, bool expected)
        => Assert.Equal(expected, ModelCardCellBuilder.IsModelCardFile(fileName));

    /// <summary>
    /// Both doors run the SAME method, so they cannot disagree about what an import produces. The
    /// tree action is a one-line forward onto the path-taking implementation the File menu also
    /// calls — a second composition is exactly how two entry points start behaving differently.
    /// </summary>
    [Fact]
    public void BothImportDoors_RunTheOneImplementation()
    {
        string src = File.ReadAllText(RepoFile("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        Assert.Contains(
            "public Task CreateCellFromModelCardAsync(ProjectTreeNodeViewModel node)\n"
            + "        => CreateCellFromModelCardFromPathAsync(node.AbsolutePath);",
            src.Replace("\r\n", "\n"), StringComparison.Ordinal);

        // …and the File-menu command forwards to the same place.
        int at = src.IndexOf("private async Task ImportModelCard(", StringComparison.Ordinal);
        Assert.True(at > 0, "ImportModelCard command not found");
        Assert.Contains("CreateCellFromModelCardFromPathAsync", src[at..], StringComparison.Ordinal);
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
    // ── The end-to-end claim ──────────────────────────────────────────────────

    /// <summary>
    /// <b>"…so the user can immediately use the cell in another circuitRF schematic."</b> Everything
    /// above tests a piece; this tests the promise. An imported cell is placed in an ordinary
    /// testbench, wired to three nets, and EXTRACTED — and what comes out has to be the netlist a
    /// hand-built transistor would have produced: one BJT_NPN carrying the card's numbers, its three
    /// terminals on the three nets the placing schematic named.
    ///
    /// <para>Extraction is where an unconnected pin, a wrong port order or a cell that declares the
    /// wrong port count stops being invisible — every one of those produces a cell that PLACES
    /// perfectly and then netlists to something else.</para>
    /// </summary>
    [Fact]
    public void AnImportedCell_PlacesInAnotherSchematicAndExtractsAsTheDeviceItCameFrom()
    {
        var t = One(".model QNPNA NPN (IS=6.734f BF=416.4 VAF=74.03 RC=1 CJC=3.638p TF=301.2p)");
        ModelCardCellBuilder.Write(_root, "QNPNA", t);

        // An ordinary testbench beside the cell, placing it by relative CellRef — exactly what
        // dragging the cell out of the project tree produces.
        string tbDir = Path.Combine(_root, "TB", "schematic");
        Directory.CreateDirectory(tbDir);

        var tb = new SchematicEditModel { SchematicDirectory = tbDir };
        var instance = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = Path.GetRelativePath(tbDir, Path.Combine(_root, "QNPNA")),
            X = 0, Y = 0,
        };
        tb.Components.Add(instance);

        var extraction = NetExtractor.Extract(tb, "tb", new DiskCells());

        Assert.Empty(extraction.Conflicts);
        Assert.Equal("X1", Assert.Single(extraction.TestBench.Instances).InstanceName);

        // The cell DEFINITION the extractor built from the imported schematic.
        var cell = Assert.Single(extraction.Library.Cells);
        Assert.Equal("QNPNA", cell.Name);

        // Three ports, named for the transistor's own terminals and in its own order — the
        // elaborator binds the placing instance's nets to these POSITIONALLY, so an order that
        // disagrees with the symbol's pins is a transistor wired collector-to-base with nothing
        // reporting it.
        Assert.Equal(["c", "b", "e"], cell.Ports);

        // …and inside it, the device itself, carrying the card's numbers.
        var device = Assert.Single(cell.Instances);
        Assert.Equal("BJT_NPN", device.Reference);
        Assert.Equal("416.4",   Value(device, "Bf"));
        Assert.Equal("74.03",   Value(device, "Vaf"));

        // Every one of the device's three terminals is bound to a DISTINCT net, which is the
        // electrical form of "the pins are connected": a pin left floating collapses two of these
        // onto one net or leaves one unbound, and the cell still places perfectly.
        Assert.Equal(3, device.NetBindings.Count);
        Assert.Equal(3, device.NetBindings.Distinct().Count());

        static string? Value(CircuitRF.Core.Design.Instance i, string name) =>
            i.Overrides.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?.Expression;
    }

    /// <summary>
    /// The disk-backed resolver the extractor needs, composed exactly as
    /// <c>WorkspaceViewModel.Resolve</c> composes it — same <c>HierarchyResolver</c>, same
    /// <c>.ccell</c> read. The production one is a view model that needs a live Avalonia
    /// application, which these tests deliberately never build.
    /// </summary>
    private sealed class DiskCells : ICellResolver
    {
        public CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containing)
        {
            if (HierarchyResolver.ResolvePrimaryPath(cellInstance, containing) is not { } primary)
                return null;

            var (model, _, _) = SchematicPersistence.LoadFromFile(primary);
            model.SchematicDirectory = Path.GetDirectoryName(primary);

            string cellDir = Path.GetDirectoryName(Path.GetDirectoryName(primary))!;
            return new CellResolution(Path.GetFileName(cellDir), model, []);
        }
    }
}
