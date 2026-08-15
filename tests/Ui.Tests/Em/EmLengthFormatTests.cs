// Owner request, 2026-08-15: "all messages from running an EM sim that reference distance/length
// need to respect the units of the .clay file."
//
// The engine (src/Engine/Mom) cannot know about mil/µm/DBU — the UI firewall forbids it from
// referencing LayoutUnits at all — so SurfaceMesher.Mesh (and every entry point downstream of it)
// takes an optional SurfaceMesher.PlanarLengthFormat delegate, defaulting to the pre-existing SI
// engineering notation when null. EmLengthFormat.For builds the real one from the layout's own
// DisplayUnit, on this side of the firewall. This file gates the round trip: a layout set to a
// non-default display unit gets its mesh notes in that unit, and a layout left at the default gets
// byte-identical text to before this existed (SI, "m").

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmLengthFormatTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    // A taper narrow enough that "Narrowest conductor dimension" reports a small, distinctive value —
    // 300 µm — which reads very differently in mil (11.8110 mil) than in the default SI note (300 µm).
    private static LayoutView TaperLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        {
            Layer = new(1, 0),
            X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 300_000,
        });
        return view;
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-lenfmt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static EmSetupEditorViewModel Editor(string dir, LayoutUnit displayUnit)
    {
        string path  = Path.Combine(dir, "panel.cem");
        var    setup = new EmSetup { Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        EmSetupPersistence.SaveToFile(path, setup);
        var view = TaperLayout();
        view.DisplayUnit = displayUnit;
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), view, StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    [Fact]
    public void OnAMilDisplayUnitLayout_TheMeshNotesReadInMil_NotMicrometres()
    {
        string dir = TempDir();
        try
        {
            var vm = Editor(dir, LayoutUnit.Mil);
            vm.BuildPlanarMesh();

            var note = Assert.Single(vm.PlanarMeshNotes,
                n => n.Contains("Narrowest conductor dimension", StringComparison.Ordinal));

            Assert.Contains("mil", note, StringComparison.Ordinal);
            Assert.DoesNotContain("µm", note, StringComparison.Ordinal);
            Assert.DoesNotContain(" m,", note, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OnTheDefaultDisplayUnit_TheMeshNotesAreByteIdentical_ToSiEngineeringNotation()
    {
        string dir = TempDir();
        try
        {
            var vm = Editor(dir, LayoutUnit.Um);
            vm.BuildPlanarMesh();

            var note = Assert.Single(vm.PlanarMeshNotes,
                n => n.Contains("Narrowest conductor dimension", StringComparison.Ordinal));

            // µm is what SurfaceMesher.Eng's own SI-prefix table calls it too, so this is checking
            // for the RIGHT reason: LayoutUnit.Um's suffix ("µm") happens to collide with Eng's own
            // prefix+base-unit spelling at this magnitude, which is exactly why the un-set-unit case
            // still needs its own gate against the direct, un-formatted engine call.
            var direct = SurfaceMesher.Mesh(vm.PlanarProblem!, vm.Working.PlanarMesh);
            Assert.Equal(
                direct.Notes.Single(n => n.Contains("Narrowest conductor dimension", StringComparison.Ordinal)),
                note);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ForBuildsAFormatter_ThatRoundTripsAKnownLength()
    {
        // 300 µm = 0.0003 m. In mil that is 11.8110... — LayoutUnits.Format's own 4-decimal default.
        var fmt = EmLengthFormat.For(LayoutUnit.Mil, Dbu);
        Assert.Equal("11.811 mil", fmt(300e-6));

        var fmtMm = EmLengthFormat.For(LayoutUnit.Mm, Dbu);
        Assert.Equal("0.3 mm", fmtMm(300e-6));
    }
}
