using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The <c>.csmith</c> document (brief-smith-1-document.md §4). <b>One test per CLAIM, not one per
/// field</b> — a field is checked by the round trip, and a rule is checked by the case that breaks it.
///
/// <para>The name is <c>SmithDocumentTests</c> although neither type it exercises is called
/// <c>SmithDocument</c>: R-smith1-1a puts that name deliberately out of use, because it is the
/// obvious name for BOTH the model (<c>SmithDesign</c>) and brief 4's Dock document
/// (<c>SmithChartDocument</c>). This file tests the `.csmith` document, which is what it is
/// about.</para>
/// </summary>
public sealed class SmithDocumentTests
{
    // ── 1. round trip ────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Every kind, both placements, and every optional section at once.</b> The point of one big
    /// design rather than eleven small ones is that the failure mode this catches is a field wired
    /// on the way out and forgotten on the way in — and that is per-FIELD, so the design has to
    /// carry a non-default value for every field there is.
    /// </summary>
    [Fact]
    public void ADesignWithEveryKind_SurvivesSerializeAndDeserializeUnchanged()
    {
        var before = FullDesign();

        var after = SmithDesignIo.Deserialize(SmithDesignIo.Serialize(before));

        AssertSame(before, after);
    }

    // ── 2. base SI on disk (R-smith1-5) ──────────────────────────────────────

    /// <summary>
    /// <b>The raw text, not the round trip.</b> A round trip passes just as happily against a format
    /// that stored picohenries, because the same wrong scale is applied in both directions — which
    /// is exactly how the 2 Hz sweep in <c>src/Engine/RESOLVED.md</c> got as far as it did. So this
    /// reads the JSON as a STRING and asserts the literal tokens.
    ///
    /// <para>The one named exception is the electrical length, in DEGREES, in a field whose name
    /// says so. It is asserted here too: a document where the exception silently became radians is
    /// the failure this pairing exists to make visible.</para>
    /// </summary>
    [Fact]
    public void OnePicohenryOnePicofaradAndNinetyDegrees_AreWrittenInBaseSiWithTheOneNamedException()
    {
        var d = MinimalDesign();
        d.Elements.Add(new SmithElement
        {
            Kind      = SmithElementKind.Srlc,
            Placement = SmithPlacement.Series,
            Name      = "X1",
            Values    = new SmithElementValues { LHenry = 1e-12, CFarad = 1e-12 },
        });
        d.Elements.Add(new SmithElement
        {
            Kind      = SmithElementKind.Tline,
            Placement = SmithPlacement.Series,
            Name      = "TL1",
            Values    = new SmithElementValues
            {
                Z0Ohm                = 50,
                ElectricalLengthDeg  = 90,
                ReferenceFrequencyHz = 2e9,
            },
        });

        string json = SmithDesignIo.Serialize(d);

        Assert.Contains("\"LHenry\": 1E-12", json, StringComparison.Ordinal);
        Assert.Contains("\"CFarad\": 1E-12", json, StringComparison.Ordinal);
        Assert.Contains("\"ReferenceFrequencyHz\": 2000000000", json, StringComparison.Ordinal);

        // Degrees, and the field's own name is what makes that legible on disk.
        Assert.Contains("\"ElectricalLengthDeg\": 90", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1.5707", json, StringComparison.Ordinal);   // π/2, had it gone radian
    }

    // ── 3. a newer FormatVersion ─────────────────────────────────────────────

    /// <summary>
    /// <b>Refused, not half-read</b>, and the sentence names the version. Reading it anyway would
    /// silently drop whatever the newer circuitRF added, and the user would find out by saving it
    /// back over the original.
    /// </summary>
    [Fact]
    public void AFileFromANewerCircuitRf_IsRefusedBySentenceNamingTheVersion()
    {
        string json = SmithDesignIo.Serialize(MinimalDesign())
                                   .Replace("\"FormatVersion\": 1", "\"FormatVersion\": 2",
                                            StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidDataException>(() => SmithDesignIo.Deserialize(json));

        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".csmith", ex.Message, StringComparison.Ordinal);
    }

    // ── 4. every Refusal() rule (R-smith1-3) ─────────────────────────────────

    /// <summary>
    /// <b>One test, a table of cases.</b> Each row breaks exactly one rule and names the token the
    /// refusal has to contain — because a refusal whose text does not identify the offending object
    /// is not a refusal anyone can act on (overview §4.2).
    /// </summary>
    /// <remarks>
    /// <b>The frequency rows name the spelling the USER typed, and they used to name "2E+09"</b>
    /// (R-smith11-4). <c>SmithDesign.Fmt</c> was <c>"G6"</c>, which switches to exponential the
    /// moment the decimal exponent reaches the precision — so every frequency in every refusal this
    /// tool raises came out in scientific notation and in bare hertz, against a field the user had
    /// typed <c>2 GHz</c> into. Naming an object the user cannot match to what they typed is the
    /// half of "a refusal names the thing that answers it" that had been missed, and these rows are
    /// where it was pinned.
    /// </remarks>
    [Theory]
    // the generator table: non-empty, sorted, unique frequencies
    [InlineData("generator-empty",      "generator table")]
    [InlineData("generator-duplicate",  "2 GHz")]
    [InlineData("generator-unsorted",   "1 GHz")]
    // the design frequency against the table's span
    [InlineData("design-freq-outside",  "5 GHz")]
    // elements
    [InlineData("element-unnamed",      "no name")]
    [InlineData("element-duplicate",    "'C1'")]
    // …and two names that differ only in case are one name, because the two surfaces that MAKE
    // names — SmithElementFactory.NextName and the strip's rename field — both already say so, and
    // brief 7 copies these out as schematic instance names.
    [InlineData("element-duplicate-case", "'c1'")]
    [InlineData("shunt-s2p",            "'S1'")]
    [InlineData("fileref-missing",      "'S1'")]
    [InlineData("fileref-unexpected",   "'C1'")]
    [InlineData("negative-inductance",  "'L1'")]
    [InlineData("tline-zero-zed",       "'TL1'")]
    [InlineData("tline-negative-length","'TL1'")]
    [InlineData("tline-no-fref",        "'TL1'")]
    // the two document-wide settings
    [InlineData("sweep-backwards",      "band")]
    [InlineData("sweep-one-point",      "1 point")]
    [InlineData("sweep-too-many-points","1001")]
    [InlineData("q-negative",           "Q")]
    public void EachWellFormednessRule_FiresAndNamesItsObject(string which, string mustName)
    {
        var d = Broken(which);

        string? refusal = d.Refusal();

        Assert.NotNull(refusal);
        Assert.Contains(mustName, refusal, StringComparison.Ordinal);

        // And Serialize REFUSES TO WRITE it (§7): a document that cannot be read back is a document
        // that was never written, and its only symptom would be a file that refuses to open later.
        var ex = Assert.Throws<InvalidDataException>(() => SmithDesignIo.Serialize(d));
        Assert.Equal(refusal, ex.Message);
    }

    /// <summary>The same table's control: a design that breaks nothing is refused by nothing. A
    /// <c>Refusal()</c> that always fired would pass every row above.</summary>
    [Fact]
    public void AWellFormedDesign_IsRefusedByNothing() => Assert.Null(FullDesign().Refusal());

    // ── 5. the clipboard marker (R-smith1-6) ─────────────────────────────────

    /// <summary>
    /// <b>False for absolutely everything that is not this.</b> The system clipboard is one shared
    /// channel, and every payload below is JSON-shaped enough that a permissive reader would get
    /// PART of it and report success — leaving a document half-replaced by something unrelated, with
    /// nothing anywhere saying so.
    /// </summary>
    [Theory]
    [InlineData("{\"marker\":\"circuitrf/railrf-clipboard-v1\",\"document\":{\"FormatVersion\":1}}")]
    [InlineData("{\"marker\":\"circuitrf/layout-fragment-v1\",\"shapes\":[]}")]
    [InlineData("{\"Tabs\":[{\"Name\":\"Tab 1\",\"Plots\":[]}]}")]           // a Data Display config
    [InlineData("{\"FormatVersion\":1,\"Chart\":{\"Z0Ohm\":50}}")]           // a BARE .csmith file
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    public void TryDeserialize_IsFalseForEverythingThatIsNotASmithClipboardPayload(string text)
    {
        Assert.False(SmithClipboard.TryDeserialize(text, out var design));
        Assert.Null(design);
    }

    /// <summary>And a truncated one of its OWN — the case a marker check alone would let through if
    /// the parse were not guarded too.</summary>
    [Fact]
    public void TryDeserialize_IsFalseForATruncatedPayloadOfItsOwn()
    {
        string full = SmithClipboard.Serialize(FullDesign());

        Assert.False(SmithClipboard.TryDeserialize(full[..(full.Length / 2)], out var design));
        Assert.Null(design);
    }

    /// <summary>The positive half, so the eight negatives above are not passing for the wrong
    /// reason.</summary>
    [Fact]
    public void TryDeserialize_ReadsBackWhatSerializeWrote()
    {
        var before = FullDesign();

        Assert.True(SmithClipboard.TryDeserialize(SmithClipboard.Serialize(before), out var after));

        AssertSame(before, after!);
    }

    /// <summary>
    /// <b>A BLANK string is written as absent, because the model reads it as absent.</b>
    /// </summary>
    /// <remarks>
    /// A <c>FileRef</c> of <c>"   "</c> is "names no file" to <c>SmithElement.Refusal</c>, which
    /// tests it with <c>IsNullOrWhiteSpace</c> — but the writer's own emptiness test was
    /// <c>Length > 0</c>, so it kept writing the blank and the two disagreed about whether the
    /// element had a reference at all. Every field the writer guards this way means the same thing
    /// blank as absent, so one rule settles it.
    /// </remarks>
    [Fact]
    public void ABlankReferenceIsWrittenAsAbsent()
    {
        var d = MinimalDesign();
        d.Elements.Add(new SmithElement { Kind = SmithElementKind.C, Name = "C1", FileRef = "   " });

        Assert.Null(d.Refusal());                                   // a C carries no file reference

        string json = SmithDesignIo.Serialize(d);
        Assert.DoesNotContain("\"FileRef\"", json, StringComparison.Ordinal);
        Assert.Null(SmithDesignIo.Deserialize(json).Elements.Single().FileRef);
    }

    // ── 6. SerializeUnvalidated ──────────────────────────────────────────────

    /// <summary>
    /// <b>A copy always writes.</b> A half-built design — here, an element nobody has named yet — is
    /// exactly the state someone copies from while they are still working, and a copy that wrote
    /// nothing would leave the PREVIOUS copy sitting on the clipboard for the next paste to find.
    /// </summary>
    [Fact]
    public void SerializeUnvalidated_WritesADesignSerializeRefuses_AndThatPayloadRoundTrips()
    {
        var d = Broken("element-unnamed");

        Assert.Throws<InvalidDataException>(() => SmithDesignIo.Serialize(d));

        string json = SmithDesignIo.SerializeUnvalidated(d);
        var back = SmithDesignIo.DeserializeUnvalidated(json);

        Assert.Equal("", back.Elements.Single().Name);
        Assert.NotNull(back.Refusal());

        // And the clipboard goes the whole way with it, which is the reason the unvalidated pair
        // exists at all.
        Assert.True(SmithClipboard.TryDeserialize(SmithClipboard.Serialize(d), out var pasted));
        Assert.Equal("", pasted!.Elements.Single().Name);
    }

    // ── 7. the .s1p import (R-smith1-7) ──────────────────────────────────────

    /// <summary>
    /// <b>Converted against the FILE's own stated reference</b>, which the fixture makes 75 Ω rather
    /// than 50 precisely so a reader that assumed 50 produces visibly different numbers instead of
    /// ones that happen to agree.
    ///
    /// <para>And the path is recorded as PROVENANCE: the values are copied in, so the document is
    /// portable on its own and a moved or archived `.s1p` cannot stop it opening.</para>
    /// </summary>
    [Fact]
    public void ImportingAnS1p_CopiesValuesInAgainstTheFilesOwnReference_AndRecordsThePath()
    {
        string path = Fixture("generator-3pt.s1p");
        var gen = new SmithGenerator();

        SmithGeneratorImport.ImportInto(gen, path);

        Assert.Equal(3, gen.Rows.Count);
        AssertRow(gen.Rows[0], 1e9,  25,   0);
        AssertRow(gen.Rows[1], 2e9,  75,  75);
        AssertRow(gen.Rows[2], 3e9, 150, -75);

        Assert.Equal(path, gen.SourcePath);
    }

    /// <summary>A 2-port handed to the generator is far more likely to be the wrong file than a
    /// request for its input reflection, so it is a refusal naming the file and what it holds.</summary>
    [Fact]
    public void ImportingANonOnePortFile_IsARefusalNamingTheFile()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "two-port.s2p");
        File.WriteAllText(path, "# GHz S RI R 50\n1 0 0 0 0 0 0 0 0\n");

        var ex = Assert.Throws<InvalidDataException>(() => SmithGeneratorImport.Read(path));

        Assert.Contains("two-port.s2p", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2-port", ex.Message, StringComparison.Ordinal);
    }

    // ── 8. Conjugate ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A one-shot edit of the table, and an involution.</b> It is not a persistent flag: a flag
    /// would mean the number in the table and the number the tool uses disagree, and there is no way
    /// to display that which does not eventually mislead someone.
    /// </summary>
    [Fact]
    public void Conjugate_NegatesXAlone_AndTwiceIsTheIdentity()
    {
        var gen = new SmithGenerator();
        gen.Rows.Add(new SmithGeneratorRow(1e9, 12.0, -8.5));
        gen.Rows.Add(new SmithGeneratorRow(2e9, 11.4,  0.0));

        gen.Conjugate();

        AssertRow(gen.Rows[0], 1e9, 12.0, 8.5);
        AssertRow(gen.Rows[1], 2e9, 11.4, 0.0);

        gen.Conjugate();

        AssertRow(gen.Rows[0], 1e9, 12.0, -8.5);
        AssertRow(gen.Rows[1], 2e9, 11.4,  0.0);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  fixtures
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>A design carrying a non-default value for every field there is, and every
    /// <see cref="SmithElementKind"/> in a placement it is allowed in.</summary>
    private static SmithDesign FullDesign()
    {
        var d = new SmithDesign
        {
            Name = "Full",
            Chart = new SmithChartSettings
            {
                Z0Ohm             = 75.0,
                DesignFrequencyHz = 2.0e9,
                Window            = new SmithWindow { MinX = -0.5, MinY = -0.4, MaxX = 0.6, MaxY = 0.7 },
                ShowGrippers      = false,
                ShowTargets       = false,
                ShowLabels        = false,
            },
            Sweep     = new SmithSweep { Enabled = true, StartHz = 1.8e9, StopHz = 2.2e9, Points = 21 },
            ConstantQ = new SmithConstantQ { Enabled = true, Q = 3.5 },
            View = new SmithView
            {
                SplitterMain   = 0.4,
                SplitterSide   = 0.3,
                NetworkScrollX = 12.5,
                NetworkScrollY = -3.25,
                NetworkZoom    = 1.75,
                MirrorNetwork  = true,
            },
        };

        d.Generator.SourcePath = "gen/source.s1p";
        d.Generator.Rows.Add(new SmithGeneratorRow(1.8e9, 12.0, -8.5));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 11.4, -9.1));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.2e9, 10.9, -9.8));

        // Every kind, in a placement it is allowed in — and the ones that may take either take both,
        // so Placement is a field the round trip actually varies.
        Add(d, SmithElementKind.R,    SmithPlacement.Series, v => v.ROhm   = 22.0);
        Add(d, SmithElementKind.L,    SmithPlacement.Shunt,  v => v.LHenry = 2.7e-9);
        Add(d, SmithElementKind.C,    SmithPlacement.Series, v => v.CFarad = 1.2e-12);
        Add(d, SmithElementKind.Prlc, SmithPlacement.Shunt,  v =>
        {
            v.ROhm = 1000.0; v.LHenry = 4.7e-9; v.CFarad = 0.8e-12;
        });
        Add(d, SmithElementKind.Srlc, SmithPlacement.Series, v =>
        {
            v.ROhm = 0.4; v.LHenry = 0.6e-9; v.CFarad = 10e-12;
        });
        Add(d, SmithElementKind.Z1P,  SmithPlacement.Shunt,  v => v.ImpedanceOhm = new Complex(30, -18));
        Add(d, SmithElementKind.S1P,  SmithPlacement.Shunt,  _ => { }, "parts/shunt.s1p");
        Add(d, SmithElementKind.S2P,  SmithPlacement.Series, _ => { }, "parts/thru.s2p");
        Add(d, SmithElementKind.Tline,       SmithPlacement.Series, Line(50,  35));
        Add(d, SmithElementKind.StubOpen,    SmithPlacement.Shunt,  Line(68,  22));
        Add(d, SmithElementKind.StubShorted, SmithPlacement.Shunt,  Line(41, 145));

        // One element with a non-default active parameter and two slider ranges, so both of those
        // fields are carried by something.
        d.Elements[0].ActiveParameter = SmithParameter.R;
        d.Elements[0].SliderRange[SmithParameter.R] = new SmithSliderRange(1.0, 100.0);
        d.Elements[4].SliderRange[SmithParameter.L] = new SmithSliderRange(0.1e-9, 10e-9);
        d.Elements[4].SliderRange[SmithParameter.C] = new SmithSliderRange(1e-12, 100e-12);

        // And one disabled element, because Enabled defaults true and would otherwise be untested.
        d.Elements[2].Enabled = false;

        d.Overlays.Add(new SmithOverlayRef
        {
            SourceKind         = SmithOverlaySource.TouchstoneFile,
            Source             = "meas/dut.s2p",
            Quantity           = "S11",
            Renormalize        = false,
            Visible            = false,
            IncludeInAutoscale = true,
            ColorHex           = "#ff8800",
            Dashed             = true,
        });
        d.Overlays.Add(new SmithOverlayRef
        {
            SourceKind = SmithOverlaySource.Cube,
            Source     = "SP1.S",
            Derived    = "SourceStabilityCircle",
        });

        d.Markers.Add(new SmithMarker
        {
            Name = "m1", Index = 1, Freq = 2.0, FreqUnits = "GHz",
            MatrixFormat = "DB", MatrixFormatImpedance = "MA", Style = "Large",
            UseNormalizedImpedance = false, MaximumFractionDigits = 6,
            InfoBoxX = 120.5, InfoBoxY = 64.25, IsMulti = true, IsDelta = true,
            PositionStaticX = 0.25f, PositionStaticY = -0.5f,
            MarkerKind = "StabilityCircle", ShowInfoBox = false, ContourSnapped = true,
            VswrEnabled = true, VswrValue = 1.5,
        });
        d.Markers.Add(new SmithMarker { Name = "m2", Index = 2, Freq = 2.2 });

        return d;
    }

    private static Action<SmithElementValues> Line(double z0, double lengthDeg) => v =>
    {
        v.Z0Ohm                = z0;
        v.ElectricalLengthDeg  = lengthDeg;
        v.ReferenceFrequencyHz = 2.0e9;
    };

    private static void Add(
        SmithDesign d, SmithElementKind kind, SmithPlacement placement,
        Action<SmithElementValues> values, string? fileRef = null)
    {
        var e = new SmithElement
        {
            Kind            = kind,
            Placement       = placement,
            Name            = $"{kind}{d.Elements.Count + 1}",
            ActiveParameter = SmithComponentMap.DefaultParameter(kind),
            FileRef         = fileRef,
        };
        values(e.Values);
        d.Elements.Add(e);
    }

    /// <summary>A well-formed design with nothing optional set — the base every broken case in
    /// <see cref="Broken"/> is one edit away from.</summary>
    private static SmithDesign MinimalDesign()
    {
        var d = new SmithDesign { Chart = { DesignFrequencyHz = 2.0e9 } };
        d.Generator.Rows.Add(new SmithGeneratorRow(1.0e9, 50.0, 0.0));
        d.Generator.Rows.Add(new SmithGeneratorRow(3.0e9, 50.0, 0.0));
        return d;
    }

    private static SmithDesign Broken(string which)
    {
        var d = MinimalDesign();

        switch (which)
        {
            case "generator-empty":
                d.Generator.Rows.Clear();
                break;

            case "generator-duplicate":
                d.Generator.Rows.Clear();
                d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 50, 0));
                d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 40, 0));
                break;

            case "generator-unsorted":
                d.Generator.Rows.Clear();
                d.Generator.Rows.Add(new SmithGeneratorRow(3.0e9, 50, 0));
                d.Generator.Rows.Add(new SmithGeneratorRow(1.0e9, 40, 0));
                break;

            case "design-freq-outside":
                d.Chart.DesignFrequencyHz = 5.0e9;
                break;

            case "element-unnamed":
                d.Elements.Add(new SmithElement { Kind = SmithElementKind.C, Name = "" });
                break;

            case "element-duplicate":
                d.Elements.Add(new SmithElement { Kind = SmithElementKind.C, Name = "C1" });
                d.Elements.Add(new SmithElement { Kind = SmithElementKind.C, Name = "C1" });
                break;

            case "element-duplicate-case":
                d.Elements.Add(new SmithElement { Kind = SmithElementKind.C, Name = "C1" });
                d.Elements.Add(new SmithElement { Kind = SmithElementKind.C, Name = "c1" });
                break;

            case "shunt-s2p":
                d.Elements.Add(new SmithElement
                {
                    Kind = SmithElementKind.S2P, Placement = SmithPlacement.Shunt,
                    Name = "S1", FileRef = "thru.s2p",
                });
                break;

            case "fileref-missing":
                d.Elements.Add(new SmithElement { Kind = SmithElementKind.S1P, Name = "S1" });
                break;

            case "fileref-unexpected":
                d.Elements.Add(new SmithElement
                {
                    Kind = SmithElementKind.C, Name = "C1", FileRef = "nothing-reads-this.s1p",
                });
                break;

            case "negative-inductance":
                d.Elements.Add(new SmithElement
                {
                    Kind   = SmithElementKind.L, Name = "L1",
                    Values = new SmithElementValues { LHenry = -1e-9 },
                });
                break;

            case "tline-zero-zed":
                d.Elements.Add(Tline("TL1", z0: 0, lengthDeg: 45, fRefHz: 2e9));
                break;

            case "tline-negative-length":
                d.Elements.Add(Tline("TL1", z0: 50, lengthDeg: -45, fRefHz: 2e9));
                break;

            case "tline-no-fref":
                d.Elements.Add(Tline("TL1", z0: 50, lengthDeg: 45, fRefHz: 0));
                break;

            case "sweep-backwards":
                d.Sweep = new SmithSweep { Enabled = true, StartHz = 3e9, StopHz = 1e9, Points = 21 };
                break;

            case "sweep-too-many-points":
                d.Sweep = new SmithSweep
                {
                    Enabled = true, StartHz = 1e9, StopHz = 3e9,
                    Points  = SmithSweep.MaxPoints + 1,
                };
                break;

            case "sweep-one-point":
                d.Sweep = new SmithSweep { Enabled = true, StartHz = 1e9, StopHz = 3e9, Points = 1 };
                break;

            case "q-negative":
                d.ConstantQ = new SmithConstantQ { Enabled = true, Q = -2.0 };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(which), which, "No such broken case.");
        }

        return d;
    }

    private static SmithElement Tline(string name, double z0, double lengthDeg, double fRefHz) => new()
    {
        Kind      = SmithElementKind.Tline,
        Placement = SmithPlacement.Series,
        Name      = name,
        Values    = new SmithElementValues
        {
            Z0Ohm                = z0,
            ElectricalLengthDeg  = lengthDeg,
            ReferenceFrequencyHz = fRefHz,
        },
    };

    // ═════════════════════════════════════════════════════════════════════════
    //  helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static void AssertSame(SmithDesign a, SmithDesign b)
    {
        Assert.Equal(a.Name, b.Name);

        Assert.Equal(a.Chart.Z0Ohm,             b.Chart.Z0Ohm);
        Assert.Equal(a.Chart.DesignFrequencyHz, b.Chart.DesignFrequencyHz);
        Assert.Equal(a.Chart.ShowGrippers,      b.Chart.ShowGrippers);
        Assert.Equal(a.Chart.ShowTargets,       b.Chart.ShowTargets);
        Assert.Equal(a.Chart.ShowLabels,        b.Chart.ShowLabels);
        Assert.Equal(a.Chart.Window?.MinX, b.Chart.Window?.MinX);
        Assert.Equal(a.Chart.Window?.MinY, b.Chart.Window?.MinY);
        Assert.Equal(a.Chart.Window?.MaxX, b.Chart.Window?.MaxX);
        Assert.Equal(a.Chart.Window?.MaxY, b.Chart.Window?.MaxY);

        Assert.Equal(a.Generator.SourcePath, b.Generator.SourcePath);
        Assert.Equal(a.Generator.Rows.Count, b.Generator.Rows.Count);
        for (int i = 0; i < a.Generator.Rows.Count; i++)
            AssertRow(b.Generator.Rows[i],
                      a.Generator.Rows[i].FrequencyHz,
                      a.Generator.Rows[i].ResistanceOhm,
                      a.Generator.Rows[i].ReactanceOhm);

        Assert.Equal(a.Elements.Count, b.Elements.Count);
        for (int i = 0; i < a.Elements.Count; i++)
        {
            var (x, y) = (a.Elements[i], b.Elements[i]);
            Assert.Equal(x.Kind,            y.Kind);
            Assert.Equal(x.Placement,       y.Placement);
            Assert.Equal(x.Name,            y.Name);
            Assert.Equal(x.Enabled,         y.Enabled);
            Assert.Equal(x.ActiveParameter, y.ActiveParameter);
            Assert.Equal(x.FileRef,         y.FileRef);

            Assert.Equal(x.Values.ROhm,                 y.Values.ROhm);
            Assert.Equal(x.Values.LHenry,               y.Values.LHenry);
            Assert.Equal(x.Values.CFarad,               y.Values.CFarad);
            Assert.Equal(x.Values.Z0Ohm,                y.Values.Z0Ohm);
            Assert.Equal(x.Values.ElectricalLengthDeg,  y.Values.ElectricalLengthDeg);
            Assert.Equal(x.Values.ReferenceFrequencyHz, y.Values.ReferenceFrequencyHz);
            Assert.Equal(x.Values.ImpedanceOhm,         y.Values.ImpedanceOhm);

            Assert.Equal(x.SliderRange.Count, y.SliderRange.Count);
            foreach (var (p, range) in x.SliderRange)
            {
                Assert.True(y.SliderRange.TryGetValue(p, out var got));
                Assert.Equal(range.Min, got!.Min);
                Assert.Equal(range.Max, got.Max);
            }
        }

        Assert.Equal(a.Sweep.Enabled, b.Sweep.Enabled);
        Assert.Equal(a.Sweep.StartHz, b.Sweep.StartHz);
        Assert.Equal(a.Sweep.StopHz,  b.Sweep.StopHz);
        Assert.Equal(a.Sweep.Points,  b.Sweep.Points);

        Assert.Equal(a.ConstantQ.Enabled, b.ConstantQ.Enabled);
        Assert.Equal(a.ConstantQ.Q,       b.ConstantQ.Q);

        Assert.Equal(a.Overlays.Count, b.Overlays.Count);
        for (int i = 0; i < a.Overlays.Count; i++)
        {
            var (x, y) = (a.Overlays[i], b.Overlays[i]);
            Assert.Equal(x.SourceKind,         y.SourceKind);
            Assert.Equal(x.Source,             y.Source);
            Assert.Equal(x.Quantity,           y.Quantity);
            Assert.Equal(x.Derived,            y.Derived);
            Assert.Equal(x.Renormalize,        y.Renormalize);
            Assert.Equal(x.Visible,            y.Visible);
            Assert.Equal(x.IncludeInAutoscale, y.IncludeInAutoscale);
            Assert.Equal(x.ColorHex,           y.ColorHex);
            Assert.Equal(x.Dashed,             y.Dashed);
        }

        Assert.Equal(a.Markers.Count, b.Markers.Count);
        for (int i = 0; i < a.Markers.Count; i++)
        {
            var (x, y) = (a.Markers[i], b.Markers[i]);
            Assert.Equal(x.Name,                   y.Name);
            Assert.Equal(x.Index,                  y.Index);
            Assert.Equal(x.Freq,                   y.Freq);
            Assert.Equal(x.FreqUnits,              y.FreqUnits);
            Assert.Equal(x.MatrixFormat,           y.MatrixFormat);
            Assert.Equal(x.MatrixFormatImpedance,  y.MatrixFormatImpedance);
            Assert.Equal(x.Style,                  y.Style);
            Assert.Equal(x.UseNormalizedImpedance, y.UseNormalizedImpedance);
            Assert.Equal(x.MaximumFractionDigits,  y.MaximumFractionDigits);
            Assert.Equal(x.InfoBoxX,               y.InfoBoxX);
            Assert.Equal(x.InfoBoxY,               y.InfoBoxY);
            Assert.Equal(x.IsMulti,                y.IsMulti);
            Assert.Equal(x.IsDelta,                y.IsDelta);
            Assert.Equal(x.PositionStaticX,        y.PositionStaticX);
            Assert.Equal(x.PositionStaticY,        y.PositionStaticY);
            Assert.Equal(x.MarkerKind,             y.MarkerKind);
            Assert.Equal(x.ShowInfoBox,            y.ShowInfoBox);
            Assert.Equal(x.ContourSnapped,         y.ContourSnapped);
            Assert.Equal(x.VswrEnabled,            y.VswrEnabled);
            Assert.Equal(x.VswrValue,              y.VswrValue);
        }

        Assert.Equal(a.View.SplitterMain,   b.View.SplitterMain);
        Assert.Equal(a.View.SplitterSide,   b.View.SplitterSide);
        Assert.Equal(a.View.NetworkScrollX, b.View.NetworkScrollX);
        Assert.Equal(a.View.NetworkScrollY, b.View.NetworkScrollY);
        Assert.Equal(a.View.NetworkZoom,    b.View.NetworkZoom);
        Assert.Equal(a.View.MirrorNetwork,  b.View.MirrorNetwork);
    }

    private static void AssertRow(SmithGeneratorRow row, double fHz, double rOhm, double xOhm)
    {
        Assert.Equal(fHz,  row.FrequencyHz,   6);
        Assert.Equal(rOhm, row.ResistanceOhm, 9);
        Assert.Equal(xOhm, row.ReactanceOhm,  9);
    }

    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "testdata", "smith", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"testdata/smith/{name} not found above {AppContext.BaseDirectory}");
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csmith-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
