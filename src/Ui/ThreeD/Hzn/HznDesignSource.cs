// HZN-3 R-hzn3-2 — a 3D design held in a Hertzon project, as the 3D editor's document.
//
// Loading is HznToC3d.Translate on a fresh read of the file, so reloading picks up what Hertzon saved. The project's
// materials are the document's own (brief-hzn-0 D6): the translation's technology is held in the technology cache under
// a path that names no file, and the document's TechRef points at it. Nothing is written to the workspace's technology.
//
// Saving (HZN-5) rewrites only what was edited: the document as opened is kept as the baseline, and a save applies the
// difference between it and the edited document to a copy of the file's tree (HznPatcher). A save that cannot be traced back to
// the file is refused with its reasons, and the file is not touched. The file's change-on-disk check (HZN-1) decides whether it
// is written at all.

using System.Security.Cryptography;
using System.Text;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.ThreeD.Hzn;

/// <summary>A design that cannot be opened: the whole refusal, every item at once.</summary>
public sealed class HznOpenException(Diagnostic refusal, IReadOnlyList<HznRefusalItem> items) : Exception(refusal.Render())
{
    public Diagnostic Refusal { get; } = refusal;
    public IReadOnlyList<HznRefusalItem> Items { get; } = items;
}

public sealed class HznDesignSource : IC3dDocumentSource
{
    private readonly TechnologyCache _technologies;
    private readonly GeometryKernel? _kernel;
    private HznFile? _file;
    private HznSaveBaseline? _baseline;

    public HznDesignSource(string hznPath, string design, TechnologyCache technologies, GeometryKernel? kernel = null)
    {
        FilePath = Path.GetFullPath(hznPath);
        Design = design;
        _technologies = technologies;
        _kernel = kernel;
        Key = KeyFor(FilePath, design);
        TechnologyPath = Path.Combine(Path.GetDirectoryName(FilePath)!, TechnologyName(FilePath, design));
        RunTechnologyPath = Path.Combine(HznEmergeRun.ScratchFolder(FilePath), TechnologyName(FilePath, design));
    }

    /// <summary>The open-document key of one design of one project.</summary>
    public static string KeyFor(string hznPath, string design) => Path.GetFullPath(hznPath) + "#" + design;

    public string Key { get; }
    public string FilePath { get; }
    public string Design { get; }
    public string Title => $"{Design} — {Path.GetFileName(FilePath)}";

    /// <summary>A design held in a project is saved back into it (HZN-5).</summary>
    public string? ReadOnlyReason => null;

    /// <summary>True when the last <see cref="Save"/> was refused because the file changed on disk since it was opened.</summary>
    public bool LastSaveChangedOnDisk { get; private set; }

    /// <summary>What the last successful <see cref="Save"/> did that the user should know: a boundary that went with a deleted
    /// object, a report that now names nothing.</summary>
    public IReadOnlyList<string> LastSaveNotes { get; private set; } = [];

    /// <summary>The path the project's materials are held under. It names no file.</summary>
    public string TechnologyPath { get; }

    /// <summary>The same materials under the path a run of the design resolves them from: a run is filed by a virtual path in the
    /// project's scratch folder (<see cref="C3dEditorViewModel.RunPath"/>), and a relative <c>TechRef</c> resolves from the document's folder.</summary>
    public string RunTechnologyPath { get; }

    /// <summary>The last translation <see cref="Load"/> produced: its provenance and notes, for the briefs that save and run.</summary>
    public C3dTranslation? Translation { get; private set; }

    public C3dDocument Load()
    {
        var read = HznFile.Read(FilePath);
        if (!read.Ok) throw new HznOpenException(read.Refusal!, []);
        var t = HznToC3d.Translate(read.File!.Project, Design, _kernel);
        if (!t.Ok) throw new HznOpenException(t.Refusal!, t.Refusals);
        Translation = t;
        _technologies.Hold(TechnologyPath, t.Technology!);
        _technologies.Hold(RunTechnologyPath, t.Technology!);
        t.Document!.TechRef = Path.GetFileName(TechnologyPath);
        // A Hertzon design opens with every chrome row of the realistic view shown (owner request): a .hzn holds no Look, so this is
        // the view it opens in each time, and Look is not one of the parts a save writes back. Whether it opens IN the realistic
        // view is still the per-user Settings choice (Realistic3DPreference), applied by the workspace after the editor starts.
        t.Document.Look = HznLook(t.Document.Look);
        _file = read.File;
        _baseline = new HznSaveBaseline(C3dPersistence.Deserialize(C3dPersistence.Serialize(t.Document)), t.Provenance, t.Technology!);
        return t.Document;
    }

    /// <summary>The Look a Hertzon design opens with: Edges, Grid, Overlays, Air box, Ports, Boundaries and Images shown, the rest as
    /// <paramref name="look"/> has it.</summary>
    public static C3dLook HznLook(C3dLook? look)
    {
        var l = look?.Clone() ?? new C3dLook();
        l.ShowEdges = l.ShowGrid = l.ShowOverlays = l.ShowAirBox = l.ShowPorts = l.ShowBoundaries = l.ShowImages = true;
        return l;
    }

    /// <summary>The edits <paramref name="document"/> holds against what was opened, by name: what is lost if the file is reloaded.</summary>
    public IReadOnlyList<string> EditSummary(C3dDocument document) =>
        _baseline is null ? [] : HznPatcher.Summarise(_baseline, document);

    /// <summary>The materials the document names, as the project holds them: what a copy of the document is converted with.</summary>
    public Technology? ProjectTechnology => _baseline?.Technology;

    /// <summary>Lets go of the held materials when the tab closes.</summary>
    public void Release()
    {
        _technologies.Release(TechnologyPath);
        _technologies.Release(RunTechnologyPath);
    }

    public string? Save(C3dDocument document)
    {
        LastSaveChangedOnDisk = false;
        LastSaveNotes = [];
        if (_file is null || _baseline is null) return $"'{Design}' was not opened from '{Path.GetFileName(FilePath)}', so there is nothing to save it into.";

        // The edit is applied to a COPY of the file's tree: a refused save leaves the file, and the tree it was opened with, as they were.
        var work = HznJsonDocument.Parse(_file.ToBytes());
        var outcome = HznPatcher.Apply(new HznProject((HznJsonObject)work.Root), Design, _baseline, document, _kernel);
        if (!outcome.Ok)
            return $"'{Design}' was not saved to '{Path.GetFileName(FilePath)}', which is unchanged:\n  " +
                   string.Join("\n  ", outcome.Refusals.SelectMany(r => r.Split('\n')));

        var result = _file.Save(work);
        if (!result.Saved)
        {
            LastSaveChangedOnDisk = string.Equals(result.Refusal?.Id, "hzn.save.changed-on-disk", StringComparison.Ordinal);
            return result.Refusal?.Render() ?? "The file could not be written.";
        }
        _baseline = new HznSaveBaseline(C3dPersistence.Deserialize(C3dPersistence.Serialize(document)), outcome.Readback!.Provenance, _baseline.Technology);
        LastSaveNotes = outcome.Notes;
        return null;
    }

    public string? Stamp()
    {
        try
        {
            var f = new FileInfo(FilePath);
            return f.Exists ? $"{f.LastWriteTimeUtc.Ticks}:{f.Length}" : null;
        }
        catch (Exception) { return null; }
    }

    private static string TechnologyName(string hznPath, string design)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(hznPath) + "\n" + design));
        return $"hzn-materials-{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}.ctech";
    }
}
