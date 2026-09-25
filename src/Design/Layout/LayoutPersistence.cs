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
//
//  THAT RESERVE IS REVOKED, AND THIS IS A WARNING RATHER THAN AN INVITATION (RC-3 R-rc3-18,
//  docs/design/revision-control.md §3.2). Measured on a real 28.4 MB board, plain vs gzipped, under
//  version control:
//
//      20 shape ADDITIONS         2.1 KB/commit  vs  6.6 KB/commit      3x
//      20 mid-file POLYGON DRAGS  5.3 KB/commit  vs  2.69 MB/commit     ~508x
//      5  mid-file DELETIONS      1.3 KB/commit  vs  2.14 MB/commit     ~1,600x
//      final .git after 46        5.12 MB        vs  74.22 MB           14.5x
//
//  STATE THE TRAP PRECISELY, BECAUSE AN APPEND-ONLY TEST REPORTS A FALSE PASS: deflate resynchronises
//  after an append, so the addition row looks almost respectable. It is the MID-FILE edit — moving one
//  polygon, which is what designing actually consists of — that destroys the delta, and deletion is
//  worse still.
//
//  The general rule, which also governs .npy: COMPRESSED OR BINARY CONTENT DOES NOT DELTA. A format
//  that saves disk once costs the repository a full copy on every save. In a versioned workspace,
//  plain text IS the compressed format.
//
//  Nothing else about .clay changes: 46 design commits cost 197 KB against a 28.4 MB layout, so no
//  change to how this file is written is required and the minimal-diff serializer that investigation
//  set out to design is unnecessary. A full reorder of 3,284 shapes costs 41 KB, because git's delta
//  compression is content-based rather than line-based — preserve shape order for the HUMAN reading a
//  diff, and never let git be the reason a serializer is constrained.
//
//  WHAT DID CHANGE, 2026-09-23, AND WHY IT IS NOT A CONTRADICTION OF THE ABOVE: the WORKING COPY. A
//  Gerber-imported board reached 57 MB on disk from a 359 kB zip, most of it the indented writer's
//  number-per-line and two-space indent. Coordinates are now a vertex per line
//  (CoordinatePairsJsonConverter), indented with one tab per level, with LF on every platform — 7.33
//  → 4.66 MB on a 6,868-shape board. Still plain text, still one line per vertex, so a moved vertex is
//  still a one-line diff. The reader is unchanged; every older file still opens.
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

    /// <summary>See <see cref="LayoutView.LvsWaivers"/> (brief-lvs-12-gui.md R-lvs12-4e). Additive —
    /// omitted when empty, so every existing <c>.clay</c> re-serializes byte-for-byte and needs no
    /// <see cref="FormatVersion"/> bump.</summary>
    public List<Lvs.LvsWaiver>? LvsWaivers { get; set; }

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

        // ── THE .clay IS WRITTEN FOR git DIFF (field report, 2026-09-23) ────────────────────────
        //
        // A tab per level rather than two spaces: leading whitespace was a third of an imported
        // board's file. And LF on every platform: the workspace's .gitattributes marks .clay `-text`
        // so the bytes on disk are the bytes written — which the platform default NewLine defeated,
        // since a board saved on Windows and then on macOS differed on every line.
        IndentCharacter             = '\t',
        IndentSize                  = 1,
        NewLine                     = "\n",
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        // CoordinatePairsJsonConverter: a vertex per line, not a number per line — half the file,
        // and still a one-line diff for one moved vertex.
        Converters                  = { new JsonStringEnumConverter(), new PCells.PCellValueJsonConverter(),
                                        new CoordinatePairsJsonConverter() },
        // Every object contract also catches the keys the read does not bind, so a load can SAY it
        // ignored one (LayoutLoadAudit): a misspelt "Xy" used to empty a polygon without a word.
        TypeInfoResolver            = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
                                      { Modifiers = { LayoutLoadAudit.CaptureUnknownFields } },
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Serialize(LayoutView view)
        => JsonSerializer.Serialize(ToFileModel(view), JsonOpts);

    public static void SaveToFile(string path, LayoutView view)
        => AtomicFile.WriteAllText(path, Serialize(view));

    // ── Read ──────────────────────────────────────────────────────────────────

    public static LayoutView Deserialize(string json)
    {
        var (file, captured) = ParseFile(json);
        return WithUnknownFields(FromFileModel(file, default, null), captured, file);
    }

    /// <summary>JSON text to the on-disk file model, version check included — split out of
    /// <see cref="Deserialize"/> so the interruptible overload of <see cref="LoadFromFile(string,
    /// CancellationToken, Action{int, int})"/> can put a cancellation point between the parse and the
    /// shape loop without a second copy of the version rule.</summary>
    private static (ClayFile File, List<(object Owner, Dictionary<string, JsonElement> Keys)> Captured) ParseFile(string json)
    {
        var (file, captured) = LayoutLoadAudit.Capturing(() => JsonSerializer.Deserialize<ClayFile>(json, JsonOpts));
        if (file is null) throw new InvalidDataException("Failed to deserialize .clay file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".clay format_version {file.FormatVersion} is newer than " +
                $"expected {CurrentFormatVersion}. Update the application.");

        return (file, captured);
    }

    public static LayoutView LoadFromFile(string path) => LoadFromFile(path, default, null);

    /// <summary>
    /// Whether the <c>.clay</c> at <paramref name="path"/> can carry any
    /// <see cref="LayoutView.PCellSnapshots"/> at all — answered by TOKENIZING the file rather than by
    /// building the document, for the caller whose only interest in a whole workspace's layouts is
    /// that one small dictionary.
    ///
    /// <para><b>Why it exists.</b> Reading a layout is not proportional to how big the file looks, and
    /// on a Gerber-imported board it is seconds rather than milliseconds — almost none of it the JSON
    /// parse, for the reason <see cref="LoadFromFile(string, CancellationToken, Action{int, int})"/>
    /// gives about <see cref="LayoutClipper.EnsureValidHoles"/>. The generated-cell pass on workspace
    /// open reads EVERY layout under the workspace to find these snapshots, so a workspace holding a
    /// few imported boards paid all of that before its window was usable, for dictionaries that in the
    /// overwhelming common case are not there at all. Measured on a three-board workspace
    /// (2026-09-12): 4.96 s of loading became 0.07 s of tokenizing.</para>
    ///
    /// <para><b>A NO is only sound because it is a WELL-FORMED no, which is why this tokenizes rather
    /// than searching the text for the name.</b> The caller's next decision is whether it has seen
    /// every snapshot in the workspace — and a file it could not read is one it must assume carries
    /// some. A substring search answers "not mentioned" for a corrupt file just as confidently as for
    /// a sound one, which would turn an unreadable layout into a licence to collect cells it might
    /// still be using. So the scan runs to the end of the document when the property is absent, and a
    /// file that does not parse throws out of here exactly as <see cref="LoadFromFile(string)"/> would
    /// have. (Absent-and-well-formed is a genuine no even if the file would later fail to BIND: a
    /// document with no such property has no snapshot names to contribute, whatever else is wrong
    /// with it.)</para>
    ///
    /// <para>True over-reports deliberately — a null or empty dictionary still answers true — because
    /// a true answer is an instruction to load the file and ask it properly, and that load is what
    /// used to happen unconditionally. Property names are matched case-insensitively because
    /// <see cref="JsonOpts"/> is: a file spelling it in another case still LOADS its snapshots, so
    /// this must not be the thing that hides them.</para>
    /// </summary>
    /// <exception cref="JsonException">The file is not well-formed JSON.</exception>
    public static bool MightCarryPCellSnapshots(string path)
    {
        var bytes = GzipTextFile.ReadAllBytesAutoGzip(path);
        var json = new ReadOnlySpan<byte>(bytes);
        if (json.StartsWith(Utf8Bom)) json = json[Utf8Bom.Length..];

        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);

        // Depth 1 is the file model's own properties — a nested object of the same name (a label's
        // text is not one, but nothing says a future shape could not have such a field) is not it.
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                continue;
            if (reader.ValueTextEquals(PCellSnapshotsUtf8)
                || string.Equals(reader.GetString(), nameof(ClayFile.PCellSnapshots),
                                 StringComparison.OrdinalIgnoreCase))
                return true;

            // Skip the VALUE, not the property name — Read() above has not consumed it yet.
            reader.Read();
            reader.Skip();
        }

        return false;
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> PCellSnapshotsUtf8 =>
        "PCellSnapshots"u8;

    /// <summary>
    /// <see cref="LoadFromFile(string)"/>, reported on and interruptible — for the caller that has
    /// moved this read onto a background thread and owes the user a progress row and a Cancel.
    ///
    /// <para><b>Both hooks land on the SHAPE LOOP, because that is the only phase with an honest
    /// denominator</b> — the parse ahead of it is one indeterminate step, and a cancel can only land
    /// promptly between shapes.</para>
    ///
    /// <para><b>It is no longer where the time is, and that is worth stating because it WAS.</b>
    /// <see cref="LayoutClipper.EnsureValidHoles"/> runs over every shape, and on a Gerber-imported
    /// board — composited pours, each with hundreds of holes — it used to be seconds to tens of
    /// seconds against a JSON parse of a few hundred milliseconds. Since <c>RingBands</c>
    /// (2026-09-12) the 10.6 MB board that motivated this reads in 68 ms of which 24 ms is the hole
    /// check, so the parse is now the larger half again. The hooks stay where they are: a big enough
    /// file is still a wait, and the shape loop is still the only place they can honestly go.</para>
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
        var (file, captured) = ParseFile(text);
        cancellation.ThrowIfCancellationRequested();
        var view = WithUnknownFields(FromFileModel(file, cancellation, onShapesLoaded), captured, file);
        ResolveRelativeBitmapPaths(view, Path.GetDirectoryName(Path.GetFullPath(path)));
        return view;
    }

    /// <summary>
    /// Puts the keys the read IGNORED in front of whatever the shape loop found — first, because an
    /// unknown key is usually the reason a shape came out degenerate (a <c>Poly</c> spelt with
    /// <c>"Points"</c> is both), and the cause should be read before the symptom. See
    /// <see cref="LayoutLoadAudit"/>.
    /// </summary>
    private static LayoutView WithUnknownFields(
        LayoutView view, List<(object Owner, Dictionary<string, JsonElement> Keys)> captured, ClayFile file)
    {
        var unknown = LayoutLoadAudit.UnknownFields(captured, file);
        if (unknown.Count > 0) view.LoadFindings = [.. unknown, .. view.LoadFindings];
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
                    Path.Combine(layoutDir, CircuitRF.Core.RefPath.ToNative(bmp.ImagePathRef)));
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
        LvsWaivers    = view.LvsWaivers.Count > 0 ? [.. view.LvsWaivers] : null,
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
        var degenerate = new LayoutLoadAudit.DegenerateTally();
        foreach (var shape in file.Shapes)
        {
            degenerate.Add(shape, loaded);
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
        if (file.LvsWaivers is not null) view.LvsWaivers.AddRange(file.LvsWaivers);
        if (file.Rulers is not null) view.Rulers.AddRange(file.Rulers);
        view.LoadFindings = [.. degenerate.Findings()];

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
/// writers only ever emit plain JSON in v1, and a reader that already sniffs the gzip magic bytes
/// means an incoming gzipped file still opens.
///
/// <para><b>Do not read this as an invitation to add the WRITER</b> (RC-3 R-rc3-18,
/// <c>revision-control.md</c> §3.2). Measured on a real 28.4 MB board, gzipping <c>.clay</c> costs a
/// repository <b>~508x per mid-file polygon drag</b>, ~1,600x per deletion, and 14.5x on the finished
/// <c>.git</c> — because deflate destroys git's delta on any edit that is not an append. The
/// APPEND-ONLY case is the trap: it measures 3x and looks almost respectable, so a test written from
/// intuition reports a false pass. The file header carries the whole table.</para>
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

    /// <summary><see cref="ReadAllTextAutoGzip"/> without the decode — for a reader that works in
    /// UTF-8 bytes (<see cref="Utf8JsonReader"/>) and would only have to encode the string back.</summary>
    public static byte[] ReadAllBytesAutoGzip(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 2 || bytes[0] != GzipMagic[0] || bytes[1] != GzipMagic[1]) return bytes;

        using var input  = new MemoryStream(bytes);
        using var gzip   = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
