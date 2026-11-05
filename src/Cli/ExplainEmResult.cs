// brief-cosim-pattern-1 R-cp1-5 — reading a result record back: how the run was made, in the setup's own words, and what differs
// from the setup now. A difference is information and never exits nonzero. It reads no solver and writes nothing.

using System.Globalization;
using System.Text.Json;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using RfCore.Export;

namespace CircuitRF.Cli;

internal static class ExplainEmResult
{
    private const string FromRecord = "the result record";

    public static int Walk(string path, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        var (record, refusal) = EmResultRecord.Load(full);
        if (record is null) return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, refusal!));

        string device = record.Solver.Device is { Length: > 0 } d ? $" on {d}" : "";
        walks.Add(new ResolutionStepJson("solver", full, $"{record.Solver.Name} {record.Solver.Version}".Trim() + device, FromRecord));
        walks.Add(new ResolutionStepJson("written", full,
            $"{record.Written.ToString("u", CultureInfo.InvariantCulture)}, circuitRF {record.Product}, stamp {record.Stamp}", FromRecord));
        foreach (var (what, relative) in new[] { (".sNp", record.Files.Snp), (".npy", record.Files.Npy), ("field set", record.Files.Fields) })
            if (relative is { Length: > 0 })
                walks.Add(new ResolutionStepJson(what, EmResultRecord.Resolve(full, relative), relative, "relative to the record"));
        foreach (var bad in record.Verify(full))
            walks.Add(new ResolutionStepJson("files disagree", bad.Path, bad.Sentence, "the stamp each file carries; `check` reports this as an error"));
        foreach (var p in record.Ports)
            walks.Add(new ResolutionStepJson($"port {p.Number}", null, Port(p), FromRecord));
        walks.Add(new ResolutionStepJson("sweep", null, Band(record.Frequencies.SweepHz), FromRecord));
        if (record.Frequencies.FarFieldHz.Count > 0)
            walks.Add(new ResolutionStepJson("far field", null, Band(record.Frequencies.FarFieldHz), FromRecord));
        if (record.Frequencies.FieldSetHz.Count > 0)
            walks.Add(new ResolutionStepJson("field set frequencies", null, Band(record.Frequencies.FieldSetHz), FromRecord));
        if (record.Geometry is { } g)
        {
            if (g.MeshSize is { } size)
                walks.Add(new ResolutionStepJson("mesh", null, $"{size.ToString("N0", CultureInfo.InvariantCulture)} {g.MeshUnit}" +
                    (g.Unknowns is { } u ? $", {u.ToString("N0", CultureInfo.InvariantCulture)} unknowns" : ""), "as the run reported it"));
            walks.Add(new ResolutionStepJson("hashes", null, $"geometry {g.GeometryHash}, mesh {g.MeshHash}, ports {g.PortHash}", "the .sNp's provenance"));
        }

        if (record.Setup is not { } setup)
        {
            walks.Add(new ResolutionStepJson("settings", full, "settings not recorded", "this result was adopted from a run that kept no record"));
            return 0;
        }
        var then = EmResultRecord.Settings(setup);
        foreach (var (field, value) in then)
            walks.Add(new ResolutionStepJson("setting " + field, null, value, "the setup as this run used it"));

        var (current, whyNot, source) = record.CurrentSetup(full);
        if (current is null)
        {
            walks.Add(new ResolutionStepJson("setup now", source, null, $"not compared: {whyNot}"));
            return 0;
        }
        var differences = EmResultRecord.Differences(then, EmResultRecord.Settings(EmSetupPersistence.ToEmbedded(current)));
        walks.Add(new ResolutionStepJson("setup now", source, differences.Count == 0 ? "the same as this run's" : $"{differences.Count} setting(s) differ",
                                         "compared with the file the setup was read from"));
        foreach (var (field, was, now) in differences)
            walks.Add(new ResolutionStepJson("changed " + field, source, $"{was ?? "(absent)"} -> {now ?? "(absent)"}", "this run's value -> the setup's now"));
        Document(record, source, walks);
        return 0;
    }

    /// <summary>What differs between the 3D view this run read and the document on disk now, when the setup lives in that document.</summary>
    private static void Document(EmResultRecord record, string? source, List<ResolutionStepJson> walks)
    {
        if (record.ThreeDView is not { Length: > 0 } text || source is null || !C3dSetups.IsThreeDView(source)) return;
        try
        {
            using var was = JsonDocument.Parse(text);
            using var now = JsonDocument.Parse(C3dPersistence.Serialize(C3dPersistence.LoadFromFile(source)));
            var differences = EmResultRecord.Differences(EmResultRecord.Settings(was.RootElement), EmResultRecord.Settings(now.RootElement));
            walks.Add(new ResolutionStepJson("3D view now", source, differences.Count == 0 ? "the same as this run's" : $"{differences.Count} field(s) differ",
                                             "compared with the document this run read"));
            foreach (var (field, then, current) in differences)
                walks.Add(new ResolutionStepJson("changed 3D view " + field, source, $"{then ?? "(absent)"} -> {current ?? "(absent)"}", "this run's value -> the document's now"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or C3dReadException)
        {
            walks.Add(new ResolutionStepJson("3D view now", source, null, $"not compared: {e.Message}"));
        }
    }

    private static string Port(EmResultPort p)
    {
        string z = p.Z0Im == 0 ? p.Z0Re.ToString("G6", CultureInfo.InvariantCulture)
                               : $"{p.Z0Re.ToString("G6", CultureInfo.InvariantCulture)}{(p.Z0Im < 0 ? "-" : "+")}{Math.Abs(p.Z0Im).ToString("G6", CultureInfo.InvariantCulture)}j";
        string part = p.Part is { } r ? $", {r}{(p.Pins is { Count: > 0 } pins ? $" pins {string.Join("-", pins)}" : "")}" : "";
        return $"{p.Name}, {z} Ohm, {p.Kind ?? "kind not recorded"}{part}";
    }

    private static string Band(IReadOnlyList<double> hz)
        => hz.Count switch
        {
            0 => "none",
            1 => Check.Hz(hz[0]),
            _ => $"{hz.Count} points, {Check.Hz(hz[0])} to {Check.Hz(hz[^1])}",
        };
}
