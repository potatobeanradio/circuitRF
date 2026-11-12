// HZN-7 R-hzn7-1/2 — a Hertzon 3D design run on circuitRF's own EMerge driver.
//
// A design opened from a `.hzn` is a `C3dDocument` held in memory (HznToC3d), with one EMerge setup per enabled sweep.
// Running it is the `.c3d` run path and nothing else: EmRunService.RunThreeDView takes the translated document and the
// setup, exactly as it takes a native `.c3d`. This file is only what differs:
//
//  - WHICH setup runs (a Hertzon setup with several sweeps is several `.c3d` setups, named "Setup - Sweep"),
//  - WHERE the answer lands: a scratch folder beside the `.hzn`, never Hertzon's own `<project>.hznresults`,
//  - the technology, which is the project's own materials and travels with the document (C3dElaborator.AttachedTechnology),
//  - the note that names every Hertzon setting this run does not use, with its value.
//
// The document has no file. A virtual `.c3d` path beside the scratch folder names the run (the setup is called
// "<design> <setup>" as an embedded setup is) and keys its record; nothing is written there.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>What a run of one Hertzon design's setup is made of: the translation, the setup chosen, and where it will land.</summary>
public sealed class HznRunPlan
{
    public required string HznPath { get; init; }
    public required string Design { get; init; }
    public required C3dTranslation Translation { get; init; }

    /// <summary>The <c>.c3d</c> setup's name: the Hertzon setup's, or "Setup - Sweep" when it has several sweeps.</summary>
    public required string C3dSetup { get; init; }

    /// <summary>The Hertzon setup and sweep that made it: "Setup/Sweep" (or "Setup" when it has no sweep).</summary>
    public required string HznSetup { get; init; }

    /// <summary>The setup as the run uses it, named "&lt;design&gt; &lt;setup&gt;" (<see cref="C3dSetups.ForRun"/>).</summary>
    public required EmSetup Setup { get; init; }

    /// <summary>The path that stands for the in-memory document: it exists only to name the run.</summary>
    public required string DocumentPath { get; init; }

    public required string ResultsRoot { get; init; }

    public C3dDocument Document => Translation.Document!;
}

public static class HznEmergeRun
{
    /// <summary>The folder beside the <c>.hzn</c> that circuitRF's own runs of it write into. Its name cannot be Hertzon's
    /// <c>&lt;project&gt;.hznresults</c>, and nothing circuitRF writes goes inside that one.</summary>
    public static string ScratchFolder(string hznPath)
    {
        string full = Path.GetFullPath(hznPath);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + ".circuitrf");
    }

    /// <summary>The results root of a run of <paramref name="hznPath"/>: a <c>results</c> folder in <see cref="ScratchFolder"/>,
    /// the leaf name every other results root has.</summary>
    public static string ResultsRoot(string hznPath) => Path.Combine(ScratchFolder(hznPath), "results");

    /// <summary>The path standing for design <paramref name="design"/>'s document: <c>&lt;scratch&gt;/&lt;design&gt;.c3d</c>. Never created.</summary>
    public static string DocumentPath(string hznPath, string design)
        => Path.Combine(ScratchFolder(hznPath), Path.GetFileName(NameSafe(design)) + C3dPersistence.Extension);

    private static string NameSafe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string([.. s.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c)]);
    }

    // ── choosing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="hznPath"/>, translates the design and chooses the setup. <paramref name="design"/> may be omitted
    /// when the project has one 3D design; <paramref name="setup"/> names a <c>.c3d</c> setup ("Setup - Sweep") or a Hertzon
    /// setup with one enabled sweep, and may be omitted when exactly one setup was translated. Anything else is a refusal that
    /// lists the choices.
    /// </summary>
    public static (HznRunPlan? Plan, Diagnostic? Refusal) Plan(string hznPath, string? design, string? setup)
    {
        var read = HznFile.Read(hznPath);
        if (!read.Ok) return (null, read.Refusal!);
        return Plan(read.File!.Project, hznPath, design, setup);
    }

    /// <summary><see cref="Plan(string, string?, string?)"/> over a project already read (an open project in the tree).</summary>
    public static (HznRunPlan? Plan, Diagnostic? Refusal) Plan(HznProject project, string hznPath, string? design, string? setup)
    {
        var designs = HznToC3d.ThreeDDesigns(project);
        if (design is null)
        {
            if (designs.Count == 0) return (null, Refuse("hzn.run.no-3d", "The project has no 3D design to run."));
            if (designs.Count > 1)
                return (null, Refuse("hzn.run.design-needed",
                    $"The project has {designs.Count} 3D designs ({string.Join(", ", designs.Select(d => $"'{d}'"))}); name one with --design <name>."));
            design = designs[0];
        }

        var t = HznToC3d.Translate(project, design);
        if (t.Refusal is { } refused) return (null, refused);
        var doc = t.Document!;

        var all = C3dSetups.Read(doc);
        if (all.Count == 0)
            return (null, Refuse("hzn.run.no-setup", $"Design '{design}' has no enabled setup with a sweep or frequency circuitRF translates, so there is nothing to run."));
        string List() => string.Join(", ", all.Select(s => $"'{s.Name}'"));

        C3dEmbeddedSetup chosen;
        if (setup is null)
        {
            if (all.Count > 1)
                return (null, Refuse("hzn.run.setup-needed", $"Design '{design}' translates to {all.Count} setups ({List()}); name one with --setup <name>."));
            chosen = all[0];
        }
        else if (all.FirstOrDefault(s => s.Name == setup) is { } exact) chosen = exact;
        else
        {
            // A Hertzon setup name stands for its sweep when it has exactly one enabled.
            var byHertzon = all.Where(s => t.Provenance.Setups.TryGetValue(s.Name, out var origin) && origin.Split('/')[0] == setup).ToList();
            if (byHertzon.Count == 1) chosen = byHertzon[0];
            else if (byHertzon.Count > 1)
                return (null, Refuse("hzn.run.sweep-needed", $"Setup '{setup}' has {byHertzon.Count} sweeps ({string.Join(", ", byHertzon.Select(s => $"'{s.Name}'"))}); name one with --setup <name>."));
            else
                return (null, Refuse("hzn.run.no-such-setup", $"Design '{design}' has no setup named '{setup}'; it translates to {List()}."));
        }
        if (chosen.Refusal is { } why) return (null, Refuse("hzn.run.setup-unreadable", why));

        string documentPath = DocumentPath(hznPath, design);
        return (new HznRunPlan
        {
            HznPath = Path.GetFullPath(hznPath),
            Design = design,
            Translation = t,
            C3dSetup = chosen.Name,
            HznSetup = t.Provenance.Setups.GetValueOrDefault(chosen.Name, chosen.Name),
            Setup = C3dSetups.ForRun(chosen.Setup!, documentPath),
            DocumentPath = documentPath,
            ResultsRoot = ResultsRoot(hznPath),
        }, null);
    }

    private static Diagnostic Refuse(string id, string text) => Diagnostic.Create(id, DiagnosticSeverity.Error, "{text}", ("text", text));

    /// <summary>The refusal for a solver named on a <c>.hzn</c> run: the setups are written for EMerge.</summary>
    public static Diagnostic OnlyEmerge(string solver) => Refuse("hzn.run.only-emerge",
        $"A Hertzon design runs on EMerge here: its setups are written for it, and translating them to '{solver}' settings is not offered.");

    // ── what the run does not use ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one note naming every Hertzon setting the run does not use, with its value: the chosen setup's and sweep's own, and
    /// those of the ports and boundaries (an integration line, a radiation order). Null when there is none.
    /// </summary>
    public static string? UnusedNote(HznRunPlan plan) => UnusedNote(plan.Translation, plan.HznSetup);

    /// <summary><see cref="UnusedNote(HznRunPlan)"/> for a translation held by an open editor and the Hertzon setup (or "Setup/Sweep") the
    /// run takes: <see cref="C3dTranslation.Provenance"/> maps a <c>.c3d</c> setup's name to it.</summary>
    public static string? UnusedNote(C3dTranslation translation, string hznSetup)
    {
        var plan = (Translation: translation, HznSetup: hznSetup);
        string[] origin = plan.HznSetup.Split('/', 2);
        string setupOwner = "setup " + origin[0];
        string? sweepOwner = origin.Length > 1 ? $"sweep {origin[0]}/{origin[1]}" : null;
        var mine = plan.Translation.Provenance.Unused
            .Where(u => !u.Owner.StartsWith("setup ", StringComparison.Ordinal) && !u.Owner.StartsWith("sweep ", StringComparison.Ordinal)
                        || u.Owner == setupOwner || u.Owner == sweepOwner)
            .DistinctBy(u => (u.Owner, u.Key, u.Value))
            .ToList();
        if (mine.Count == 0) return null;
        return $"Hertzon settings not used by circuitRF's run ({mine.Count}): " +
               string.Join("; ", mine.Select(u => $"{u.Owner}: {u.Key} = {(u.Value.Length == 0 ? "(empty)" : u.Value)}")) + ".";
    }

    /// <summary>The run's notes that come from the translation: its own sentences, then the unused-settings note.</summary>
    public static IReadOnlyList<string> RunNotes(HznRunPlan plan)
    {
        var notes = new List<string>(plan.Translation.Notes);
        if (UnusedNote(plan) is { } unused) notes.Add(unused);
        return notes;
    }

    // ── running ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The elaborator a run (and a view) of this plan builds with: it carries the project's materials.</summary>
    public static C3dElaborator Elaborator(HznRunPlan plan, GeometryKernel? kernel = null)
        => new(kernel: kernel) { AttachedTechnology = plan.Translation.Technology };

    /// <summary>
    /// Runs the plan through <see cref="EmRunService.RunThreeDView"/> — the one door a native <c>.c3d</c> runs through — and
    /// adds the translation's notes to the result. The run's record names the <c>.hzn</c> as its setup source.
    /// </summary>
    public static EmRunResult Run(HznRunPlan plan, CancellationToken ct = default, RunControl? control = null, int? maxCores = null,
                                  Func<CircuitRF.Design.Em3d.Em3dMemoryVerdict, bool>? confirmMemory = null,
                                  long? memoryCeilingBytes = null,
                                  CircuitRF.Design.Em3d.EmergeDevice emergeDevice = CircuitRF.Design.Em3d.EmergeDevice.Auto,
                                  string? touchstoneDestination = null)
    {
        var target = new EmRunTarget
        {
            TouchstoneDestination = touchstoneDestination,
            SetupPath = plan.HznPath,
            EmbeddedSetup = plan.C3dSetup,
        };
        var result = EmRunService.RunThreeDView(plan.Setup, plan.Document, plan.DocumentPath, null, plan.ResultsRoot, ct, control, maxCores,
                                                confirmMemory, memoryCeilingBytes: memoryCeilingBytes, emergeDevice: emergeDevice,
                                                elaborator: Elaborator(plan), target: target);
        var notes = RunNotes(plan);
        return notes.Count == 0 ? result : result with { Notes = [.. notes, .. result.Notes ?? []] };
    }
}
