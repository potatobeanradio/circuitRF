// ================================================================
//  TransientRunner.cs  —  resolve a `tran` directive, run it, pack the DataSet
//  (docs/design/transient.md §3; brief-trn-1)
//
//  Cubes:  V [time, node]  (V)     every labelled net (every net when none is)
//          I [time, branch] (A)    every IProbe, first node to second
//          P [time, branch] (W)    v·i at the probe
//          Vprobe [time, branch] (V) the voltage at the probe's first node
//          OP.V [node]             the starting state
//          Steps.time, Steps.V     the accepted steps, with SaveSteps=yes
//          Tran.AcceptedSteps, Tran.RejectedSteps, Tran.NewtonIterations,
//          Tran.Factorizations,
//          Tran.DeviceEvaluations  counters (the gates' evidence, never timings)
//          __LabeledNodes          as every other analysis has it
//          __TranFundamental_Hz    the lowest source carrier (or pulse rate), for the preset's spectrum
// ================================================================

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;

namespace CircuitRF.Engine.Transient;

public static class TransientRunner
{
    /// <summary>The directive's text resolved against <paramref name="globals"/>: every value in base SI. Refuses a
    /// malformed value, and a key this solver does not take, by name.</summary>
    public static TransientParams Resolve(TransientAnalysis a, IReadOnlyDictionary<string, Value> globals)
    {
        string who = $"transient '{a.Name}'";
        double Num(string key, string expr, double def)
        {
            if (string.IsNullOrWhiteSpace(expr) || expr.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return def;
            try
            {
                var v = HbEngine.BuildEvaluator(globals).EvalExpr(Parser.Parse(expr.Trim()), HbEngine.BuildScope(globals));
                double d = v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real;
                if (!double.IsFinite(d)) throw new InvalidOperationException("not a finite number");
                return d;
            }
            catch (Exception ex) when (ex is not TransientRefusedException)
            {
                throw new TransientRefusedException($"{who}: {key}={expr} cannot be evaluated: {ex.Message}");
            }
        }

        string solver = a.Solver.Trim().ToLowerInvariant();
        if (solver is not (TransientAnalysis.SolverNative or TransientAnalysis.SolverNgspice or ""))
            throw new TransientRefusedException($"{who}: Solver must be native or ngspice, not '{a.Solver}'.");
        bool ngspice = solver == TransientAnalysis.SolverNgspice;
        // the ngspice-only keys: refused with the native solver, which reads none of them; the deck writer reads them for ngspice
        if (!ngspice)
        foreach (var (key, value, def) in new[]
                 {
                     ("SpiceOptions", a.SpiceOptions, ""), ("SpiceLines", a.SpiceLines, TransientAnalysis.SpiceLinesDefault),
                     ("SpiceLineFreq", a.SpiceLineFreq, TransientAnalysis.SpiceLineFreqDefault), ("SpiceFit", a.SpiceFit, TransientAnalysis.SpiceFitDefault),
                 })
            if (!value.Trim().Equals(def, StringComparison.OrdinalIgnoreCase))
                throw new TransientRefusedException($"{who}: {key} is read by the ngspice solver only; the native solver takes no {key}.");

        if (string.IsNullOrWhiteSpace(a.StopExpr)) throw new TransientRefusedException($"{who}: Stop is required.");
        double stop = Num("Stop", a.StopExpr, 0);
        if (!(stop > 0)) throw new TransientRefusedException($"{who}: Stop must be a positive time.");
        double startS = Num("Start", a.StartExpr, 0);
        if (startS < 0) throw new TransientRefusedException($"{who}: Start must not be negative; the run starts at t = 0.");
        if (startS >= stop) throw new TransientRefusedException($"{who}: Start must be before Stop.");
        double step = Num("Step", a.StepExpr, stop / 1000);
        if (!(step > 0)) throw new TransientRefusedException($"{who}: Step must be a positive time.");
        // a Step longer than the record reported only its two ends, and every measure over them read garbage with no message
        if (step > (stop - startS) * (1 + 1e-9))
            throw new TransientRefusedException(string.Create(CultureInfo.InvariantCulture,
                $"{who}: Step ({step:G4} s) is longer than the record it reports, Start to Stop ({stop - startS:G4} s)."));
        double maxStep = Num("MaxStep", a.MaxStepExpr, 0);
        if (maxStep < 0) throw new TransientRefusedException($"{who}: MaxStep must be a positive time or auto.");
        double relTol = Num("RelTol", a.RelTolExpr, 1e-3), vTol = Num("VTol", a.VTolExpr, 1e-6), iTol = Num("ITol", a.ITolExpr, 1e-12);
        if (!(relTol > 0) || !(vTol > 0) || !(iTol > 0)) throw new TransientRefusedException($"{who}: RelTol, VTol and ITol must be positive.");
        double maxSteps = Num("MaxSteps", a.MaxStepsExpr, 1e7);
        if (!(maxSteps >= 1)) throw new TransientRefusedException($"{who}: MaxSteps must be at least 1.");

        var method = a.Method.Trim().ToLowerInvariant() switch
        {
            TransientAnalysis.MethodTrap or "" => TransientMethod.Trap,
            TransientAnalysis.MethodGear2 => TransientMethod.Gear2,
            _ => throw new TransientRefusedException($"{who}: Method must be trap or gear2, not '{a.Method}'."),
        };
        var start = a.InitialState.Trim().ToLowerInvariant() switch
        {
            TransientAnalysis.InitialOp or "" => TransientStart.Op,
            TransientAnalysis.InitialZero => TransientStart.Zero,
            _ => throw new TransientRefusedException($"{who}: InitialState must be op or zero, not '{a.InitialState}'."),
        };
        var ic = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in a.IcExpr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = item.IndexOf('=');
            if (eq <= 0) throw new TransientRefusedException($"{who}: IC entries are net=value, not '{item}'.");
            ic[item[..eq].Trim()] = Num("IC", item[(eq + 1)..], 0);
        }
        return new TransientParams
        {
            Name = a.Name, StopS = stop, StepS = step, StartS = startS, MaxStepS = maxStep, Method = method,
            RelTol = relTol, VTol = vTol, ITol = iTol, Start = start, IcNets = ic, SaveSteps = a.SaveStepsOn,
            MaxSteps = (long)Math.Min(maxSteps, long.MaxValue / 2),
            Solver = ngspice ? TransientSolver.Ngspice : TransientSolver.Native, Directive = a,
        };
    }

    /// <summary>Runs <paramref name="p"/> on <paramref name="nl"/> and packs the result.</summary>
    /// <remarks>brief TRN-7 — with <c>Solver=ngspice</c>, <see cref="NgspiceRunner.Run"/> solves it instead, its deck under
    /// <see cref="AnalysisSettings.OutputDirectory"/>; with no ngspice discovered that is a refusal, never a native run.</remarks>
    public static DataSet Run(TransientParams p, ElaboratedNetlist nl, AnalysisSettings? settings = null, RunControl? control = null)
        => p.Solver == TransientSolver.Ngspice
            ? NgspiceRunner.Run(p, nl, settings?.OutputDirectory, control, settings?.BenchName ?? "", settings?.ProgramVersion ?? "")
            : Pack(new TransientEngine(nl, settings).Run(p, control), nl);

    /// <summary>The DataSet of one run.</summary>
    public static DataSet Pack(TransientRunResult r, ElaboratedNetlist nl)
    {
        var ds = new DataSet();
        int nt = r.Time.Length, nn = r.NodeNames.Length, np = r.ProbeNames.Length;
        var time = new Axis("time", r.Time, "s");
        var nodeAxis = new Axis("node", Index(nn), "V", r.NodeNames);
        if (nn > 0)
        {
            var v = new double[nt * nn];
            for (int t = 0; t < nt; t++) Array.Copy(r.V[t], 0, v, t * nn, nn);
            ds.Add("V", new DataCube([time, nodeAxis], v));
            ds.Add("OP.V", new DataCube([nodeAxis], (double[])r.OpV.Clone()));
        }
        if (np > 0)
        {
            var branch = new Axis("branch", Index(np), "A", r.ProbeNames);
            var i = new double[nt * np];
            var pw = new double[nt * np];
            var vp = new double[nt * np];
            for (int t = 0; t < nt; t++)
            {
                Array.Copy(r.I[t], 0, i, t * np, np); Array.Copy(r.P[t], 0, pw, t * np, np);
                if (r.VProbe.Length > t) Array.Copy(r.VProbe[t], 0, vp, t * np, np);
            }
            ds.Add("I", new DataCube([time, branch], i));
            ds.Add("P", new DataCube([time, new Axis("branch", Index(np), "W", r.ProbeNames)], pw) { Unit = "W" });
            ds.Add("Vprobe", new DataCube([time, new Axis("branch", Index(np), "V", r.ProbeNames)], vp) { Unit = "V" });
            ds.Add("__ProbeBranches", new DataCube([new Axis("probe", Index(np), "", r.ProbeNames)], new double[np]));
        }
        if (r.StepTime is { } st && r.StepV is { } sv && nn > 0)
        {
            var steps = new double[st.Length * nn];
            for (int k = 0; k < st.Length; k++) Array.Copy(sv[k], 0, steps, k * nn, nn);
            ds.Add("Steps.time", new DataCube([new Axis("step", Index(st.Length))], (double[])st.Clone()) { Unit = "s" });
            ds.Add("Steps.V", new DataCube([new Axis("step", Index(st.Length)), nodeAxis], steps));
        }
        ds.Add("Tran.AcceptedSteps", DataCube.Scalar(r.AcceptedSteps));
        ds.Add("Tran.RejectedSteps", DataCube.Scalar(r.RejectedSteps));
        ds.Add("Tran.NewtonIterations", DataCube.Scalar(r.NewtonIterations));
        ds.Add("Tran.Factorizations", DataCube.Scalar(r.Factorizations));
        ds.Add("Tran.DeviceEvaluations", DataCube.Scalar(r.DeviceEvaluations));
        ds.Add("__TranMaxStep_s", DataCube.Scalar(r.MaxStepS));
        // brief TRN-5 — the frequency the Transient preset reads the output's spectrum at, when a source has one
        if (TransientSources.Fundamental(nl) is double f0) ds.Add(FundamentalCube, DataCube.Scalar(f0));
        var labeled = r.NodeNames.Where(nm => nl.Nodes.LabeledNames.Contains(nm)).Distinct().ToArray();
        if (labeled.Length > 0)
            ds.Add("__LabeledNodes", new DataCube([new Axis("label", Index(labeled.Length), "", labeled)], new double[labeled.Length]));
        foreach (var note in r.Notes) nl.AddNoteOnce("tran:" + note, note);
        return ds;
    }

    /// <summary>The metadata cube holding the bench's fundamental (Hz): the lowest carrier a source plays, else a repeating pulse's
    /// rate (<see cref="TransientSources.Fundamental"/>). Absent when there is neither.</summary>
    public const string FundamentalCube = "__TranFundamental_Hz";

    /// <summary>One line describing a resolved run, for the CLI and the run panel.</summary>
    public static string Describe(TransientParams p) => string.Format(CultureInfo.InvariantCulture,
        "transient {0}: Stop {1:G4} s, Step {2:G4} s, {3}, start {4}{5}", p.Name, p.StopS, p.StepS > 0 ? p.StepS : p.StopS / 1000,
        p.Method == TransientMethod.Trap ? "trap" : "gear2", p.Start == TransientStart.Op ? "op" : "zero",
        p.Solver == TransientSolver.Ngspice ? ", solved by ngspice" : "");

    private static double[] Index(int n)
    {
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = i;
        return x;
    }
}
