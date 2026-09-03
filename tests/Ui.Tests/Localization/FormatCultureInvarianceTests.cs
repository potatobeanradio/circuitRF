// ================================================================
//  FormatCultureInvarianceTests.cs — every file circuitRF writes must be
//  byte-for-byte identical no matter what locale the machine runs at.
//  (docs/sonnet-briefs/brief-localization-groundwork.md §5, R-loc-2)
// ================================================================

using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Schematic;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;

namespace CircuitRF.Ui.Tests.Localization;

/// <summary>
/// The locale analogue of <c>tests/Firewall.Tests</c>'s UI-framework gate: circuitRF's file formats
/// are invariant today (checked writer by writer), and this exists so that stops being a property
/// that merely happens to hold.
///
/// <para><b>Why byte equality and not a round trip.</b> A round trip through a writer and its own
/// matching reader is not sufficient and is the trap this whole gate is built around: a
/// comma-decimal writer paired with a comma-decimal reader agrees with itself perfectly, passes
/// every round-trip assertion, and produces a file nobody else on earth can open. The only
/// assertion that catches that is the one made here — write the same fixture under a foreign locale
/// and under <c>en-US</c>, and demand the BYTES match.</para>
///
/// <para><b>Why a source scan would not do.</b> Grepping for <c>InvariantCulture</c> proves nothing:
/// it cannot see <c>System.Text.Json</c> (invariant by construction, and the mechanism behind seven
/// of these formats), and <c>GerberUnits</c> is correct while naming a culture only incidentally —
/// it never converts a <c>double</c> at all. The gate has to be behavioural.</para>
///
/// <para><b>Why two probe cultures.</b> <c>de-DE</c> is the sharpest probe for NUMBERS — comma
/// decimal and dot thousands, so a leak is visible in both directions at once. It is blind to a
/// second leak class, though, and that class was live in this repo when this file was written: in a
/// CUSTOM date format string <c>':'</c> is the culture's TIME-SEPARATOR PLACEHOLDER rather than a
/// literal colon, and German happens to use <c>':'</c> anyway. <c>fi-FI</c> uses <c>'.'</c>, which
/// is what exposed Gerber's <c>%TF.CreationDate</c> and the <c>.gbrjob</c> header emitting
/// <c>2026-08-27T14.23.05Z</c> — not ISO-8601, and not something any receiving CAM tool would
/// accept. Both are fixed; both are gated here.</para>
///
/// <para><b>Culture and parallelism.</b> Setting <see cref="CultureInfo.CurrentCulture"/> affects
/// the CURRENT THREAD only — it is not the process-wide switch that
/// <c>DefaultThreadCurrentCulture</c> is (which <c>tests/TestCulture.cs</c> owns, pinning the whole
/// suite to <c>en-US</c>). That is what makes this gate safe to run alongside the rest of the
/// suite. The collection below still serializes these tests and every probe still restores in a
/// <c>finally</c>, because the cost is nil and the failure mode — flaking OTHER projects under
/// full-solution load, where isolated repetition will never reproduce it — is the expensive kind.</para>
///
/// <para><b>Not <c>InvariantGlobalization</c>.</b> That switch would make this file pass trivially
/// and for entirely the wrong reason, while making a future localization impossible and changing
/// collation everywhere. It is not the shortcut.</para>
/// </summary>
[Collection(CultureProbeCollection.Name)]
public sealed class FormatCultureInvarianceTests
{
    /// <summary>Comma decimal + dot grouping (numbers), and comma decimal + dot time separator
    /// (dates). Between them every culture-varying element a writer can leak is visible.</summary>
    private static readonly string[] ProbeCultures = ["de-DE", "fi-FI"];

    /// <summary>A fixed instant, so a timestamp a writer stamps into its own output is not itself a
    /// source of difference between the two writes being compared.</summary>
    private static readonly DateTime Stamp = new(2026, 8, 27, 14, 23, 5, DateTimeKind.Utc);

    // ── The gate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="write"/>'s fixture under <c>en-US</c> and under each probe culture and
    /// demands the bytes match. <paramref name="write"/> must be deterministic in everything BUT
    /// culture — no clock, no GUID, no hash-order iteration — or this reports a false leak.
    /// </summary>
    private static void AssertBytesAreCultureIndependent(string format, Func<byte[]> write)
    {
        byte[] reference = InCulture("en-US", write);

        foreach (var probe in ProbeCultures)
        {
            byte[] actual = InCulture(probe, write);

            if (!reference.AsSpan().SequenceEqual(actual))
                Assert.Fail(
                    $"CULTURE LEAK in the {format} writer: the file written under '{probe}' differs " +
                    $"byte-for-byte from the one written under 'en-US'.\n" +
                    $"A file format is machine-readable and must not follow the user's locale — a " +
                    $"comma decimal, a dot thousands separator or a localized time separator makes it " +
                    $"unreadable everywhere else, INCLUDING by circuitRF on another machine.\n" +
                    $"First difference:\n{DescribeFirstDifference(reference, actual, probe)}");
        }
    }

    private static byte[] InCulture(string name, Func<byte[]> write)
    {
        var previous   = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            var ci = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture   = ci;
            CultureInfo.CurrentUICulture = ci;
            return write();
        }
        finally
        {
            CultureInfo.CurrentCulture   = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    /// <summary>The failure message is the whole value of this gate — "bytes differ" sends the
    /// reader back to a diff tool, whereas the offending line usually names the bug outright.</summary>
    private static string DescribeFirstDifference(byte[] reference, byte[] actual, string probe)
    {
        int i = 0;
        while (i < reference.Length && i < actual.Length && reference[i] == actual[i]) i++;

        // Binary formats have no useful line to quote; report the offset instead.
        if (!LooksLikeText(reference))
            return $"  byte offset {i}: en-US 0x{(i < reference.Length ? reference[i] : 0):X2} " +
                   $"vs {probe} 0x{(i < actual.Length ? actual[i] : 0):X2} " +
                   $"(lengths {reference.Length} and {actual.Length})";

        string refText = Encoding.UTF8.GetString(reference);
        string actText = Encoding.UTF8.GetString(actual);
        int line = refText.Take(i).Count(c => c == '\n') + 1;
        return $"  line {line} (byte offset {i}):\n" +
               $"    en-US : {LineAt(refText, line)}\n" +
               $"    {probe,-6}: {LineAt(actText, line)}";
    }

    private static bool LooksLikeText(byte[] bytes) =>
        bytes.Take(512).All(b => b is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\r' or (byte)'\t');

    private static string LineAt(string text, int oneBased)
    {
        var lines = text.Split('\n');
        return oneBased - 1 < lines.Length ? lines[oneBased - 1].TrimEnd('\r') : "<past end of file>";
    }

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    private static byte[] ViaWriter(Action<TextWriter> write)
    {
        var sw = new StringWriter { NewLine = "\n" };
        write(sw);
        return Utf8(sw.ToString());
    }

    private static byte[] ViaTempFile(string extension, Action<string> write)
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"crf-culture-{Guid.NewGuid():N}{extension}");
        try
        {
            write(path);
            return File.ReadAllBytes(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ══ The design-layer documents — all System.Text.Json ═════════════════════
    //
    // JSON numbers are invariant by construction, so these are expected to be green from the first
    // run. They are gated anyway: the property that protects them is "the writer is STJ", and
    // nothing stops someone hand-formatting one value into a string field later.

    [Fact]
    public void Clay_LayoutView_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".clay", () => Utf8(LayoutPersistence.Serialize(SampleLayout())));

    [Fact]
    public void Ctech_Technology_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".ctech", () => Utf8(TechPersistence.Serialize(SampleTechnology())));

    [Fact]
    public void Cem_EmSetup_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".cem", () => Utf8(EmSetupPersistence.Serialize(SampleEmSetup())));

    [Fact]
    public void Cws_Workspace_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".cws", () => Utf8(WorkspacePersistence.Serialize(SampleWorkspace())));

    [Fact]
    public void Ccell_Cell_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".ccell", () => Utf8(CellPersistence.Serialize(SampleCell())));

    // ══ The netlist ══════════════════════════════════════════════════════════

    /// <summary>
    /// Driven from a REAL committed netlist rather than a hand-built TestBench: reading
    /// <c>testdata/pi_network.cnl</c> and writing it back exercises the writer's whole numeric
    /// surface — component values, parameters, analysis directives, sweep specs — instead of only
    /// the handful of fields a synthetic fixture would remember to set.
    /// </summary>
    [Fact]
    public void Cnl_Netlist_IsCultureIndependent()
    {
        string source = TestDataPath("pi_network.cnl");
        AssertBytesAreCultureIndependent(".cnl", () =>
        {
            var (library, bench) = CnlReader.ReadFile(source);
            return Utf8(CnlWriter.Write(bench, library));
        });
    }

    // ══ The RF interchange formats ═══════════════════════════════════════════

    [Fact]
    public void Touchstone_IsCultureIndependent()
    {
        string source = TestDataPath(Path.Combine("Hero1", "potentially_unstable_amp.s2p"));
        AssertBytesAreCultureIndependent("Touchstone (.s2p)", () =>
        {
            var snp = TouchstoneIO.ReadFile(source);
            return ViaWriter(w => TouchstoneIO.Write(snp, w));
        });
    }

    [Fact]
    public void Spl_Loadpull_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".spl", () => ViaWriter(w => SplWriter.WriteSpl(SampleLoadpull(), w)));

    [Fact]
    public void Lpcwave_Loadpull_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".lpcwave", () => ViaWriter(w => LpcwaveWriter.WriteLpcwave(SampleLoadpull(), w)));

    [Fact]
    public void Tsv_DataSetExport_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".txt (TSV)",
            () => ViaTempFile(".txt", p => DataSetExporter.Export(SampleLoadpull(), p, ExportFormat.Tsv)));

    [Fact]
    public void Npy_DataSetExport_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".npy",
            () => ViaTempFile(".npy", p => DataSetExporter.Export(SampleLoadpull(), p, ExportFormat.Npy)));

    [Fact]
    public void Mat_DataSetExport_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".mat",
            () => ViaTempFile(".mat", p => DataSetExporter.Export(SampleLoadpull(), p, ExportFormat.Mat)));

    // ══ The artwork interchange formats ══════════════════════════════════════

    [Fact]
    public void KicadPcb_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".kicad_pcb", () => ViaWriter(w => PcbWriter.Write(w, SamplePcbModel())));

    [Fact]
    public void Dxf_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent("DXF", () => ViaWriter(w => DxfWriter.Write(
            w,
            [new InterchangeStructure("top", SampleShapes(), [])],
            "top",
            SampleTechnology(),
            LayoutUnits.DefaultDbuPerMicron,
            new DxfExportOptions())));

    /// <summary>
    /// Gerber carries BOTH leak classes in one file — coordinates and aperture diameters (numbers)
    /// and <c>%TF.CreationDate</c> (a custom date format whose <c>':'</c> is a culture placeholder).
    /// The second one was live until this gate was written; see the class remarks.
    /// </summary>
    [Fact]
    public void Gerber_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent("Gerber", () =>
        {
            using var ms = new MemoryStream();
            GerberWriter.Write(ms, SampleLayerDef(), SampleShapes(),
                GerberUnits.Resolve(LayoutUnits.DefaultDbuPerMicron), SampleTechnology(), Stamp);
            return ms.ToArray();
        });

    [Fact]
    public void GerberJobFile_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent(".gbrjob", () =>
        {
            using var ms = new MemoryStream();
            GerberJobFile.Write(ms,
                [new GerberJobFile.FileAttribute("top.gbr", "Copper,L1,Top", "Positive")],
                Stamp, "1.0.0");
            return ms.ToArray();
        });

    [Fact]
    public void Excellon_IsCultureIndependent() =>
        AssertBytesAreCultureIndependent("Excellon", () =>
        {
            using var ms = new MemoryStream();
            ExcellonWriter.Write(ms, SampleVias(), GerberUnits.Resolve(LayoutUnits.DefaultDbuPerMicron));
            return ms.ToArray();
        });

    // ══ Fixtures ═════════════════════════════════════════════════════════════
    //
    // Every fixture deliberately carries values with a FRACTIONAL part and values above 1000. A
    // fixture built entirely from small integers would pass all of the above under any locale and
    // prove nothing: there is no decimal separator to get wrong and no thousands separator to
    // insert.

    private static List<LayoutShape> SampleShapes() =>
    [
        new PathShape  { Layer = new LayerKey(1, 0), Xy = [0, 0, 1_234_500, 987_650], Width = 152_400, Net = "RF" },
        new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 2_540_000, 0, 2_540_000, 1_270_000, 0, 1_270_000] },
        new CircleShape { Layer = new LayerKey(1, 0), Cx = 3_175_000, Cy = 1_587_500, R = 254_000 },
        new RectShape  { Layer = new LayerKey(2, 0), X1 = -1_000_500, Y1 = -2_000_250, X2 = 1_000_500, Y2 = 2_000_250 },
    ];

    private static List<ViaShape> SampleVias() =>
    [
        new ViaShape { X = 1_234_500, Y = -987_650, PadSize = 508_000, DrillSize = 304_800 },
        new ViaShape { X = 2_469_000, Y =  987_650, PadSize = 508_000, DrillSize = 254_000 },
    ];

    private static LayerDef SampleLayerDef() => new()
    {
        Key = new LayerKey(1, 0),
        Name = "M1",
        FillOpacity = 0.35,
    };

    private static LayoutView SampleLayout()
    {
        var view = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = LayoutUnit.Um,
            SnapDbu      = 1000,
            TechRef      = "../tech/board.ctech",
        };
        view.Shapes.AddRange(SampleShapes());
        // A non-cardinal angle, so the one genuinely fractional field in this format is exercised.
        view.Instances.Add(new LayoutInstance { CellRef = "../sub", X = 1_234_500, Y = 2_345_600, RotationDegrees = 37.5 });
        return view;
    }

    private static Technology SampleTechnology()
    {
        var tech = new Technology { Name = "board", DefaultDisplayUnit = LayoutUnit.Um };
        tech.Layers.Add(SampleLayerDef());
        tech.Layers.Add(new LayerDef { Key = new LayerKey(2, 0), Name = "M2", FillOpacity = 0.125 });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Name = "core", Kind = StackupKind.Dielectric, ThicknessDbu = 1_524_000,
            // The four the .ctech editor's own rows stage and commit — see StackupLayerRowViewModel.
            Epsr = 4.4, TanD = 0.0027, Mur = 1.0, SigmaSm = 5.8e7,
        });
        return tech;
    }

    private static EmSetup SampleEmSetup() => new()
    {
        Name = "hero", LayoutRef = "Amp/layout/Amp.clay",
        SignalStackupLayerName = "M1",
        // Complex reference impedances and a fractional-GHz sweep: the .cem's real numeric surface.
        Port1Z0 = new Complex(50.5, -1.25),
        Port2Z0 = new Complex(75.25, 0.5),
        PortZ0s = [new Complex(50.5, -1.25), new Complex(75.25, 0.5)],
        Frequency = new FrequencySpec("1.5", "20.25", 101, SweepKind.Linear, "GHz", "GHz"),
    };

    private static CwsFile SampleWorkspace() => new()
    {
        FormatVersion = WorkspacePersistence.CurrentFormatVersion,
        LibraryRefs = ["../lib"],
        KnownFiles  = ["Amp/Amp.ccell"],
    };

    private static CcellFile SampleCell() => new()
    {
        FormatVersion = 1,
        PrimarySchematic = "amp.csch",
        PrimarySymbol    = "amp.csym",
    };

    private static PcbExportModel SamplePcbModel()
    {
        var model = new PcbExportModel
        {
            Tech = SampleTechnology(),
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            BoardTitle = "culture probe",
        };
        model.BoardShapes.AddRange(SampleShapes());
        return model;
    }

    /// <summary>A loadpull-shaped DataSet — the input contract the .spl/.lpcwave writers and the
    /// DataSet exporters all share, so one fixture serves six formats.</summary>
    private static DataSet SampleLoadpull()
    {
        const int ng = 4, np = 3;
        var grid = new Axis("gridPoint", Enumerable.Range(0, ng).Select(i => (double)i).ToArray());
        var pin  = new Axis("pinStep",   [-10.5, -5.25, 0.0]);
        var freq = new Axis("freq", [2_400_000_000.5], "Hz");

        var gamma = new Complex[ng];
        for (int gi = 0; gi < ng; gi++)
            gamma[gi] = Complex.FromPolarCoordinates(0.2 + 0.15 * gi, gi * 0.5);

        DataCube Fom(Func<int, int, double> f)
        {
            var buf = new double[ng * np];
            for (int gi = 0; gi < ng; gi++)
                for (int pi = 0; pi < np; pi++)
                    buf[gi * np + pi] = f(gi, pi);
            return new DataCube([grid, pin], buf);
        }

        var ds = new DataSet();
        ds.Add("GammaLoad",  new DataCube([grid], gamma));
        // Values chosen to straddle 1000 as well as carrying a fraction — a leaked thousands
        // separator is only visible on a number big enough to have one.
        ds.Add("Pout_dBm",   Fom((gi, pi) => 1030.125 + gi + pi));
        ds.Add("Gt_dB",      Fom((gi, pi) => 12.5 + 0.1 * gi - 0.2 * pi));
        ds.Add("Efficiency", Fom((gi, pi) => 40.75 + 2.0 * gi + pi));
        ds.Add("PAE",        Fom((gi, pi) => 35.25 + 2.0 * gi + pi));
        ds.Add("Zin_real",   Fom((gi, pi) => 60.5 + gi));
        ds.Add("Zin_imag",   Fom((gi, pi) => -5.25 + pi));
        ds.Add("ZSource",    new DataCube([freq], [new Complex(40.5, 10.25)]));
        ds.Add("__Freq",     new DataCube([freq], [2_400_000_000.5]));
        return ds;
    }

    // ── Locating testdata/ ───────────────────────────────────────────────────

    private static string TestDataPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testdata")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repo's testdata/ folder from " + AppContext.BaseDirectory);
        string path = Path.Combine(dir!.FullName, "testdata", relative);
        Assert.True(File.Exists(path), $"Fixture not found: {path}");
        return path;
    }
}

/// <summary>
/// Serializes every culture-probing test in one collection. See
/// <see cref="FormatCultureInvarianceTests"/>'s remarks: the probes set only the CURRENT THREAD's
/// culture, so this is belt-and-braces rather than the load-bearing part — but culture bugs that
/// escape into other test projects only ever show up under full-solution load, which is precisely
/// the shape isolated repetition never reproduces.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CultureProbeCollection
{
    public const string Name = "CultureProbe";
}
