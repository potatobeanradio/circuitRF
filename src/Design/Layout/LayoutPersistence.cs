using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout;

// ──────────────────────────────────────────────────────────────────────────────
//  .clay file format — rev 1 (alpha, no back-compat per policy).
//  Mirrors SymbolPersistence.cs conventions:
//    - System.Text.Json, enum-as-string, PascalCase, no naming policy
//    - format_version: reject on mismatch (alpha policy)
//    - Id never persisted (LayoutView/shapes carry no Id at all)
//    - Written through AtomicFile
//  Extra: LoadFromFile sniffs the gzip magic bytes and transparently decompresses if present, so a
//  future gzip writer needs no format-version bump (docs/design/layout-view.md §4).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class ClayFile
{
    public int FormatVersion { get; set; } = 1;
    public int DbuPerMicron { get; set; } = LayoutUnits.DefaultDbuPerMicron;
    public LayoutUnit DisplayUnit { get; set; } = LayoutUnit.Um;
    public long SnapDbu { get; set; }
    public AngleMode AngleMode { get; set; } = AngleMode.AnyAngle;

    /// <summary>Relative path to a .ctech.</summary>
    public string? TechRef { get; set; }

    /// <summary>Non-null when this view's <see cref="Shapes"/> were PCell-generated
    /// (pcell-contract.md R1, <see cref="Layout.PCellOrigin"/>).</summary>
    public ClayPCellOrigin? PCellOrigin { get; set; }

    /// <summary>L5 R-L5-11: see <see cref="LayoutView.SchematicPCellSnapshots"/>.</summary>
    public Dictionary<string, Dictionary<string, PCellValue>>? SchematicPCellSnapshots { get; set; }

    /// <summary>brief-L5-followups-2.md §4.2/R-L5g-6: see <see cref="LayoutView.PCellSnapshots"/>.</summary>
    public Dictionary<string, ClayPCellSnapshot>? PCellSnapshots { get; set; }

    /// <summary>See <see cref="LayoutView.Pins"/>. Additive — omitted when empty, so every existing
    /// pin-free <c>.clay</c> re-serializes byte-for-byte and needs no <see cref="FormatVersion"/>
    /// bump.</summary>
    public List<LayoutPin>? Pins { get; set; }

    /// <summary>See <see cref="LayoutView.DrcWaivers"/> (docs/design/layout-view.md §9A.1: a waiver must
    /// be "per-violation, persisted, and visible"). Additive — omitted when empty, so every existing
    /// waiver-free <c>.clay</c> re-serializes byte-for-byte and needs no <see cref="FormatVersion"/>
    /// bump.</summary>
    public List<Drc.DrcWaiver>? DrcWaivers { get; set; }

    /// <summary>See <see cref="LayoutView.Rulers"/> (docs/design/layout-view.md §9B.7, R-rul-15).
    /// Additive — omitted when empty, so every existing ruler-free <c>.clay</c> re-serializes
    /// byte-for-byte and needs no <see cref="FormatVersion"/> bump.</summary>
    public List<RulerAnnotation>? Rulers { get; set; }

    public List<LayoutShape> Shapes { get; set; } = [];
    public List<LayoutInstance> Instances { get; set; } = [];
}

/// <summary>On-disk shape of <see cref="Layout.PCellSnapshot"/> — mirrors <see cref="ClayPCellOrigin"/>'s
/// own reasoning (a concrete <see cref="Dictionary{TKey,TValue}"/> for <c>Parameters</c>, never the
/// model's own <c>IReadOnlyDictionary</c>).</summary>
public sealed class ClayPCellSnapshot
{
    public string GeneratorId { get; set; } = "";
    public Dictionary<string, PCellValue> Parameters { get; set; } = new();
    public string? TechIdentity { get; set; }
    public string? SignalLayerNameOverride { get; set; }
    public string? GroundLayerNameOverride { get; set; }
}

/// <summary>On-disk shape of <see cref="Layout.PCellOrigin"/> — a dedicated DTO (concrete
/// <see cref="Dictionary{TKey,TValue}"/> rather than the model's own <c>IReadOnlyDictionary</c>) so
/// System.Text.Json's record-constructor deserialization has an unambiguous concrete type to bind,
/// matching every other <c>ClayFile</c>-adjacent DTO's convention of never persisting an interface-
/// typed property directly.</summary>
public sealed class ClayPCellOrigin
{
    public string GeneratorId { get; set; } = "";
    public Dictionary<string, PCellValue> Parameters { get; set; } = new();

    /// <summary>The parameters the generator DERIVED on the run that produced this cell, and what it
    /// derived them to (the value is absent for one the generator names but cannot state).
    ///
    /// <para><b>Persisted with the generated cell, not cached in memory, and that is what makes it
    /// worth anything.</b> A generated cell folder is reused on a plain existence check — nothing is
    /// re-generated on a hit — so a derived value held only in RAM would be present for the cell you
    /// just placed and absent for every cell already on disk, which is the same parameter list
    /// behaving two different ways for no reason the user can see. These ride in the cell's own
    /// <c>.clay</c> alongside the parameters that produced them, so a cache hit carries them too.
    /// They are NOT inputs to the folder's content hash — <c>BuildCellName</c> hashes the parameters
    /// it was given, and an output is a function of those, so hashing it would only add a second
    /// spelling of the same thing.</para></summary>
    public List<string>? ComputedParameters { get; set; }
    public Dictionary<string, PCellValue>? ComputedValues { get; set; }

    /// <summary>The parameters the run that drew this cell never read — for the same reason as the
    /// two above: a cache hit does not re-run the generator, so a measurement made at generation
    /// time is only knowable later if it travels with the cell.</summary>
    public List<string>? UnreadParameters { get; set; }
}

/// <summary>Reads and writes .clay files. Framework-free (no Avalonia / Skia).</summary>
public static class LayoutPersistence
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter(), new PCells.PCellValueJsonConverter() },
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Serialize(LayoutView view)
        => JsonSerializer.Serialize(ToFileModel(view), JsonOpts);

    public static void SaveToFile(string path, LayoutView view)
        => AtomicFile.WriteAllText(path, Serialize(view));

    // ── Read ──────────────────────────────────────────────────────────────────

    public static LayoutView Deserialize(string json) => FromFileModel(ParseFile(json), default, null);

    /// <summary>JSON text to the on-disk file model, version check included — split out of
    /// <see cref="Deserialize"/> so the interruptible overload of <see cref="LoadFromFile(string,
    /// CancellationToken, Action{int, int})"/> can put a cancellation point between the parse and the
    /// shape loop without a second copy of the version rule.</summary>
    private static ClayFile ParseFile(string json)
    {
        var file = JsonSerializer.Deserialize<ClayFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .clay file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".clay format_version {file.FormatVersion} is newer than " +
                $"expected {CurrentFormatVersion}. Update the application.");

        return file;
    }

    public static LayoutView LoadFromFile(string path) => LoadFromFile(path, default, null);

    /// <summary>
    /// <see cref="LoadFromFile(string)"/>, reported on and interruptible — for the caller that has
    /// moved this read onto a background thread and owes the user a progress row and a Cancel.
    ///
    /// <para><b>Both hooks land on the SHAPE LOOP, and that is where the time is.</b> Reading a
    /// layout is not proportional to how big the file looks: <see cref="LayoutClipper.EnsureValidHoles"/>
    /// runs over every shape, and on a Gerber-imported board — thousands of composited pours, each
    /// with hundreds of holes — that is seconds to tens of seconds, against a JSON parse measured in
    /// hundreds of milliseconds. So the loop is both the only place a cancel can land promptly and the
    /// only phase with an honest denominator; the parse ahead of it is one indeterminate step.</para>
    ///
    /// <para>Cancelling throws <see cref="OperationCanceledException"/> rather than returning a
    /// half-built view — a partially loaded layout is indistinguishable from a corrupt one to
    /// everything downstream, and this is a document the user is waiting to see, not a partial result
    /// worth salvaging.</para>
    /// </summary>
    /// <param name="onShapesLoaded">Called as (loaded, total) at intervals during the shape loop, on
    /// the loading thread. Deliberately not per shape: at a few hundred thousand shapes the callback
    /// would cost more than the work it reports on.</param>
    public static LayoutView LoadFromFile(string path, CancellationToken cancellation,
                                          Action<int, int>? onShapesLoaded)
    {
        string text = GzipTextFile.ReadAllTextAutoGzip(path);
        cancellation.ThrowIfCancellationRequested();
        var file = ParseFile(text);
        cancellation.ThrowIfCancellationRequested();
        var view = FromFileModel(file, cancellation, onShapesLoaded);
        ResolveRelativeBitmapPaths(view, Path.GetDirectoryName(Path.GetFullPath(path)));
        return view;
    }

    /// <summary>
    /// Turns a relative <see cref="BitmapShape.ImagePathRef"/> into an absolute path against the
    /// <c>.clay</c>'s own folder — the convention the field has always documented ("absolute, or
    /// relative to the containing <c>.clay</c>"), and the one <c>SchematicPersistence</c> already
    /// applies to <c>.csch</c> bitmaps.
    ///
    /// <para>Load-time rather than render-time because the renderer is handed a model, not a path:
    /// <c>BitmapCache.Load</c> hands the string straight to Skia, which resolves a relative path
    /// against the PROCESS working directory — i.e. against somewhere the document knows nothing
    /// about. It matters now because an archived workspace stores exactly such a relative reference,
    /// so that an underlay copied into the archive still resolves on the machine it is unpacked
    /// on.</para>
    /// </summary>
    internal static void ResolveRelativeBitmapPaths(LayoutView view, string? layoutDir)
    {
        if (string.IsNullOrEmpty(layoutDir)) return;

        foreach (var bmp in view.Shapes.OfType<BitmapShape>())
            if (!string.IsNullOrEmpty(bmp.ImagePathRef) && !Path.IsPathFullyQualified(bmp.ImagePathRef))
                bmp.ImagePathRef = Path.GetFullPath(
                    Path.Combine(layoutDir, bmp.ImagePathRef.Replace('/', Path.DirectorySeparatorChar)));
    }

    // ── Convert LayoutView <-> ClayFile ───────────────────────────────────────

    private static ClayFile ToFileModel(LayoutView view) => new()
    {
        FormatVersion = CurrentFormatVersion,
        DbuPerMicron  = view.DbuPerMicron,
        DisplayUnit   = view.DisplayUnit,
        SnapDbu       = view.SnapDbu,
        AngleMode     = view.AngleMode,
        TechRef       = view.TechRef,
        PCellOrigin   = view.PCellOrigin is { } o ? new ClayPCellOrigin
        {
            GeneratorId        = o.GeneratorId,
            Parameters         = new Dictionary<string, PCellValue>(o.Parameters),
            ComputedParameters = o.ComputedParameters is { Count: > 0 } c ? [.. c] : null,
            ComputedValues     = o.ComputedValues is { Count: > 0 } v ? new Dictionary<string, PCellValue>(v) : null,
            UnreadParameters   = o.UnreadParameters is { Count: > 0 } u ? [.. u] : null,
        } : null,
        SchematicPCellSnapshots = view.SchematicPCellSnapshots.Count > 0
            ? view.SchematicPCellSnapshots.ToDictionary(kv => kv.Key, kv => new Dictionary<string, PCellValue>(kv.Value))
            : null,
        PCellSnapshots = view.PCellSnapshots.Count > 0
            ? view.PCellSnapshots.ToDictionary(kv => kv.Key, kv => new ClayPCellSnapshot
            {
                GeneratorId = kv.Value.GeneratorId,
                Parameters = new Dictionary<string, PCellValue>(kv.Value.Parameters),
                TechIdentity = kv.Value.TechIdentity,
                SignalLayerNameOverride = kv.Value.SignalLayerNameOverride,
                GroundLayerNameOverride = kv.Value.GroundLayerNameOverride,
            })
            : null,
        Pins          = view.Pins.Count > 0 ? [.. view.Pins] : null,
        DrcWaivers    = view.DrcWaivers.Count > 0 ? [.. view.DrcWaivers] : null,
        Rulers        = view.Rulers.Count > 0 ? [.. view.Rulers] : null,
        Shapes        = [.. view.Shapes],
        Instances     = [.. view.Instances],
    };

    private static LayoutView FromFileModel(ClayFile file, CancellationToken cancellation,
                                            Action<int, int>? onShapesLoaded)
    {
        var view = new LayoutView
        {
            DbuPerMicron = file.DbuPerMicron,
            DisplayUnit  = file.DisplayUnit,
            SnapDbu      = file.SnapDbu,
            AngleMode    = file.AngleMode,
            TechRef      = file.TechRef,
            PCellOrigin  = file.PCellOrigin is { } o
                ? new PCellOrigin(o.GeneratorId, o.Parameters, o.ComputedParameters, o.ComputedValues,
                                  o.UnreadParameters)
                : null,
        };

        if (file.SchematicPCellSnapshots is not null)
            foreach (var kv in file.SchematicPCellSnapshots)
                view.SchematicPCellSnapshots[kv.Key] = new Dictionary<string, PCellValue>(kv.Value);

        if (file.PCellSnapshots is not null)
            foreach (var kv in file.PCellSnapshots)
                view.PCellSnapshots[kv.Key] = new PCellSnapshot(
                    kv.Value.GeneratorId, new Dictionary<string, PCellValue>(kv.Value.Parameters),
                    kv.Value.TechIdentity, kv.Value.SignalLayerNameOverride, kv.Value.GroundLayerNameOverride);

        // Every ReportEvery shapes rather than every shape: at these counts a callback and a token
        // read per shape would cost more than the normalization they are reporting on, and a progress
        // bar cannot show more than a few dozen steps anyway.
        const int ReportEvery = 256;
        int total = file.Shapes.Count, loaded = 0;
        foreach (var shape in file.Shapes)
        {
            PadEdgesIfShort(shape);
            // §3.1a R10b / R-L1e-0: a hand-edited (or otherwise not-Clipper2-produced) shape may carry
            // an invalid hole — enforce validity on load rather than trust it. A no-op for the
            // overwhelming common case (no holes, or holes already valid).
            foreach (var normalized in LayoutClipper.EnsureValidHoles(shape))
                view.Shapes.Add(normalized);

            if (++loaded % ReportEvery != 0) continue;
            cancellation.ThrowIfCancellationRequested();
            onShapesLoaded?.Invoke(loaded, total);
        }
        onShapesLoaded?.Invoke(total, total);
        view.Instances.AddRange(file.Instances);
        if (file.Pins is not null) view.Pins.AddRange(file.Pins);
        if (file.DrcWaivers is not null) view.DrcWaivers.AddRange(file.DrcWaivers);
        if (file.Rulers is not null) view.Rulers.AddRange(file.Rulers);

        return view;
    }

    /// <summary>A shorter-than-expected Edges list is padded with Line edges on load — graceful
    /// within-version load (docs/design/layout-view.md §3.2 R9a).</summary>
    private static void PadEdgesIfShort(LayoutShape shape)
    {
        switch (shape)
        {
            case CurveShape { Edges: { } edges } curve:
                PadTo(edges, curve.Xy.Length / 2);
                break;
            case PathShape { Edges: { } edges } path:
                PadTo(edges, Math.Max(0, path.Xy.Length / 2 - 1));
                break;
        }
    }

    private static void PadTo(List<LayoutEdge> edges, int expectedCount)
    {
        while (edges.Count < expectedCount)
            edges.Add(new LayoutEdge());
    }
}

/// <summary>Shared gzip-sniffing text reader for .clay / .ctech (docs/design/layout-view.md §4):
/// writers only ever emit plain JSON in v1, but a reader that already sniffs the gzip magic bytes
/// makes a future gzip writer a write-side-only change with no format-version bump.
///
/// <para>Public rather than internal because a second consumer outside this assembly reads the same
/// bytes: <c>CellViewFileValidator</c> checks a candidate <c>.clay</c>'s own JSON keys before a cell
/// is built from it, and a private copy of the magic-byte sniff is exactly the sort of thing that
/// drifts the day a writer starts emitting gzip.</para></summary>
public static class GzipTextFile
{
    private static readonly byte[] GzipMagic = [0x1F, 0x8B];

    public static string ReadAllTextAutoGzip(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == GzipMagic[0] && bytes[1] == GzipMagic[1])
        {
            using var input  = new MemoryStream(bytes);
            using var gzip   = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
