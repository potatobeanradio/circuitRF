using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-6 — a test-design-set cell made EM-capable: the design copied into a scratch workspace, its layout made by
/// the planner and saved, a planar <c>.cem</c> written for it (coarse: three frequencies, a 10-cells-per-wavelength mesh,
/// so the one real solve stays short), and one S-parameter analysis <c>SP1</c> over the setup's band saved into its
/// schematic.
/// </summary>
internal sealed record EmCellFixture(SdlChecker.Prepared P, string Cem)
{
    public string Workspace => P.Workspace;
    public string Cws => Path.Combine(P.Workspace, ".cws");
    public string Csch => P.SchematicPath;

    public static EmCellFixture Create(string design, string scratch, AnalysisModel sparamModel = AnalysisModel.Circuit,
                                       ParametricSweepAnalysis? sweep = null, Action<SchematicEditModel>? edit = null)
    {
        var p = SdlChecker.Prepare(design, Path.Combine(scratch, design));
        edit?.Invoke(p.Model);   // before the layout is planned, so the layout is made from the edited schematic
        var layout = p.Applied(p.Plan());
        LayoutPersistence.SaveToFile(p.ClayPath, layout);
        var created = EmSetupCreate.Create(p.Workspace, design, EmSetupCreate.LayoutRefOf(p.Workspace, p.ClayPath), s =>
        {
            s.AnalysisKind = EmAnalysisKind.Planar;
            s.Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz");
            s.PlanarMesh = s.PlanarMesh with { CellsPerWavelength = 10 };
        });
        var sp = new SParameterAnalysis("SP1", [new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz")])
        { Model = sparamModel };
        p.Model.Analyses.Add(sp);
        if (sweep is not null) p.Model.Analyses.Add(sweep);
        CircuitRF.Design.Schematic.SchematicPersistence.SaveToFile(p.SchematicPath, p.Model);
        return new EmCellFixture(p, created.Path!);
    }

    /// <summary>The user's three files, as bytes — what an evaluation must leave unchanged.</summary>
    public (string, string, string) UserFileBytes() =>
        (File.ReadAllText(P.SchematicPath), File.ReadAllText(P.ClayPath), File.ReadAllText(Cem));
}
