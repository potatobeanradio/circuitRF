using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Layout Editor's DXF export carries the bond wires (owner, 2026-08-27: a <c>.clay</c> holding
/// wires exported with its primitives present and every wire absent).
///
/// <para><b>The writer was never the problem</b> — it has taken a <c>WBondDesign</c> since wbond.md
/// §9.4, and the wBond editor's own export passes one. The Layout Editor passed <c>null</c>, and
/// <c>DxfExport.Preview</c>'s own doc comment blessed it as "the Layout Editor's own case". WB40 made
/// that stale: a wirebond CELL keeps its wires in a <c>.wBond</c> sidecar beside the <c>.clay</c>, so
/// this editor opens documents that have them.</para>
///
/// <para>The defect lives in <c>LayoutEditorView.axaml.cs</c>, which is code-behind this project
/// cannot construct (Ui.Tests calls no Avalonia runtime API, by the project's own rule), so the
/// wiring is pinned by SOURCE SCAN — the same fallback used for menu structure and AXAML wiring
/// throughout this suite — and the behaviour it depends on is driven directly.</para>
/// </summary>
public class LayoutDxfWireExportTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The file with its comments removed — a scan that matches inside a comment proves
    /// nothing, and this file's own explanation of the fix names both calls.</summary>
    private static string ReadStripped(string relative)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), relative));
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\n]*", "");
        return text;
    }

    // ── The wiring ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLayoutEditorsDxfExport_PassesItsWireDesign_ToBothThePreviewAndTheWrite()
    {
        string src = ReadStripped(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"));

        // The preview matters as much as the write: it is what the fidelity dialog reports, and a
        // preview that omitted the wires would describe a different file from the one that lands.
        Assert.Matches(@"DxfExport\.Preview\([^)]*wires[^)]*\)", src);
        Assert.Matches(@"DxfExport\.Write\([^)]*wires[^)]*\)", src);

        // …and "wires" has to be the editor's own design, not some other local.
        Assert.Contains("vm.WireDesign", src);
    }

    [Fact]
    public void TheWBondEditorsOwnExport_StillPassesItsDesign()
    {
        // The path that already worked. Without this, "make the layout editor pass wires" could be
        // satisfied by moving the argument rather than adding one.
        string src = ReadStripped(Path.Combine("src", "Ui", "Views", "WBond", "WBondEditorView.Dxf.cs"));
        // [^;]* rather than [^)]*: the call's own arguments contain parentheses.
        Assert.Matches(@"DxfExport\.Write\([^;]*\.Design\)", src);
    }

    // ── The behaviour it depends on ──────────────────────────────────────────────────────────────

    private static (DxfExport.ExportPlan Plan, WBondDesign Design) Fixture()
    {
        var structure = new InterchangeStructure(
            "TOP",
            [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 20_000 }],
            []);

        var plan = new DxfExport.ExportPlan(
            UnresolvedInstanceReferences: [],
            BlockNameByCellName: new System.Collections.Generic.Dictionary<string, string> { ["TOP"] = "TOP" },
            Structures: [structure],
            RootStructureName: "TOP",
            Tech: null,
            DbuPerMicron: 1000);

        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        for (int i = 0; i < 3; i++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                new Point3(0, i * 5_000_000, 0),
                new Point3(20_000_000, i * 5_000_000, 0),
                WBondDefaults.DiameterNm, WBondDefaults.Material, 2_000_000, WBondDefaults.Points));
        design.Arrays.Add(array);

        return (plan, design);
    }

    [Fact]
    public void APlanWithWires_WritesThem_AndTheSamePlanWithoutThemWritesNone()
    {
        var (plan, design) = Fixture();

        // The differential IS the bug: the same plan, the same options, one argument apart.
        Assert.Equal(0, DxfExport.Preview(plan, new DxfExportOptions(), null).WiresWritten);
        Assert.Equal(3, DxfExport.Preview(plan, new DxfExportOptions(), design).WiresWritten);
    }

    [Fact]
    public void TheWiresReachTheFile_OnTheirOwnLayer()
    {
        var (plan, design) = Fixture();

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
        try
        {
            var summary = DxfExport.Write(path, plan, new DxfExportOptions(), design);
            Assert.Equal(3, summary.WiresWritten);

            string dxf = File.ReadAllText(path);
            string layer = DxfWireIo.LayerNameFor("G1");
            Assert.Contains(layer, dxf);

            // A POLYLINE per wire, so a reader that never resolves a block still draws them — which
            // is the whole complaint, since the wires were invisible in a third-party viewer.
            Assert.True(Regex.Matches(dxf, @"\bPOLYLINE\b").Count >= 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AnOrdinaryLayoutWithNoWires_WritesExactlyWhatItAlwaysDid()
    {
        // Null stays the ordinary case, and must cost nothing: no wire layer, no wire entities.
        var (plan, _) = Fixture();

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
        try
        {
            var summary = DxfExport.Write(path, plan, new DxfExportOptions(), null);
            Assert.Equal(0, summary.WiresWritten);
            Assert.DoesNotContain(DxfWireIo.LayerPrefix, File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
