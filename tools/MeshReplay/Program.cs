// Mesh replay (brief-dfr35-1 R-dfr35-1-6). Imported boards fail at the mesh, the one step a dry run does not reach, so every setup
// to hand is meshed again before a release: each workspace is copied to a temporary directory, so nothing it is pointed at is ever
// written, and `em --mesh-only` runs there under a time limit and a memory ceiling. One row per setup. Given last run's output it
// prints only what changed: a refusal that is a correct answer to a setup's state is not a failure, and a change is what to look at.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

return MeshReplay.Main(args);

internal static class MeshReplay
{
    internal sealed record Row(string Setup, string Outcome, long? Elements, long? Estimated, int Warnings, bool Named, double Seconds, string Detail);

    private const string Usage =
        "usage: MeshReplay <folder> [--cli <CircuitRF.Cli.dll>] [--time <seconds per setup, default 300>]\n" +
        "                   [--memory <GB per setup, default 12>] [--out <this run's rows.json>] [--previous <last run's rows.json>]";

    public static int Main(string[] args)
    {
        string? folder = null, cli = null, output = null, previous = null;
        int seconds = 300;
        double memoryGb = 12;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--cli": cli = Next(); break;
                case "--out": output = Next(); break;
                case "--previous": previous = Next(); break;
                case "--time": seconds = int.Parse(Next() ?? "300"); break;
                case "--memory": memoryGb = double.Parse(Next() ?? "12", System.Globalization.CultureInfo.InvariantCulture); break;
                default: folder = args[i]; break;
            }
        }
        if (folder is null || !Directory.Exists(folder)) { Console.Error.WriteLine(Usage); return 2; }
        cli ??= FindCli();
        if (cli is null || !File.Exists(cli))
        {
            Console.Error.WriteLine("The CLI was not found: build it (dotnet build src/Cli -c Release) or pass --cli <CircuitRF.Cli.dll>.");
            return 2;
        }

        string root = Path.GetFullPath(folder);
        var cems = Directory.EnumerateFiles(root, "*.cem", SearchOption.AllDirectories)
                            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(d => d is "results" or ".git"))
                            .Order(StringComparer.Ordinal).ToList();
        var last = previous is not null && File.Exists(previous)
            ? JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(previous))?.ToDictionary(r => r.Setup) : null;
        Console.WriteLine($"{cems.Count} setup(s) under {root}; {seconds} s and {memoryGb} GB each; CLI {cli}");

        var rows = new List<Row>();
        foreach (string cem in cems)
        {
            var row = Replay(cem, root, cli, seconds, memoryGb);
            rows.Add(row);
            if (last is null) Print(row);
            else if (!last.TryGetValue(row.Setup, out var was)) { Console.Write("new      "); Print(row); }
            else if (Changed(was, row)) { Console.Write("was      "); Print(was); Console.Write("now      "); Print(row); }
        }
        if (last is not null)
            foreach (var gone in last.Values.Where(w => rows.All(r => r.Setup != w.Setup))) { Console.Write("gone     "); Print(gone); }

        Console.WriteLine(string.Join(", ", rows.GroupBy(r => r.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")));
        if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static bool Changed(Row a, Row b) => a.Outcome != b.Outcome || a.Elements != b.Elements || a.Warnings != b.Warnings || a.Detail != b.Detail;

    private static void Print(Row r)
    {
        string size = r.Elements is { } n
            ? $"{n:N0} elements" + (r.Estimated is > 0 and var e ? $" ({(double)n / e:0.0}× the estimate of {e:N0})" : "") + $", {r.Warnings} warning(s)"
            : "";
        string detail = r.Detail.Length > 0 ? (size.Length > 0 ? " — " : "") + r.Detail : "";
        Console.WriteLine($"{r.Outcome,-9}{r.Seconds,6:0} s  {r.Setup}: {size}{detail}{(r.Named ? " [named before the mesh]" : "")}");
    }

    /// <summary>The workspace is copied and the copy meshed: a setup's own folder is never written.</summary>
    private static Row Replay(string cem, string root, string cli, int seconds, double memoryGb)
    {
        string setup = Path.GetRelativePath(root, cem);
        string workspace = WorkspaceOf(cem, root);
        string temp = Path.Combine(Path.GetTempPath(), "crf-meshreplay-" + Guid.NewGuid().ToString("N")[..12]);
        var watch = Stopwatch.StartNew();
        try
        {
            CopyTree(workspace, temp);
            string copy = Path.Combine(temp, Path.GetRelativePath(workspace, cem));
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = temp, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (string a in new[] { cli, "em", copy, "--mesh-only", "--json", "--memory-limit", memoryGb.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(seconds * 1000))
            {
                p.Kill(entireProcessTree: true);
                return new(setup, "timed-out", null, null, 0, false, watch.Elapsed.TotalSeconds, $"stopped at {seconds} s");
            }
            return Read(setup, p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), watch.Elapsed.TotalSeconds, temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(setup, "failed", null, null, 0, false, watch.Elapsed.TotalSeconds, "the replay itself: " + e.Message);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Row Read(string setup, int exit, string stdout, string stderr, double seconds, string temp)
    {
        using var doc = JsonDocument.Parse(stdout.Length > 0 ? stdout : "{}");
        var said = doc.RootElement.TryGetProperty("diagnostics", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(d => (Id: d.GetProperty("id").GetString() ?? "", Text: d.GetProperty("message").GetString() ?? "")).ToList() : [];
        bool named = said.Any(d => d.Text.Contains("Geometry that can stop the mesher", StringComparison.Ordinal));
        string Clean(string t) => t.Replace(temp + Path.DirectorySeparatorChar, "", StringComparison.Ordinal).Replace(temp, "", StringComparison.Ordinal);
        if (exit == 0)
        {
            var m = said.Select(d => Regex.Match(d.Text, @"^Meshed ([\d,]+) tetrahedra against ([\d,]+) estimated")).FirstOrDefault(x => x.Success);
            long? N(int g) => m is null ? null : long.Parse(m.Groups[g].Value.Replace(",", ""));
            int warned = said.Count(d => d.Id == "em.mesh-only.warning");
            string words = string.Join("; ", said.Where(d => d.Id == "em.mesh-only.warning").Select(d => d.Text.Replace("Gmsh: ", "")).Distinct().Take(3));
            return new(setup, "meshed", N(1), N(2), warned, named, seconds, words);
        }
        var error = said.LastOrDefault(d => d.Id.StartsWith("em.", StringComparison.Ordinal) && !d.Id.Contains(".run.", StringComparison.Ordinal) && !d.Id.Contains("mesh-only", StringComparison.Ordinal));
        string text = Clean(error.Text ?? "");
        if (text.Length == 0) text = Clean(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? $"exit {exit}");
        // Gmsh's words are the point of a mesher failure; a refusal's first sentence says what it needs to.
        return error.Id == "em.solve.failed"
            ? new(setup, "failed", null, null, 0, named, seconds, Regex.Match(text, "“(.*?)”").Groups[1].Value is { Length: > 0 } quoted ? quoted : text)
            : new(setup, "refused", null, null, 0, named, seconds, FirstSentence(text));
    }

    private static string FirstSentence(string s)
    {
        // A short opening sentence is only the heading ("The mesh has elements the solver cannot use."): the next one says which.
        var m = Regex.Match(s, @"^.{60,}?[.:](\s|$)");
        return m.Success ? m.Value.Trim() : s;
    }

    private static string WorkspaceOf(string cem, string root)
    {
        for (string? d = Path.GetDirectoryName(cem); d is not null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, ".cws"))) return d;
        return Path.GetDirectoryName(cem)!;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(from))
            if (Path.GetFileName(d) is not ("results" or ".git")) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static string? FindCli()
    {
        for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "circuitRF.slnx")))
                return Path.Combine(d, "src", "Cli", "bin", "Release", "net10.0", "CircuitRF.Cli.dll");
        return null;
    }
}
