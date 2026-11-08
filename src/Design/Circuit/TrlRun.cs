using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Design.Circuit;

/// <summary>
/// brief-de-4 — runs a <see cref="TrlAnalysis"/>: reads the three SnP blocks it names from the elaborated bench (so their files are
/// resolved exactly as the run resolves any SnP's, variables and <c>--set</c> included), solves for both fixtures with
/// <see cref="TrlCalibration.Solve"/>, writes them as Touchstone files a Deembed reads, and returns what was found as a DataSet.
/// Solves no circuit. The GUI's Simulate, the <c>trl</c> verb and MCP all reach it through <see cref="CircuitEvaluation"/>.
/// </summary>
public static class TrlRun
{
    /// <summary>What the run wrote, for a caller that reports paths (the CLI's <c>--json</c>).</summary>
    public sealed record Written(string FixtureA, string FixtureB);

    /// <summary>
    /// Solves <paramref name="a"/> on <paramref name="nl"/> and writes both fixtures. <paramref name="baseDirectory"/> is the base an
    /// SnP's relative <c>File</c> resolves against in this run (<see cref="Elaborator.ResolveFileReference"/>), so a fixture path
    /// written here is the string a Deembed's <c>File</c> takes. <paramref name="fixtureA"/>/<paramref name="fixtureB"/>, when
    /// given, replace the directive's paths (the CLI's overrides). A refusal throws <see cref="InvalidOperationException"/>
    /// naming the analysis.
    /// </summary>
    public static DataSet Run(TrlAnalysis a, ElaboratedNetlist nl, string? baseDirectory, List<string> notes,
                              string? fixtureA = null, string? fixtureB = null, Action<Written>? written = null)
    {
        string who = $"TRL '{a.Name}'";
        string FileOf(string key, string instance)
        {
            if (string.IsNullOrWhiteSpace(instance))
                throw new TrlRefusalException(TrlDiagnostics.NoBlockNamed(a.Name, key));
            var ec = nl.Components.FirstOrDefault(c => string.Equals(c.InstancePath, instance, StringComparison.Ordinal));
            if (ec is null)
            {
                var snps = nl.Components.Where(c => c.Model is SnpModel { Inverse: false, RecordPath: null }).Select(c => c.InstancePath).ToList();
                throw new TrlRefusalException(TrlDiagnostics.NoInstance(a.Name, key, instance, string.Join(", ", snps)));
            }
            if (ec.Model is not SnpModel { Inverse: false, RecordPath: null } snp)
                throw new TrlRefusalException(TrlDiagnostics.NotAnSnp(a.Name, key, instance, ec.ComponentType));
            if (snp.PortCount != 2)
                throw new TrlRefusalException(TrlDiagnostics.NotTwoPort(a.Name, key, instance, snp.PortCount));
            if (string.IsNullOrWhiteSpace(snp.FilePath))
                throw new TrlRefusalException(TrlDiagnostics.NoFile(a.Name, key, instance));
            if (!File.Exists(snp.FilePath))
                throw new TrlRefusalException(TrlDiagnostics.FileMissing(a.Name, key, instance, snp.FilePath));
            return snp.FilePath;
        }

        string thruPath = FileOf("thru", a.Thru), reflectPath = FileOf("reflect", a.Reflect), linePath = FileOf("line", a.Line);
        var thru = TouchstoneCache.Get(thruPath);
        var (result, refusal) = TrlCalibration.Solve(thru, TouchstoneCache.Get(reflectPath), TouchstoneCache.Get(linePath),
                                                     a.ReflectIsOpen ? TrlReflect.Open : TrlReflect.Short);
        if (result is null) throw new TrlRefusalException(TrlDiagnostics.SolveRefused(a.Name, refusal!));

        string storedA = string.IsNullOrWhiteSpace(fixtureA) ? a.FixtureAPath : fixtureA.Trim();
        string storedB = string.IsNullOrWhiteSpace(fixtureB) ? a.FixtureBPath : fixtureB.Trim();
        string pathA = Path.GetFullPath(Elaborator.ResolveFileReference(storedA, baseDirectory));
        string pathB = Path.GetFullPath(Elaborator.ResolveFileReference(storedB, baseDirectory));
        if (string.Equals(pathA, pathB, StringComparison.Ordinal))
            throw new TrlRefusalException(TrlDiagnostics.SameFixtureFile(a.Name, storedA));

        string Header(string which, string side) =>
            $" {which} from TRL '{a.Name}' (Thru {a.Thru}, Reflect {a.Reflect}, Line {a.Line}): {side}. " +
            $"The reference plane is the middle of the Thru; the impedance it is referenced to is the Line's.";
        WriteFixture(result.FixtureA, pathA, Header("Fixture A", "port 1 the instrument side, port 2 the part's"));
        WriteFixture(result.FixtureB, pathB, Header("Fixture B", "port 1 the part's side, port 2 the instrument's"));
        written?.Invoke(new Written(pathA, pathB));

        notes.Add($"{who}: wrote fixture A to '{storedA}' and fixture B to '{storedB}', {thru.FrequencyCount} point(s) " +
                  $"{Ghz(thru.Frequencies[0])}-{Ghz(thru.Frequencies[^1])}. The reference plane is the middle of the Thru, and a part " +
                  "de-embedded with them is referenced to the Line's impedance.");

        if (result.UnusableCount > 0)
        {
            nl.AddWarningOnce($"trl-usable:{a.Name}",
                $"{who}: at {result.UnusableCount} of {result.Usable.Length} point(s) ({Runs(thru.Frequencies, result.Usable)}) " +
                $"the Line is outside {TrlCalibration.UsableFromDeg:0}-{TrlCalibration.UsableToDeg:0} degrees longer than the Thru, " +
                "where the two standards are too alike to separate the fixtures; the fixtures there are not to be trusted. " +
                "Use another line for that band, or narrow the band.");
        }

        return Pack(result);
    }

    /// <summary>
    /// The sentence a run adds when a file it needs is missing and a TRL analysis in the workspace is what writes it: a
    /// Deembed duplicated from a bench whose fixtures were never made. Reads the workspace's schematics only on that failure.
    /// Null when no TRL analysis there writes <paramref name="missingFile"/>.
    /// </summary>
    public static string? WriterOf(string missingFile, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return null;
        string want = Path.GetFullPath(missingFile);
        foreach (string csch in Directory.EnumerateFiles(workspaceRoot, "*.csch", SearchOption.AllDirectories))
        {
            try
            {
                if (!File.ReadAllText(csch).Contains("\"trl\"", StringComparison.Ordinal)) continue;
                foreach (var t in CircuitRF.Design.Schematic.SchematicPersistence.LoadFromFile(csch).Item1.Analyses.OfType<TrlAnalysis>())
                    foreach (string f in new[] { t.FixtureAPath, t.FixtureBPath })
                        if (string.Equals(Path.GetFullPath(Elaborator.ResolveFileReference(f, workspaceRoot)), want, StringComparison.Ordinal))
                            return $"It is a fixture file TRL '{t.Name}' writes, on {Path.GetFileNameWithoutExtension(csch)}: run that first.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        return null;
    }

    private static void WriteFixture(SNP fixture, string path, string header)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        fixture.Comments.Add(new CommentEntry(-1, header));
        TouchstoneIO.WriteFile(fixture, path, MatrixFormat.RI, writeComments: true, precision: "G15");
    }

    /// <summary><c>SA</c>, <c>SB</c> (complex, freq × i × j, 1-based ports), <c>LineDeg</c>, <c>LineLossDb</c>, <c>Gamma</c> and <c>Usable</c>.</summary>
    private static DataSet Pack(TrlResult r)
    {
        var ds = new DataSet();
        ds.Add("SA", DataSetBuilder.FromSnp(r.FixtureA)["S"]);
        ds.Add("SB", DataSetBuilder.FromSnp(r.FixtureB)["S"]);
        var freq = new Axis("freq", r.FixtureA.Frequencies, "Hz");
        ds.Add("LineDeg", new DataCube([freq], r.LineDegrees));
        ds.Add("LineLossDb", new DataCube([freq], r.LineLossDb));
        ds.Add("Gamma", new DataCube([freq], r.Gamma));
        ds.Add("Usable", new DataCube([freq], r.Usable.Select(u => u ? 1.0 : 0.0).ToArray()));
        return ds;
    }

    /// <summary>The untrusted points as runs of the grid: "1 GHz and 8 GHz", "1-1.4 GHz and 7.6-8 GHz", so two points at the
    /// band's two ends do not read as the whole band.</summary>
    private static string Runs(double[] f, bool[] usable)
    {
        var runs = new List<string>();
        for (int k = 0; k < f.Length; k++)
        {
            if (usable[k]) continue;
            int end = k;
            while (end + 1 < f.Length && !usable[end + 1]) end++;
            runs.Add(end == k ? Ghz(f[k]) : $"{(f[k] / 1e9).ToString("G6", CultureInfo.InvariantCulture)}-{Ghz(f[end])}");
            k = end;
        }
        return runs.Count <= 2 ? string.Join(" and ", runs) : string.Join(", ", runs.Take(runs.Count - 1)) + " and " + runs[^1];
    }

    private static string Ghz(double hz) => (hz / 1e9).ToString("G6", CultureInfo.InvariantCulture) + " GHz";
}
