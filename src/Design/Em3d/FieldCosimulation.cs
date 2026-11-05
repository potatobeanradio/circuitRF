// brief-sar-9 R-sar9-3 / R-sar9-4 / R-sar9-5 — a schematic's `fields` analysis: an EM Field Data block's per-port fields (named by
// its result record, kept by a 3D run with Sar.PerPortFields) recombined with the incident waves THIS circuit delivers to each of the
// block's ports, into the field — and the SAR and B1 — of the whole coil with its capacitors, matching network and feed.
// docs/design/sar.md §11.
//
// NO EM RUN. Everything here is one circuit solve at one frequency (SParameterEngine.BlockWaves) and a weighted sum of files,
// so changing a capacitor and running the analysis again costs seconds and starts no process (Runs counts the recombinations;
// OpenEmsRun.Invocations stays where it was — CosimNoEmRunTests holds both).
//
// THE WEIGHTS. The circuit's own drive is a unit incident wave at its driven port; each block port's incident wave a_k =
// (V_k + Z0_k·I_k)/(2·√Z0_k), from the voltage across it and the current into it, in the Z0 its fields were normalised in
// (the field set's manifest). Over the drive's wave that is the square root of a power ratio in any phasor convention, so the
// weights are those waves times √P, with P the available power the recombined field is written at:
// PalaceDrive.IncidentPowerW, the incident power every driven field in the 3D view is loaded at, so the view draws a
// recombined field with no scale of its own (its drive is the circuit's port and reflection).
//
// A FAR FIELD IS THE OTHER HALF, in FieldCosimulation.FarField.cs: same waves, and no SAR wording.
//
// AS SOLVED, THEN PER WATT. The volumes and the record's values are at P available; the record's AcceptedPowerW is the power
// the circuit's drive port accepts, P·(1 − |Γ|²), and its per-watt values divide by that (norm=accepted) or by P itself
// (norm=available, ReferencePowerW).

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d.Sar;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>brief-sar-9 — what a fields analysis did, beside its SAR record: the drive, the weights, the powers and the checks.</summary>
public sealed class CosimRecord
{
    public string Analysis { get; set; } = "";
    public string Block { get; set; } = "";
    public string Snp { get; set; } = "";
    public string Stamp { get; set; } = "";
    public double FrequencyHz { get; set; }
    public int DrivePort { get; set; }
    public double DriveZ0Re { get; set; }
    public double DriveZ0Im { get; set; }
    /// <summary>The drive port's reflection, S_jj of the circuit with every other circuit port terminated.</summary>
    public double ReflectionRe { get; set; }
    public double ReflectionIm { get; set; }
    /// <summary>brief-sar-10 R-sar10-4 — several circuit ports driven together: [port, incident re, im, reflected re, im] each, in
    /// the drive's own units (only ratios mean anything); null when one port drives.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<double[]>? Drives { get; set; }
    /// <summary>The block's ports, in the field set's order, and the incident and reflected waves the circuit gives them, √W.</summary>
    public List<int> Ports { get; set; } = [];
    public List<double[]> Incident { get; set; } = [];
    public List<double[]> Reflected { get; set; } = [];
    public double AvailablePowerW { get; set; }
    public double AcceptedPowerW { get; set; }
    /// <summary>Σ_k |a_k|² − |b_k|²: what the circuit delivers into the block.</summary>
    public double DeliveredPowerW { get; set; }
    /// <summary>The same from the field set's own S-matrix, aᴴ(I − SᴴS)a.</summary>
    public double DeliveredFromSW { get; set; }
    /// <summary>Absorbed in every tissue cell (not only a stated Region: the SAR record has that), from the summed field, and
    /// from the tissue matrix aᴴQa.</summary>
    public double AbsorbedPowerW { get; set; }
    public double AbsorbedFromQW { get; set; }
    /// <summary>Delivered minus absorbed: radiated plus conductor and any other loss.</summary>
    public double OtherLossW { get; set; }
    public string Normalisation { get; set; } = FieldsAnalysis.NormAccepted;
    public bool Averaged { get; set; }
    public List<string> Checks { get; set; } = [];

    public Complex Reflection => new(ReflectionRe, ReflectionIm);
    public Complex DriveZ0 => new(DriveZ0Re, DriveZ0Im);
}

/// <summary>
/// brief-cosim-pattern-2 R-cp2-7 / R-cp2-8 — Fields was pointed at a plain SnP. An exception of its own so a run can offer the change
/// beside the sentence; the sentence alone is the whole answer headlessly.
/// </summary>
public sealed class PlainSnpFieldsBlockException(string who, string block, string snpPath)
    : InvalidOperationException($"{who}: '{block}' is an SnP, and Fields reads its data from an {EmFieldDataRecord.DisplayName} block. Place one and pick " +
                                $"the same run's record, '{Path.ChangeExtension(Path.GetFileName(snpPath), EmFieldDataRecord.Extension)}', beside the Touchstone file; " +
                                "for a run older than records, its file picker offers to write one, as `circuitrf em --adopt` does.")
{
    public string Block { get; } = block;
    public string SnpPath { get; } = snpPath;
}

public static partial class FieldCosimulation
{
    /// <summary>The folder inside a field set the recombined results are written to, one subfolder per analysis.</summary>
    public const string CosimFolder = "cosim";

    /// <summary>The record beside a recombination's SAR record.</summary>
    public const string RecordFile = "cosim.json";

    /// <summary>The DataSet cubes a fields analysis adds besides the SAR group's own names (OpenEmsSarResults).</summary>
    public const string DeliveredCube = "Pdel", OtherLossCube = "Pother", IncidentCube = "a", ReflectedCube = "b",
                        ReflectionCube = "Gamma", PeakLocalCube = "SARpeak";

    /// <summary>R-sar9-5's tolerance: the power checks agree to this fraction of the delivered power.</summary>
    public const double PowerTolerance = 0.02;

    private static long _runs;

    /// <summary>Recombinations performed in this process — what CosimNoEmRunTests reads beside OpenEmsRun.Invocations.</summary>
    public static long Runs => Interlocked.Read(ref _runs);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The folder a recombination of analysis <paramref name="analysis"/> writes into, inside <paramref name="fieldsFolder"/>.</summary>
    public static string OutputFolder(string fieldsFolder, string analysis) => Path.Combine(fieldsFolder, CosimFolder, Safe(analysis));

    public static CosimRecord? ReadRecord(string folder)
    {
        string path = Path.Combine(folder, RecordFile);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<CosimRecord>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Runs <paramref name="fa"/> on <paramref name="nl"/>: the DataSet of its scalars (the caller groups it under the analysis's
    /// name), with the volumes, the SAR record and the co-simulation record written beside the field set. Throws
    /// <see cref="InvalidOperationException"/> with the refusal's sentence when it cannot run.
    /// <para>brief-sar-10 — <paramref name="output"/> says how much of that a caller wants (<see cref="FieldsOutput"/>);
    /// <paramref name="metrics"/>, when given, receives the recombined field for the field measurements, and owns the open
    /// field set from then on (the caller disposes it once the measurements are evaluated).</para>
    /// <para>brief-cosim-pattern-3 R-cp3-2 — the record decides what runs: a field set gives SAR and B1, a far field the pattern's cubes, both give both,
    /// and every run publishes the circuit's S and Z0. Nothing is written beside a result that has only a far field.</para>
    /// </summary>
    public static DataSet Run(FieldsAnalysis fa, ElaboratedNetlist nl, List<string> notes, RunControl? control = null,
                              List<CircuitRF.Design.Circuit.FieldsArtifact>? artifacts = null,
                              CircuitRF.Design.Circuit.FieldsOutput output = CircuitRF.Design.Circuit.FieldsOutput.Written,
                              string? label = null, Action<IFieldMetrics>? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(fa);
        ArgumentNullException.ThrowIfNull(nl);
        string who = $"Fields '{fa.Name}'";
        if (string.IsNullOrWhiteSpace(fa.Block)) throw new InvalidOperationException($"{who}: block= names no {EmFieldDataRecord.DisplayName} block.");
        bool every = fa.AtEveryFrequency;
        double hz = double.NaN;
        if (!every)
        {
            try { hz = FreqUnit.ResolveHz(fa.FreqExpr, fa.FreqUnit, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit); }
            catch (Exception e) { throw new InvalidOperationException($"{who}: freq='{fa.FreqExpr}' could not be resolved ({e.Message})."); }
        }
        var drives = Drives(fa, nl, who);

        var block = nl.Components.FirstOrDefault(c => string.Equals(c.InstancePath, fa.Block, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"{who}: the circuit has no instance '{fa.Block}'.");
        if (block.Model is not SnpModel snp)
            throw new InvalidOperationException($"{who}: '{fa.Block}' is a {block.ComponentType}, not an {EmFieldDataRecord.DisplayName} block.");
        // brief-cosim-pattern-2 R-cp2-7 — a Touchstone file says nothing of fields, so a plain SnP is refused. It is not converted:
        // no record exists to point at until the setup runs again or the file is adopted.
        if (snp.RecordPath is not { } recordPath)
            throw new PlainSnpFieldsBlockException(who, fa.Block, snp.FilePath);
        // brief-sens-2: the port fields are the FILE's structure's; offsets on its S describe one the field set does not hold.
        if (snp.OffsetsNotAppliedBy(block.InstancePath, "the field co-simulation, whose port fields are the file's own") is { } offsets)
            throw new InvalidOperationException($"{who}: {offsets}.");

        var plan = PlanOf(fa, recordPath, hz, who, out var far);
        var lines = new List<string>();
        var warnings = new List<string>();
        var data = new DataSet();
        CosimFieldMetrics? provider = null;
        PortFieldSet? set = null;
        bool handedOver = false;
        try
        {
            if (plan.FieldSet)
            {
                var (opened, refusal) = PortFieldSet.Open(recordPath, hz);
                set = opened ?? throw new InvalidOperationException($"{who}: {refusal}");
                if (set.PortCount != snp.PortCount)
                    throw new InvalidOperationException($"{who}: '{fa.Block}' has {snp.PortCount} ports and its field set {set.PortCount}.");
                control?.ThrowIfCancellationRequested();
                var waves = SParameterEngine.BlockWaves(nl, hz, fa.Block, drives);
                if (output == CircuitRF.Design.Circuit.FieldsOutput.OnDemand)
                {
                    // R-sar10-2 — an optimizer's, yield's or DOE's evaluation: the waves and the tissue matrix's absorbed power, nothing
                    // recombined; each field measurement reads only what it needs when an expression asks for it
                    var w = Weigh(fa, set, waves);
                    provider = new CosimFieldMetrics(fa, set, snp.FilePath, w, output, null, null);
                    data = DataSetOf(hz, w, set.Manifest, provider.AbsorbedAllW);
                    lines.Add($"Fields '{fa.Name}' at {MaterialDispersion.Hz(hz)}: the circuit's waves at '{fa.Block}', recombined only where a measurement reads them.");
                }
                else
                {
                    bool average = output == CircuitRF.Design.Circuit.FieldsOutput.Written && fa.Average;
                    string folder = label is null ? fa.Name : $"{fa.Name} ({label})";
                    (data, provider) = RecombineCore(fa, set, snp.FilePath, waves, lines, warnings, control, average, folder, output);
                    artifacts?.Add(new CircuitRF.Design.Circuit.FieldsArtifact(folder, C3dOf(set.Folder), set.Manifest.Setup, hz,
                                                                               OutputFolder(set.Folder, folder), set.HasH && set.Manifest.B1));
                }
            }
            double[] frequencies = [hz];
            if (plan.FarField.Count > 0)
            {
                if (far!.PortCount != snp.PortCount)
                    throw new InvalidOperationException($"{who}: '{fa.Block}' has {snp.PortCount} ports and its far field {far.PortCount}.");
                PublishFarField(fa, nl, far, plan.FarField, drives, data, lines, warnings, control);
                frequencies = [.. plan.FarField.Select(i => far.FrequenciesHz[i])];
                // R-cp3-2 — gain per watt accepted is what a pattern means, whatever a field set's numbers are per
                if (fa.PerAvailable)
                    lines[0] += " norm=available does not apply to the pattern: its gain stays per watt accepted, and its realized gain includes the mismatch.";
            }
            lines.AddRange(plan.Notes);
            Interlocked.Increment(ref _runs);
            AddCircuitS(data, nl, frequencies);

            // the detail as the run's notes (the Messages pane's Info), the first line as the analysis's summary
            for (int i = 0; i < lines.Count; i++) nl.AddNoteOnce($"fields:{fa.Name}:{i}", lines[i]);
            foreach (string warning in warnings) nl.AddWarning(warning);
            if (lines.Count > 0) notes.Add(lines[0]);
            if (metrics is not null)
            {
                metrics(provider is null ? new NoFieldSetMetrics(fa.Name, Path.GetFileName(recordPath)) : provider);
                handedOver = provider is not null;
            }
            return data;
        }
        finally
        {
            if (!handedOver) set?.Dispose();
        }
    }

    /// <summary>What one fields analysis does: run the field set, and at which of the far field's frequencies; and what it says about the half it cannot do.</summary>
    private sealed record FieldsPlan(bool FieldSet, IReadOnlyList<int> FarField, IReadOnlyList<string> Notes);

    /// <summary>
    /// R-cp3-2 / R-cp3-4 — which half of a result runs. A field set is at one frequency, a far field at every frequency the run solved, and
    /// a frequency between those is refused rather than interpolated: far-field phase across a coarse sweep is not safe to interpolate.
    /// </summary>
    private static FieldsPlan PlanOf(FieldsAnalysis fa, string recordPath, double hz, string who, out PortFarFieldSet? far)
    {
        var (record, refused) = EmFieldDataRecord.Open(recordPath);
        if (record is null) throw new InvalidOperationException($"{who}: {refused}");
        string name = Path.GetFileName(recordPath);
        bool hasSet = record.FieldsFolder is not null;
        far = null;
        var notes = new List<string>();
        if (record.FarFieldPoints > 0)
        {
            (far, string? farRefused) = PortFarFieldSet.Open(recordPath);
            // A field set does not depend on the far field beside it, so a run that can still do that half does it and says what it lost.
            if (far is null && !hasSet) throw new InvalidOperationException($"{who}: {farRefused}");
            if (far is null) notes.Add($"Fields '{fa.Name}': no pattern. {farRefused}");
        }
        double[] setHz = hasSet ? [.. (PortFieldSet.ReadManifest(record.FieldsFolder!)?.Frequencies ?? []).Select(f => f.Hz)] : [];

        if (fa.AtEveryFrequency)
        {
            if (far is null)
                throw new InvalidOperationException($"{who}: freq=all takes every frequency of a far field, and '{name}' holds a field set, whose SAR and B1 are " +
                                                    $"worked out at one frequency. Name one of {HzList(setHz)}.");
            if (hasSet) notes.Add($"Fields '{fa.Name}': freq=all recombines the far field only, since SAR and B1 are worked out at one frequency.");
            return new FieldsPlan(false, [.. Enumerable.Range(0, far.FrequenciesHz.Count)], notes);
        }

        // With no far field, PortFieldSet.Open's own refusal says what a frequency it does not hold means for a field set.
        if (far is null) return new FieldsPlan(true, [], notes);

        int at = far.IndexOf(hz);
        bool setHere = setHz.Any(f => Math.Abs(f - hz) <= 1e-9 * Math.Max(f, hz));
        if (at < 0 && !setHere)
            throw new InvalidOperationException($"{who}: '{name}' holds a pattern at {HzList(far.FrequenciesHz)} only" +
                                                (hasSet ? $" and a field set at {HzList(setHz)}" : "") +
                                                $", not at {MaterialDispersion.Hz(hz)}: a pattern exists only at frequencies the run solved, because its phase is not safe to " +
                                                "interpolate between them. Name one of these, or freq=all.");
        if (hasSet && !setHere) notes.Add($"Fields '{fa.Name}': no SAR or B1, since '{name}' holds a field set at {HzList(setHz)} only.");
        if (setHere && at < 0) notes.Add($"Fields '{fa.Name}': no pattern, since '{name}' holds one at {HzList(far.FrequenciesHz)} only.");
        return new FieldsPlan(setHere, at >= 0 ? [at] : [], notes);
    }

    private static string HzList(IReadOnlyList<double> hz) => string.Join(", ", hz.Select(MaterialDispersion.Hz));

    /// <summary>The recombination proper, once the circuit's waves at the block are known: what <see cref="Run"/> does after its
    /// circuit solve, and a test's entry with waves of its own.</summary>
    public static DataSet Recombine(FieldsAnalysis fa, PortFieldSet set, string snpPath, SParameterEngine.BlockPortDrive drive,
                                    List<string> notes, List<string> warnings, RunControl? control = null)
        => RecombineCore(fa, set, snpPath, drive, notes, warnings, control, fa.Average, fa.Name,
                         CircuitRF.Design.Circuit.FieldsOutput.Written).Data;

    /// <summary>brief-sar-10 — the block's waves for one excitation of the circuit, in each port's Z0, scaled so the drives
    /// together make <see cref="PalaceDrive.IncidentPowerW"/> available, and what the excitation accepts and reflects.</summary>
    internal sealed record CosimWaves(Complex[] A, Complex[] B, SParameterEngine.BlockPortDrive Drive, double AvailableW,
                                      double AcceptedW, Complex Reflection, double DivisorW, bool PerAvailable)
    {
        public double Accepts => AvailableW > 0 ? AcceptedW / AvailableW : 0;
        public bool Multi => Drive.Drives.Count > 1;
    }

    internal static CosimWaves Weigh(FieldsAnalysis fa, PortFieldSet set, SParameterEngine.BlockPortDrive drive)
        => Weigh(fa, [.. set.Manifest.Ports.Select(p => p.Z0)], drive);

    /// <summary>The one statement of the waves, in the Z0 each of the block's ports was solved in.</summary>
    internal static CosimWaves Weigh(FieldsAnalysis fa, IReadOnlyList<Complex> z0s, SParameterEngine.BlockPortDrive drive)
    {
        int n = z0s.Count;
        double pAvail = PalaceDrive.IncidentPowerW;
        double incident = drive.Drives.Sum(d => d.Incident.Magnitude * d.Incident.Magnitude);
        double reflected = drive.Drives.Sum(d => d.Reflected.Magnitude * d.Reflected.Magnitude);
        if (!(incident > 0)) throw new InvalidOperationException($"Fields '{fa.Name}': every drive's weight is zero, so nothing is driven.");
        // the drives together make P available: one unit drive is the SAR-9 scale √P, several share it
        double root = Math.Sqrt(pAvail / incident);
        var a = new Complex[n];
        var b = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            Complex z0 = z0s[k];
            Complex sq = RfCore.RFNetwork.ComplexSqrt(z0);
            a[k] = (drive.Voltage[k] + z0 * drive.Current[k]) / (2 * sq) * root;
            b[k] = (drive.Voltage[k] - z0 * drive.Current[k]) / (2 * sq) * root;
        }
        double accepts = 1 - reflected / incident;
        // one drive: its own S_jj; several: the reflection a single drive accepting the same fraction would have
        Complex gamma = drive.Drives.Count == 1 ? drive.Reflection : new Complex(Math.Sqrt(Math.Max(0, 1 - accepts)), 0);
        double pAcc = pAvail * accepts;
        return new CosimWaves(a, b, drive, pAvail, pAcc, gamma, fa.PerAvailable ? pAvail : pAcc, fa.PerAvailable);
    }

    private static (DataSet Data, CosimFieldMetrics Metrics) RecombineCore(
        FieldsAnalysis fa, PortFieldSet set, string snpPath, SParameterEngine.BlockPortDrive drive, List<string> notes,
        List<string> warnings, RunControl? control, bool average, string folderName, CircuitRF.Design.Circuit.FieldsOutput output)
    {
        var m = set.Manifest;
        int n = set.PortCount;
        double hz = set.Frequency.Hz;
        var w = Weigh(fa, set, drive);
        var (a, b) = (w.A, w.B);
        double pAvail = w.AvailableW, pAcc = w.AcceptedW, accepts = w.Accepts;
        double pDel = Enumerable.Range(0, n).Sum(k => a[k].Magnitude * a[k].Magnitude - b[k].Magnitude * b[k].Magnitude);
        var settingsB1 = SarSettings.ParseB0Axis(m.B0Axis) ?? (2, 1);
        var method = Enum.TryParse<SarMethod>(m.Method, out var meth) ? meth : SarMethod.Cube;

        // ── the summed field, then SAR-5's engine on it ─────────────────────────────────────────────
        var field = FieldRecombination.Recombine(set, a, m.B1, settingsB1.Item1, settingsB1.Item2, control: control);
        short[] owners = set.ReadOwners();
        var regionSet = m.Region.ToHashSet(StringComparer.Ordinal);
        bool[] Mask(Func<string, bool> keep)
        {
            var mask = new bool[owners.Length];
            for (int c = 0; c < mask.Length; c++) mask[c] = owners[c] >= 0 && owners[c] < m.Tissues.Count && keep(m.Tissues[owners[c]]);
            return mask;
        }
        var region = m.RegionStated ? SarRegion.Mask(Mask(regionSet.Contains), m.RegionName) : SarRegion.All;
        var result = SarEngine.Compute(field.Grid, new SarOptions
        {
            MassesGrams = average ? m.MassesGrams : [], Method = method, Region = region,
        }, control);

        // ── R-sar9-5 — the power checks ──────────────────────────────────────────────────────────────
        var s = set.Frequency.SMatrix();
        var q = set.Frequency.QMatrix();
        var loss = Dissipation(s);
        double pDelS = FieldRecombination.QuadraticPower(loss, a);
        double pAbsQ = FieldRecombination.QuadraticPower(q, a);
        // Q is over every tissue cell, so it is checked against every tissue cell's absorption, not a stated Region's
        double pAbsAll = 0;
        for (int c = 0; c < field.Grid.Count; c++)
            if (field.Grid.Density[c] > 0) pAbsAll += (double)field.Grid.PowerDensity[c] * field.Grid.Volume[c];
        var checks = Checks(pAbsAll, pAbsQ, pDel, pDelS, loss, q, set.Frequency, out var failed);
        warnings.AddRange(failed.Select(x => $"Fields '{fa.Name}': {x}"));

        // ── the record, the volumes, the co-simulation record ────────────────────────────────────────
        string outDir = OutputFolder(set.Folder, folderName);
        Directory.CreateDirectory(outDir);
        var grid = field.Grid;
        int[] shape = [grid.Nx, grid.Ny, grid.Nz];
        var record = new SarRunRecord
        {
            Port = drive.DrivenPort, FrequencyHz = hz, Index = 1, AcceptedPowerW = pAcc,
            ReferencePowerW = fa.PerAvailable ? pAvail : null,
            Region = result.Region, AbsorbedPowerW = result.AbsorbedPowerW, TissueMassKg = result.TissueMassKg,
            UncountedPowerW = result.UncountedPowerW, Shape = shape, Grid = m.Grid,
        };
        // brief-sar-8b — a cylindrical set averages no cube (its run had no masses); the analysis says so rather than nothing.
        if (m.IsCylindrical && average)
            notes.Add($"Fields '{fa.Name}': the field set is on a cylindrical grid, so SAR is local only (a cube is a Cartesian grid's); " +
                      "its volumes are (ρ, α, z) cells, which the 3D view does not draw.");
        average &= m.MassesGrams.Count > 0;
        WriteVolume(outDir, SarVolumeFiles.LocalQuantity, result.LocalSar, shape, record);
        if (result.LocalPeak is { } lp) record.Peaks.Add(Peak(SarVolumeFiles.LocalQuantity, lp, grid, owners, m));
        foreach (var avg in result.Averages)
        {
            string name = SarSettings.AveragedName(avg.MassGrams);
            WriteVolume(outDir, name, avg.Sar, shape, record);
            if (avg.Peak is { } p) record.Peaks.Add(Peak(name, p, grid, owners, m));
        }
        if (field.B1Plus is { } plus && field.B1Minus is { } minus)
        {
            WriteVolume(outDir, SarVolumeFiles.B1PlusQuantity, plus, shape, record);
            WriteVolume(outDir, SarVolumeFiles.B1MinusQuantity, minus, shape, record);
            record.B0Axis = m.B0Axis;
            record.B1Region = m.B1RegionName;
            var b1Set = m.B1Region.ToHashSet(StringComparer.Ordinal);
            var b1Mask = Mask(b1Set.Contains);
            double sum = 0, vol = 0;
            for (int c = 0; c < b1Mask.Length; c++)
                if (b1Mask[c]) { sum += plus[c] * (double)grid.Volume[c]; vol += grid.Volume[c]; }
            record.B1pMeanT = vol > 0 ? sum / vol : null;
        }
        SarVolumeFiles.WriteFloat64(Path.Combine(outDir, SarVolumeFiles.LinesFile(0, 'x')), Centres(grid.X));
        SarVolumeFiles.WriteFloat64(Path.Combine(outDir, SarVolumeFiles.LinesFile(0, 'y')), Centres(grid.Y));
        SarVolumeFiles.WriteFloat64(Path.Combine(outDir, SarVolumeFiles.LinesFile(0, 'z')), Centres(grid.Z));
        SarVolumeFiles.WriteRecord(Path.Combine(outDir, SarVolumeFiles.RecordFile(0)), record);

        var cosim = new CosimRecord
        {
            Analysis = folderName, Block = fa.Block, Snp = Path.GetFileName(snpPath), Stamp = m.Stamp, FrequencyHz = hz,
            DrivePort = drive.DrivenPort, DriveZ0Re = drive.DrivenZ0.Real, DriveZ0Im = drive.DrivenZ0.Imaginary,
            ReflectionRe = w.Reflection.Real, ReflectionIm = w.Reflection.Imaginary,
            Drives = w.Multi ? [.. drive.Drives.Select(d => new[] { d.Port, d.Incident.Real, d.Incident.Imaginary, d.Reflected.Real, d.Reflected.Imaginary })] : null,
            Ports = [.. m.Ports.Select(p => p.Number)],
            Incident = [.. a.Select(x => new[] { x.Real, x.Imaginary })], Reflected = [.. b.Select(x => new[] { x.Real, x.Imaginary })],
            AvailablePowerW = pAvail, AcceptedPowerW = pAcc, DeliveredPowerW = pDel, DeliveredFromSW = pDelS,
            AbsorbedPowerW = pAbsAll, AbsorbedFromQW = pAbsQ, OtherLossW = pDel - pAbsAll,
            Normalisation = fa.PerAvailable ? FieldsAnalysis.NormAvailable : FieldsAnalysis.NormAccepted, Averaged = average,
            Checks = checks,
        };
        File.WriteAllText(Path.Combine(outDir, RecordFile), JsonSerializer.Serialize(cosim, Json));

        // ── what the run says, and the DataSet ───────────────────────────────────────────────────────
        string per = fa.PerAvailable ? "per 1 W available" : "per 1 W accepted";
        string drivenBy = w.Multi
            ? $"driven from circuit ports {string.Join(", ", drive.Drives.Select(d => d.Port))} together ({Pct(accepts)} of their available power accepted)"
            : $"driven from circuit port {drive.DrivenPort} (|Γ| {Db(drive.Reflection.Magnitude)} dB, {Pct(accepts)} of the available power accepted)";
        notes.Add($"Fields '{fa.Name}' at {MaterialDispersion.Hz(hz)}: '{fa.Block}' recombined from {field.Counts.PortsUsed} of {n} port fields " +
                  $"({field.Counts.CellsRead:N0} cells), {drivenBy}, {per}" +
                  (output == CircuitRF.Design.Circuit.FieldsOutput.Local ? "; local SAR and B1 only while the knob moves." : "."));
        notes.Add(OpenEmsSarResults.Readout(record).Replace(OpenEmsSarResults.Normalisation, per, StringComparison.Ordinal)
                  .Replace($"port {record.Port} driven", w.Multi ? "circuit ports driven together" : $"circuit port {record.Port} driven", StringComparison.Ordinal));
        notes.Add($"Fields '{fa.Name}': {G(pDel / Math.Max(pAcc, double.Epsilon) * 100)} % of the accepted power reaches the block; of that, " +
                  $"{Pct(pAbsAll / Math.Max(pDel, double.Epsilon))} is absorbed in tissue and " +
                  $"{Pct(cosim.OtherLossW / Math.Max(pDel, double.Epsilon))} is radiated or lost in conductors.");
        notes.AddRange(checks);
        notes.Add($"Fields '{fa.Name}': the results are beside the field set, in '{Path.GetRelativePath(Path.GetDirectoryName(snpPath) ?? "", outDir)}'.");

        var provider = new CosimFieldMetrics(fa, set, snpPath, w, output, field.Grid, average ? result : null);
        return (DataSetOf(record, cosim, m, a, b, w.Reflection), provider);
    }

    /// <summary>brief-sar-10 R-sar10-4 — the circuit ports <paramref name="fa"/> drives and the incident-wave amplitude of each:
    /// <c>drive=</c>'s port numbers (or expressions over the globals), <c>weights=</c>'s complex expressions in the same order.</summary>
    internal static List<(int Port, Complex Amplitude)> Drives(FieldsAnalysis fa, ElaboratedNetlist nl, string who)
    {
        var ports = SplitList(fa.DriveExpr.Trim().Length == 0 ? "1" : fa.DriveExpr);
        var weights = SplitList(fa.WeightsExpr);
        if (weights.Count > 0 && weights.Count != ports.Count)
            throw new InvalidOperationException($"{who}: drive='{fa.DriveExpr}' names {ports.Count} port{(ports.Count == 1 ? "" : "s")} and " +
                                                $"weights='{fa.WeightsExpr}' {weights.Count} weight{(weights.Count == 1 ? "" : "s")}: give one weight a port.");
        var eval = new Evaluator();
        var scope = new Scope("globals");
        foreach (var (name, value) in nl.ResolvedGlobals)
        {
            scope.Bind(name, value.ToString()!);
            eval.InjectResolved("globals", name, value);
        }
        var list = new List<(int, Complex)>();
        for (int i = 0; i < ports.Count; i++)
        {
            int port;
            // a port number, or an expression over the globals
            try
            {
                port = int.TryParse(ports[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? d
                     : (int)Math.Round(eval.Eval(ports[i], scope, null).AsReal());
            }
            catch (Exception e) { throw new InvalidOperationException($"{who}: drive='{ports[i]}' is not a port number ({e.Message})."); }
            Complex weight = Complex.One;
            if (weights.Count > 0)
            {
                try { weight = eval.Eval(weights[i], scope, null).ToComplex(); }
                catch (Exception e) { throw new InvalidOperationException($"{who}: weight '{weights[i]}' of port {port} is not a number ({e.Message})."); }
                if (!double.IsFinite(weight.Real) || !double.IsFinite(weight.Imaginary))
                    throw new InvalidOperationException($"{who}: weight '{weights[i]}' of port {port} is not finite.");
            }
            list.Add((port, weight));
        }
        return list;
    }

    /// <summary>A comma-separated list, split only at bracket depth zero, so <c>polar(1,-90)</c> stays one item.</summary>
    private static List<string> SplitList(string text)
    {
        var items = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length)
            {
                char ch = text[i];
                if (ch is '(' or '[') depth++;
                else if (ch is ')' or ']') depth--;
                if (ch != ',' || depth != 0) continue;
            }
            string item = text[start..i].Trim();
            if (item.Length > 0) items.Add(item);
            start = i + 1;
        }
        return items;
    }

    // ── the pieces ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 3D view whose run kept the fields in <paramref name="fieldsFolder"/>: the first document its run directory's
    /// inputs record names (EmRunInputs, relative to the workspace that holds <c>results/</c>), when it is a <c>.c3d</c> that
    /// still exists; null otherwise.</summary>
    public static string? C3dOf(string fieldsFolder)
    {
        string runDir = fieldsFolder.EndsWith(".fields", StringComparison.Ordinal) ? fieldsFolder[..^".fields".Length] : fieldsFolder;
        string inputs = Path.Combine(runDir, CircuitRF.Design.ThreeD.C3dRunDocument.InputsFileName);
        try
        {
            if (!File.Exists(inputs)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(inputs));
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
            string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(runDir)));
            foreach (var f in files.EnumerateArray())
                if (f.TryGetProperty("path", out var p) && p.GetString() is { } rel && rel.EndsWith(".c3d", StringComparison.OrdinalIgnoreCase) && root is not null)
                {
                    string full = Path.GetFullPath(Path.Combine(root, rel));
                    return File.Exists(full) ? full : null;
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    /// <summary>I − SᴴS: the power a set of unit incident waves leaves in the block, as a Hermitian form.</summary>
    internal static Complex[,] Dissipation(Complex[,] s)
    {
        int n = s.GetLength(0);
        var l = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex sum = i == j ? Complex.One : Complex.Zero;
                for (int k = 0; k < n; k++) sum -= Complex.Conjugate(s[k, i]) * s[k, j];
                l[i, j] = sum;
            }
        return l;
    }

    /// <summary>R-sar9-5's checks, as sentences (each a note), and the ones that fail as warnings.</summary>
    private static List<string> Checks(double pAbs, double pAbsQ, double pDel, double pDelS, Complex[,] loss, Complex[,] q,
                                       PortFieldFrequency freq, out List<string> warnings)
    {
        var lines = new List<string>();
        warnings = [];
        double scale = Math.Max(Math.Abs(pDel), double.Epsilon);
        double dq = Math.Abs(pAbs - pAbsQ) / Math.Max(pAbs, double.Epsilon);
        lines.Add($"Check: the summed field absorbs {G(pAbs)} W and the tissue matrix says {G(pAbsQ)} W ({G(dq * 100)} % apart).");
        if (dq > 1e-3) warnings.Add($"The summed field's absorbed power differs from the tissue matrix's by {G(dq * 100)} %: the field files do not match their manifest.");
        double dd = Math.Abs(pDel - pDelS) / scale;
        lines.Add($"Check: the circuit delivers {G(pDel)} W into the block and the field set's S-matrix says {G(pDelS)} W ({G(dd * 100)} % apart).");
        if (dd > PowerTolerance)
            warnings.Add($"The power the circuit delivers into the block differs from the field set's S-matrix by {G(dd * 100)} %: the .sNp the block " +
                         "reads is interpolated away from the SAR frequency, or is not the run's.");
        if (pAbs > pDel + PowerTolerance * scale)
            warnings.Add($"The tissue absorbs {G(pAbs)} W, more than the {G(pDel)} W delivered into the block: the per-port fields and the S-matrix disagree.");
        // L − Q must be positive semidefinite: no excitation absorbs more than it dissipates.
        int n = q.GetLength(0);
        var diff = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) diff[i, j] = loss[i, j] - q[i, j];
        double minEig = MinEigenvalue(diff), maxLoss = MaxEigenvalue(loss);
        lines.Add($"Check: over every excitation, the block's dissipation less the tissue's absorption is at least {G(minEig)} W per W incident " +
                  $"(the dissipation's largest {G(maxLoss)}).");
        if (minEig < -PowerTolerance * Math.Max(maxLoss, double.Epsilon))
            warnings.Add($"Some excitation of the block absorbs more in tissue than the S-matrix lets it dissipate ({G(minEig)} W per W incident): the " +
                         "per-port fields are not consistent with the S-matrix.");
        if (freq.RadiationEfficiency is { } eff)
            for (int k = 0; k < n && k < eff.Count; k++)
                if (eff[k] is { } eta)
                {
                    double accepted = 1 - freq.SMatrix()[k, k].Magnitude * freq.SMatrix()[k, k].Magnitude;
                    double prad = eta * accepted, lkk = loss[k, k].Real, qkk = q[k, k].Real;
                    double rest = lkk - qkk - prad;
                    lines.Add($"Check: port {k + 1} alone dissipates {G(lkk)} W per W incident: {G(qkk)} absorbed, {G(prad)} radiated, {G(rest)} the rest (conductor loss).");
                    if (rest < -PowerTolerance * Math.Max(lkk, double.Epsilon))
                        warnings.Add($"Port {k + 1} alone absorbs and radiates more than it dissipates ({G(-rest)} W per W incident over).");
                }
        return lines;
    }

    private static DataSet DataSetOf(SarRunRecord r, CosimRecord c, PortFieldManifest m, Complex[] a, Complex[] b, Complex gamma)
    {
        var data = new DataSet();
        var f = new Axis("f", [r.FrequencyHz], "Hz");
        void Scalar(string name, string unit, double? v)
        {
            if (v is { } x) data.Add(name, new DataCube([f], [x]) { Unit = unit });
        }
        foreach (double mass in m.MassesGrams) Scalar(OpenEmsSarResults.PeakCube(mass), "W/kg", r.PeakPerWatt(SarSettings.AveragedName(mass)));
        Scalar(PeakLocalCube, "W/kg", r.PeakPerWatt(SarVolumeFiles.LocalQuantity));
        Scalar(OpenEmsSarResults.WholeSarCube, "W/kg", r.WholeSarPerWatt);
        Scalar(OpenEmsSarResults.AbsorbedCube, "W", r.AbsorbedFraction);
        Scalar(OpenEmsSarResults.AcceptedCube, "W", r.AcceptedPowerW / r.PerWattDivisor);
        Scalar(DeliveredCube, "W", c.DeliveredPowerW / r.PerWattDivisor);
        Scalar(OtherLossCube, "W", c.OtherLossW / r.PerWattDivisor);
        Scalar(OpenEmsSarResults.MassCube, "kg", r.TissueMassKg);
        Scalar(OpenEmsSarResults.B1MeanCube, "T", r.B1pMeanPerSqrtWatt);
        Scalar(OpenEmsSarResults.EfficiencyCube, "", r.SarEfficiency);
        data.Add(ReflectionCube, new DataCube([f], [gamma]));
        var port = new Axis("Port", [.. m.Ports.Select(p => (double)p.Number)], "");
        double root = Math.Sqrt(r.PerWattDivisor);
        data.Add(IncidentCube, new DataCube([f, port], [.. a.Select(x => x / root)]) { Unit = "sqrt(W)" });
        data.Add(ReflectedCube, new DataCube([f, port], [.. b.Select(x => x / root)]) { Unit = "sqrt(W)" });
        return data;
    }

    /// <summary>brief-sar-10 R-sar10-2 — an on-demand evaluation's scalars: the reflection, what the drive accepts and the
    /// block receives, the absorbed power from the tissue matrix, and the waves — all per watt, nothing recombined.</summary>
    private static DataSet DataSetOf(double hz, CosimWaves w, PortFieldManifest m, double absorbedW)
    {
        var data = new DataSet();
        var f = new Axis("f", [hz], "Hz");
        int n = w.A.Length;
        double pDel = Enumerable.Range(0, n).Sum(k => w.A[k].Magnitude * w.A[k].Magnitude - w.B[k].Magnitude * w.B[k].Magnitude);
        data.Add(OpenEmsSarResults.AbsorbedCube, new DataCube([f], [absorbedW / w.DivisorW]) { Unit = "W" });
        data.Add(OpenEmsSarResults.AcceptedCube, new DataCube([f], [w.AcceptedW / w.DivisorW]) { Unit = "W" });
        data.Add(DeliveredCube, new DataCube([f], [pDel / w.DivisorW]) { Unit = "W" });
        data.Add(ReflectionCube, new DataCube([f], [w.Reflection]));
        var port = new Axis("Port", [.. m.Ports.Select(p => (double)p.Number)], "");
        double root = Math.Sqrt(w.DivisorW);
        data.Add(IncidentCube, new DataCube([f, port], [.. w.A.Select(x => x / root)]) { Unit = "sqrt(W)" });
        data.Add(ReflectedCube, new DataCube([f, port], [.. w.B.Select(x => x / root)]) { Unit = "sqrt(W)" });
        return data;
    }

    private static void WriteVolume(string dir, string quantity, float[] values, int[] shape, SarRunRecord record)
    {
        SarVolumeFiles.WriteFloat32(Path.Combine(dir, SarVolumeFiles.VolumeFile(0, quantity)), values, shape);
        record.Volumes.Add(quantity);
    }

    private static SarPeakRecord Peak(string quantity, SarPeak p, SarGrid grid, short[] owners, PortFieldManifest m)
    {
        int t = owners[grid.Index(p.I, p.J, p.K)];
        bool known = t >= 0 && t < m.Tissues.Count;
        var at = m.World(p.XM, p.YM, p.ZM);
        return new SarPeakRecord
        {
            Quantity = quantity, Value = p.Value, Cell = [p.I, p.J, p.K], CentreM = [at.X, at.Y, at.Z],
            CubeLoM = p.Cube?.LoM, CubeHiM = p.Cube?.HiM, CubeMassKg = p.Cube?.MassKg,
            Kind = p.Cube is null ? null : p.Kind.ToString(),
            Object = known ? m.Tissues[t] : null, Material = known && t < m.TissueMaterials.Count ? m.TissueMaterials[t] : null,
        };
    }

    private static double[] Centres(SarAxis a)
    {
        var c = new double[a.Count];
        for (int i = 0; i < c.Length; i++) c[i] = a.Centre(i);
        return c;
    }

    /// <summary>The smallest and largest eigenvalues of a small Hermitian matrix, through its real symmetric 2N form (each
    /// eigenvalue twice) and cyclic Jacobi.</summary>
    internal static double MinEigenvalue(Complex[,] h) => Eigenvalues(h).Min();
    internal static double MaxEigenvalue(Complex[,] h) => Eigenvalues(h).Max();

    private static double[] Eigenvalues(Complex[,] h)
    {
        int n = h.GetLength(0), m = 2 * n;
        var a = new double[m, m];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                a[i, j] = a[n + i, n + j] = h[i, j].Real;
                a[i, n + j] = -h[i, j].Imaginary;
                a[n + i, j] = h[i, j].Imaginary;
            }
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int p = 0; p < m; p++) for (int r = p + 1; r < m; r++) off += a[p, r] * a[p, r];
            if (off < 1e-30) break;
            for (int p = 0; p < m; p++)
                for (int r = p + 1; r < m; r++)
                {
                    if (Math.Abs(a[p, r]) < 1e-300) continue;
                    double theta = (a[r, r] - a[p, p]) / (2 * a[p, r]);
                    double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < m; k++)
                    {
                        double akp = a[k, p], akr = a[k, r];
                        a[k, p] = c * akp - s * akr;
                        a[k, r] = s * akp + c * akr;
                    }
                    for (int k = 0; k < m; k++)
                    {
                        double apk = a[p, k], ark = a[r, k];
                        a[p, k] = c * apk - s * ark;
                        a[r, k] = s * apk + c * ark;
                    }
                }
        }
        var e = new double[m];
        for (int i = 0; i < m; i++) e[i] = a[i, i];
        return e;
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(ch => invalid.Contains(ch) ? '_' : ch)]);
    }

    private static string Db(double mag) => (20 * Math.Log10(Math.Max(mag, 1e-15))).ToString("0.0", CultureInfo.InvariantCulture);
    private static string Pct(double f) => (f * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";
    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
