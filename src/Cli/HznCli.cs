using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Diagnostics;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// HZN-2 R-hzn2-8 — what <c>check</c>, <c>explain</c> and <c>find</c> say about a Hertzon project (<c>.hzn</c>).
///
/// <para>It owns no translation: every sentence comes out of <see cref="HznFile"/>, <see cref="HznToC3d"/> and the
/// validators a <c>.c3d</c> already goes through, so a design that checks here is a design that opens in the 3D editor.
/// Read-only, as the other two are: nothing is written, and nothing is solved.</para>
/// </summary>
internal static class HznCli
{
    public const string Extension = ".hzn";

    /// <summary>True when <paramref name="path"/> is a Hertzon project — by content: its <c>format</c> says so (its version
    /// may be one circuitRF does not read; that is <c>check</c>'s finding, not a reason to call the file something else).</summary>
    public static bool IsHertzon(string path)
    {
        var read = HznFile.Read(path);
        if (read.Ok) return true;
        return read.Refusal?.Id is "hzn.version.newer" or "hzn.version.older" or "hzn.version.unreadable"
            or "hzn.file.not-json" or "hzn.file.not-utf8";
    }

    // ── check ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every finding about the file and its 3D designs (or just <paramref name="design"/>).</summary>
    public static IReadOnlyList<Diagnostic> Check(string path, string? design)
    {
        var found = new List<Diagnostic>();
        var read = HznFile.Read(path);
        if (!read.Ok) return [read.Refusal!];
        var project = read.File!.Project;

        var names = HznToC3d.ThreeDDesigns(project);
        if (design is not null)
        {
            if (!project.Designs.Any(d => d.Name == design)) return [HznTranslationDiagnostics.NoSuchDesign(design, [.. project.Designs.Select(d => d.Name ?? "")])];
            names = [design];
        }
        foreach (string name in names)
        {
            var t = HznToC3d.Translate(project, name);
            if (t.Refusal is { } refusal) { found.Add(refusal); continue; }
            var doc = t.Document!;
            var tech = t.Technology;
            foreach (var d in C3dValidation.Validate(doc, isKnownMaterial: m => tech?.FindMaterial(m) is not null))
                if (d.Severity != DiagnosticSeverity.Error) found.Add(d);
            foreach (string note in t.Notes) found.Add(HznTranslationDiagnostics.Note($"design '{name}': {note}"));
        }
        if (names.Count == 0 && design is null)
            found.Add(HznTranslationDiagnostics.Note("The project has no 3D design."));
        return found;
    }

    // ── explain ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Each design and what it became: the objects, ports and setups of the document, or the refusal's item count.
    /// Returns the exit contribution (1 when a design is refused).</summary>
    public static int Explain(string path, string? design, string? setup, List<ResolutionStepJson> walks)
    {
        var read = HznFile.Read(path);
        if (!read.Ok)
        {
            walks.Add(new ResolutionStepJson("project", path, null, read.Refusal!.Render()));
            return 1;
        }
        var project = read.File!.Project;
        walks.Add(new ResolutionStepJson("project", path, project.Name ?? "", $"hertzon-project, format version {project.Version}"));
        int exit = 0;
        foreach (var d in project.Designs)
        {
            string name = d.Name ?? "";
            if (design is not null && name != design) continue;
            if (d.Kind == HznDesignKind.Circuit)
            {
                walks.Add(new ResolutionStepJson("design", name, null, "a circuit design; circuitRF opens a project's 3D designs"));
                continue;
            }
            var t = HznToC3d.Translate(project, name);
            if (t.Ok)
            {
                var doc = t.Document!;
                walks.Add(new ResolutionStepJson("design", name,
                    $"{doc.Objects.Count} object(s), {doc.Ports.Count} port(s), {doc.Variables.Count} variable(s), {doc.Setups.Count} setup(s), {t.Technology?.ResolvedMaterials.Count ?? 0} material(s)",
                    "translated to a 3D view held in memory"));
                // HZN-7: the setups it became, and which one `em` would run (and where its answer would land).
                foreach (var (c3dSetup, hznOrigin) in t.Provenance.Setups)
                    walks.Add(new ResolutionStepJson("setup", name + "/" + c3dSetup, hznOrigin, "an EMerge setup"));
                var (plan, why) = HznEmergeRun.Plan(project, path, name, setup);
                walks.Add(plan is not null
                    ? new ResolutionStepJson("run", name + "/" + plan.C3dSetup, plan.HznSetup, $"em runs this setup on EMerge; results in {plan.ResultsRoot}")
                    : new ResolutionStepJson("run", name, null, why!.Render()));
                foreach (var (c3d, origin) in t.Provenance.Objects.OrderBy(o => o.Key, StringComparer.Ordinal))
                    walks.Add(new ResolutionStepJson("object", name + "/" + c3d, origin.HznObject,
                        $"{origin.Steps.Count} step(s)" + (origin.Steps.Any(s => s.Suppressed) ? ", some suppressed" : "")));
            }
            else
            {
                walks.Add(new ResolutionStepJson("design", name, null, (t.Refusal ?? HznTranslationDiagnostics.NotThreeD(name)).Render()));
                exit = 1;
            }
        }
        if (design is not null && !project.Designs.Any(x => x.Name == design))
        {
            walks.Add(new ResolutionStepJson("design", design, null, "the project has no such design"));
            exit = 1;
        }
        return exit;
    }

    // ── find ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Hertzon projects below <paramref name="dir"/> (bounded, no directory link followed), each with its designs.</summary>
    public static List<FoundHertzonProjectJson> Projects(string dir, int depth, Func<string, bool>? skip = null)
    {
        var list = new List<FoundHertzonProjectJson>();
        Walk(dir, 0, depth, skip, list);
        list.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return list;
    }

    private static void Walk(string dir, int level, int limit, Func<string, bool>? skip, List<FoundHertzonProjectJson> into)
    {
        if (skip?.Invoke(dir) == true) return;
        try
        {
            foreach (string file in Directory.GetFiles(dir, "*" + Extension))
                into.Add(Describe(file));
            if (level >= limit) return;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                Walk(sub, level + 1, limit, skip, into);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static FoundHertzonProjectJson Describe(string file)
    {
        var read = HznFile.Read(file);
        if (!read.Ok) return new(file, null, [], read.Refusal!.Render());
        var p = read.File!.Project;
        return new(file, p.Name, [.. p.Designs.Select(d => new FoundHertzonDesignJson(d.Name ?? "", d.Kind == HznDesignKind.Circuit ? "circuit" : "3d"))]);
    }
}
