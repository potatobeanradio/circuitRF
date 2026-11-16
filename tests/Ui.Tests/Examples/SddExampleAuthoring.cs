// ================================================================
//  SddExampleAuthoring.cs — the "Symbolically Defined Device (SDD)" example: a GaN FET model written as SDD equations,
//  built up over five benches from its drain current to its charges, each under the same loadpull, and a sixth whose
//  SDD reads another part's current. BUILT from netlists with the function `circuitrf netlist --to-schematic` calls.
//  Skipped unless CRF_AUTHOR_SDD_EXAMPLE=1, because it WRITES examples/Symbolically Defined Device (SDD) (the README is
//  kept).
//
//  The FET is the hero SDD FET every harmonic-balance reference in this repository uses (testdata/Hero2), at the
//  Loadpull and Pursuit example's 1.25 mm periphery, bias and load grid; the gate, charge and bias-loop equations are
//  this example's own.
// ================================================================

using System.Text.Json.Nodes;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class SddExampleAuthoring
{
    public const string Folder = "Symbolically Defined Device (SDD)";

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_SDD_EXAMPLE") != "1")
                Skip = "re-builds the SDD example only with CRF_AUTHOR_SDD_EXAMPLE=1";
        }
    }

    public static string Workspace => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", Folder);

    // ── the equations ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The hero FET's coefficients, on the SDD's own line.</summary>
    private const string Hero = "Periphery_mm=1.25 Sv=-0.837 Sc=0.71 TV0=4.268 TC=1.507 th=0.001 a=0.176 g=0.089 lam=0.0012 B=1130";

    /// <summary>The drain current: _v1 is Vgs, _v2 is Vds.</summary>
    private const string Ids = "Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0-_v1+_v2*th+Sc*ln(exp(-(Sv-_v1)/Sc)+1)))+1))*ln(exp(-(2*TV0-2*_v1+2*_v2*th+2*Sc*ln(exp(-(Sv-_v1)/Sc)+1))/TC)+1)*(_v2*lam+1))/2";

    /// <summary>The gate's forward Schottky current, its exponential continued by its tangent past 40 so a Newton iterate that
    /// overshoots does not overflow.</summary>
    private const string Igs = "Periphery_mm*Isg*(if(_v1/Vtg<40,exp(_v1/Vtg),exp(40)*(1+_v1/Vtg-40))-1)";

    /// <summary>Gate-drain breakdown: current from drain to gate once Vdg passes Vbr.</summary>
    private const string Ibr = "Periphery_mm*Ibk*if((_v2-_v1-Vbr)/Vbk<40,exp((_v2-_v1-Vbr)/Vbk),exp(40)*(1+(_v2-_v1-Vbr)/Vbk-40))";
    private const string GateParams = "Isg=1e-10 Vtg=0.1 Ibk=1e-6 Vbr=150 Vbk=5";

    /// <summary>1D gate-source charge: Cgs(Vgs) = Cgsp + Cgs0/2·(1 + tanh(Kgs·(Vgs − Vgs0))), integrated.</summary>
    private const string Qgs1 = "Periphery_mm*(Cgsp*_v1+Cgs0/2*(_v1+ln(cosh(Kgs*(_v1-Vgs0)))/Kgs))";

    /// <summary>1D drain-source charge: Cds(Vds) = Cdsm + (Cds0 − Cdsm)·exp(−Vds/Vds0), integrated.</summary>
    private const string Qds1 = "Periphery_mm*(Cdsm*_v2+(Cds0-Cdsm)*Vds0*(1-exp(-_v2/Vds0)))";
    private const string CapParams = "Cgsp=0.3e-12 Cgs0=1.0e-12 Kgs=1.5 Vgs0=-3 Cdsm=0.2e-12 Cds0=0.5e-12 Vds0=10";

    /// <summary>2D gate charge: the 1D Cgs charge, falling as Vds rises (the channel pinches off at the drain end).</summary>
    private const string Qgs2 = "Periphery_mm*(Cgsp*_v1+Cgs0/2*(_v1+ln(cosh(Kgs*(_v1-Vgs0)))/Kgs)*(1+Dgs*tanh(-_v2/Vgsd)))";

    /// <summary>The gate-drain charge in the FET's own two port voltages: Cgd(Vdg) = Cgdm + (Cgd0 − Cgdm)·exp(−Vdg/Vgd0).</summary>
    private const string Qgd2 = "Periphery_mm*(Cgdm*(_v1-_v2)-(Cgd0-Cgdm)*Vgd0*(1-exp(-(_v2-_v1)/Vgd0)))";
    private const string Cap2Params = "Dgs=0.3 Vgsd=20 Cgdm=0.04e-12 Cgd0=0.15e-12 Vgd0=8";

    /// <summary>The same gate-drain charge as a ONE-port SDD across gate and drain, whose one port voltage _v1 is Vgd.</summary>
    private const string Qgd1Port = "Periphery_mm*(Cgdm*_v1-(Cgd0-Cgdm)*Vgd0*(1-exp(_v1/Vgd0)))";

    // ── the benches ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Loadpull and Pursuit example's bench around a given FET: the source and load tuners with their bias tees,
    /// 3 dB compression over the polar grid at 2 GHz.</summary>
    private static string Loadpull(string fet) => $"""
        VGS = -3.05
        VDS = 48
        RFfreq = 2 GHz
        Tuner:SourceTuner1  Vin  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=VGS  Z[1]=50  Z[2]=1e-6  Z[3]=1e-6
        {fet}
        Tuner:LoadTuner1  Vout  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=VDS  Z[1]=50  Z[2]=1e-6  Z[3]=1e-6
        analysis LP1 type=loadpull Tone="RFfreq" ToneUnit=GHz MaxHarm=5 LoadTuner=LoadTuner1 SourceTuner=SourceTuner1 Grid="load_grid.gam" Sweep=Load TuneHarm=1 Compression=3 GainType=Gt PinStart=-10 PinStep=1 PinMax=36 Tickle=-50 MaxIter=100 FFTOverSample=1 Tol=1e-6 DriveStepping=IfNecessary GuardHarmonic=0
        labelednets Vin Vout
        """;

    public static readonly string Bench1 = Loadpull($"SDD:FET  Vin  0  Vout  0  {Hero}  I[1,0]=_v1/50  I[2,0]={Ids}");

    public static readonly string Bench2 = Loadpull($"SDD:FET  Vin  0  Vout  0  {Hero}  {GateParams}  I[1,0]=_v1/50+{Igs}-{Ibr}  I[2,0]={Ids}+{Ibr}");

    public static readonly string Bench3 = Loadpull($"SDD:FET  Vin  0  Vout  0  {Hero}  {GateParams}  {CapParams}  I[1,0]=_v1/50+{Igs}-{Ibr}  I[2,0]={Ids}+{Ibr}  I[1,1]={Qgs1}  I[2,1]={Qds1}");

    public static readonly string Bench4 = Loadpull($"SDD:FET  Vin  0  Vout  0  {Hero}  {GateParams}  {CapParams}  {Cap2Params}  I[1,0]=_v1/50+{Igs}-{Ibr}  I[2,0]={Ids}+{Ibr}  I[1,1]={Qgs2}+{Qgd2}  I[2,1]={Qds1}-({Qgd2})");

    public static readonly string Bench5 = Loadpull(
        $"SDD:FET  Vin  0  Vout  0  {Hero}  {GateParams}  {CapParams}  Dgs=0.3  Vgsd=20  I[1,0]=_v1/50+{Igs}-{Ibr}  I[2,0]={Ids}+{Ibr}  I[1,1]={Qgs2}  I[2,1]={Qds1}\n" +
        $"SDD:CGD  Vin  Vout  Periphery_mm=1.25  Cgdm=0.04e-12  Cgd0=0.15e-12  Vgd0=8  I[1,1]={Qgd1Port}");

    /// <summary>The bias loop: bench 5's FET with its own bias tees, the gate fed through a choke from an SDD that reads the
    /// drain supply's current (C[1]=IDD) and sets its node to VGS0 − Vspan·tanh(Kloop·(Idd − Iset)/Vspan): proportional
    /// control within ±Vspan of the nominal bias. DC, swept over a threshold shift dVt with the loop open and closed.</summary>
    public static readonly string Bench6 = $"""
        VGS0 = -3.05
        VDS = 48
        Iset = 0.0614
        Kloop = 200
        dVt = 0
        RFfreq = 2 GHz
        Pin = 0
        P1Tone:SRC  src  0  Num=1  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq
        C:CBG  src  g  C=100 pF
        L:LG  vg  g  L=100 nH
        SDD:BIAS  vg  0  I[1,0]=(_v1-VGS0-Vspan*tanh(Kloop*(Iset-_c1)/Vspan))/Rc  C[1]=IDD  Rc=1e-3  Vspan=1
        SDD:FET  g  0  d  0  {Hero.Replace("TV0=4.268", "TV0=4.268+dVt", StringComparison.Ordinal)}  {GateParams}  {CapParams}  Dgs=0.3  Vgsd=20  I[1,0]=_v1/50+{Igs}-{Ibr}  I[2,0]={Ids}+{Ibr}  I[1,1]={Qgs2}  I[2,1]={Qds1}
        SDD:CGD  g  d  Periphery_mm=1.25  Cgdm=0.04e-12  Cgd0=0.15e-12  Vgd0=8  I[1,1]={Qgd1Port}
        Vdc:VDD  vdd  0  Vdc=VDS V
        IProbe:IDD  vdd  vds
        L:LD  vds  d  L=100 nH
        C:CBD  d  o  C=100 pF
        R:RL  o  0  R=50 Ohm
        analysis DC1 type=dc
        analysis SHIFT type=parametric_sweep Var=dVt Values=-0.3,0,0.3 Inner=DC1
        analysis LOOP type=parametric_sweep Var=Kloop Values=0,200 Inner=SHIFT
        measure Idq_mA = 1000*DC1.I("IDD")
        measure Vgs_V = DC1.V("g")
        labelednets g d vg
        """;

    public static readonly (string Cell, string Netlist, bool Loadpull)[] Benches =
    [
        ("1 Drain Current", Bench1, true),
        ("2 Gate Current", Bench2, true),
        ("3 Charges (1D)", Bench3, true),
        ("4 Charges (2D)", Bench4, true),
        ("5 Cgd as an SDD Block", Bench5, true),
        ("6 Bias Loop (control current)", Bench6, false),
    ];

    // ── building ───────────────────────────────────────────────────────────────────────────────────────────────────

    [AuthorFact]
    public void BuildTheWorkspace()
    {
        string examples = Path.Combine(PalaceBackendTests.RepoRoot(), "examples");
        string ws = Workspace;
        string readme = Path.Combine(ws, "README.md");
        string? kept = File.Exists(readme) ? File.ReadAllText(readme) : null;
        if (Directory.Exists(ws)) Directory.Delete(ws, true);
        WorkspaceCreate.Create(examples, Folder, null);
        if (kept is not null) File.WriteAllText(readme, kept);

        // the Loadpull and Pursuit example's polar grid with a denser patch around the optima, at the workspace root where every
        // bench names it
        File.WriteAllText(Path.Combine(ws, "load_grid.gam"), Grid());

        foreach (var (cell, netlist, loadpull) in Benches)
        {
            ModelComposerExampleAuthoring.Draw(ws, cell, netlist, null);
            if (loadpull) File.WriteAllText(Path.Combine(ws, cell + ".cdd"), Display(cell));
        }
    }

    /// <summary>
    /// The load grid: the Loadpull and Pursuit example's polar grid (|Γ| = 0, 0.2, 0.4, 0.6, 0.75 at 30° steps) and, where every
    /// bench's MXP and MXE fall (|Γ| 0.13 to 0.42, −5° to 45°), rings at 0.1, 0.15, 0.25, 0.3 and 0.35 from −30° to 75° every 15°
    /// and the 15° points between on the 0.2 and 0.4 rings. On the coarse grid alone the interpolated MXP sat off the centre of
    /// the innermost Pout contour.
    /// </summary>
    public static string Grid()
    {
        var pts = new List<(double Mag, int Deg)> { (0, 0) };
        foreach (double m in new[] { 0.2, 0.4, 0.6, 0.75 })
            for (int a = 0; a < 360; a += 30) pts.Add((m, a));
        foreach (double m in new[] { 0.1, 0.15, 0.25, 0.3, 0.35 })
            for (int a = -30; a <= 75; a += 15) pts.Add((m, (a + 360) % 360));
        foreach (double m in new[] { 0.2, 0.4 })
            foreach (int a in new[] { 345, 15, 45, 75 }) pts.Add((m, a));
        var sb = new System.Text.StringBuilder();
        sb.Append("# gamma Z0=50 mag_ang\n");
        sb.Append("# The Loadpull and Pursuit example's polar grid (|Gamma| = 0, 0.2, 0.4, 0.6, 0.75 at 30 degrees), and a denser\n");
        sb.Append("# patch where this example's optima fall: |Gamma| 0.1 to 0.35 from -30 to 75 degrees every 15, and the 15-degree\n");
        sb.Append("# points between on the 0.2 and 0.4 rings.\n");
        foreach (var (m, a) in pts)
            sb.Append(System.FormattableString.Invariant($"{m:0.00}  {a}\n"));
        return sb.ToString();
    }

    /// <summary>
    /// A loadpull bench's display, which Simulate opens: the Pout contours with MXP marked and the PAE contours with MXE
    /// marked on one Smith chart, at 3 dB compression, and a summary table at each optimum.
    /// </summary>
    public static string Display(string cell)
    {
        string src = cell + ".npy";
        JsonObject Contour(string metric, double start, double step, double stop, uint colour, bool mxp) => new()
        {
            ["SourcePath"] = src, ["Label"] = metric,
            ["ContourTrace"] = new JsonObject
            {
                ["MetricName"] = metric, ["ConstraintKind"] = "Compression", ["ConstraintValue"] = 3.0, ["LevelMode"] = "Range",
                ["LevelStart"] = start, ["LevelStep"] = step, ["LevelStop"] = stop, ["ShowIsoLines"] = true, ["ShowFill"] = false,
                ["DrawLabels"] = true, ["LineColor"] = colour, ["LineColorOverridden"] = true, ["StrokeWidth"] = 1.5,
                ["DisplayMxp"] = mxp, ["DisplayMxe"] = !mxp, ["LabelBackground"] = 2348876033u,
            },
        };
        JsonObject Column(string kind, string metric, string header) => new()
        {
            ["SourcePath"] = src, ["SummaryColumn"] = new JsonObject { ["Kind"] = kind, ["MetricName"] = metric, ["Header"] = header },
        };
        JsonObject Table(string optimum, int top, string title) => new()
        {
            ["Left"] = 580, ["Top"] = top, ["Width"] = 560, ["Height"] = 90, ["PlotType"] = "Table",
            ["CustomTitle"] = title, ["CustomTitleOn"] = true, ["TableOptimum"] = optimum, ["TableReadMode"] = "Interp",
            ["TableCompression"] = 3,
            ["Traces"] = new JsonArray(Column("Metric", "Pout_dBm", "Pout (dBm)"), Column("Metric", "Gt_dB", "Gt (dB)"),
                                       Column("Metric", "PAE", "PAE (%)"), Column("Zload", "", "ZL (Ω)")),
        };
        var doc = new JsonObject
        {
            ["FormatVersion"] = 2, ["SelectedDataSource"] = src, ["ActiveTabIndex"] = 0,
            ["Tabs"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Loadpull",
                ["Plots"] = new JsonArray(
                    new JsonObject
                    {
                        ["Left"] = 0, ["Top"] = 0, ["Width"] = 560, ["Height"] = 560, ["PlotType"] = "Smith",
                        ["CustomTitle"] = "Pout (dBm, MXP marked) and PAE (%, MXE marked) at 3 dB compression", ["CustomTitleOn"] = true,
                        ["Legend"] = "Inside",
                        ["Traces"] = new JsonArray(Contour("Pout_dBm", 38, 1, 41, 4284111450u, true),
                                                   Contour("PAE", 50, 5, 65, 4288309802u, false)),
                    },
                    Table("Mxp", 0, "At maximum power (MXP)"),
                    Table("Mxe", 110, "At maximum efficiency (MXE)")),
            }),
        };
        return doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
