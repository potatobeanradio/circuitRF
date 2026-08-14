using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Devices.External;

namespace CircuitRF.Harmonica;

/// <summary>
/// R-hrf-11 / D10 — the <c>.charm</c> file (harmonicarf.md §8). JSON, versioned, following the
/// <c>DataDisplayConfig</c> pattern: a role or field absent from the file resolves to its built-in
/// default, so an old file still opens after new fields are added.
///
/// <para><b>Setup only — no results.</b> The file is re-solved on open. At ~0.5 s for a full map that
/// is cheap, and it eliminates an entire class of stale-data bug.</para>
///
/// <para><b>Embed or reference, per D10.</b> An SDD or a built-in model is stored WHOLE, equation
/// text included, so a <c>.charm</c> is self-contained and portable. A Verilog-A <c>.osdi</c> or a
/// vendor kit is stored as a REFERENCE — the artifact is a compiled binary or a licensed kit, and a
/// file that embedded it would be neither portable nor legal. Opening a <c>.charm</c> whose reference
/// cannot be resolved says WHICH file is missing; it does not fail silently and it does not
/// substitute another model.</para>
///
/// <para><b>Touchstone files are referenced by BARE FILENAME</b> and resolved relative to the
/// <c>.charm</c> — the same portability rule <c>.cdd</c> follows (R-dd-6), and the reason a project
/// folder can be moved or sent to someone else and still open.</para>
/// </summary>
public static class CharmIo
{
    /// <summary>Bumped when a change cannot be expressed as "absent field takes its default".</summary>
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A model reference that could not be resolved when the file was opened.</summary>
    /// <param name="Kind">What sort of artifact is missing — "model" or "embedding".</param>
    /// <param name="Reference">Exactly what the file named.</param>
    /// <param name="Message">What to tell the user.</param>
    public readonly record struct UnresolvedReference(string Kind, string Reference, string Message);

    /// <summary>Everything one <c>.charm</c> carries, from one parse.</summary>
    /// <param name="Model">The circuit — DUT, embedding, bias, settings, drive.</param>
    /// <param name="Terminations">The marker set (§4.2). Unmarked bands are absent, by design.</param>
    /// <param name="Appearance">R-h45-12's role maps and iso-line fade parameters.</param>
    /// <param name="Layout">R-h45-1's §7.1 panel placement.</param>
    /// <param name="Unresolved">Every referenced artifact that is not there.</param>
    /// <param name="Traces">R-h7-7's picked traces, as plain (spec, panel, label) data.</param>
    /// <param name="Vswr">R-h9r2-8 — every marker whose VSWR-circle overlay is ON.</param>
    public readonly record struct CharmContents(
        CircuitModel                        Model,
        TerminationSet                      Terminations,
        CharmAppearance                     Appearance,
        CharmLayout                         Layout,
        IReadOnlyList<UnresolvedReference>  Unresolved,
        IReadOnlyList<CharmTrace>           Traces,
        IReadOnlyList<CharmMarkerVswr>      Vswr);

    /// <summary>
    /// R-h7-7 — one picked trace, as the <c>.charm</c> carries it.
    ///
    /// <para><b>Plain data, and deliberately not parsed here.</b> The spec's grammar belongs to
    /// <c>CubeTraceSpecParser</c>, which is <c>src/Ui</c>; this project stores the string. A spec that
    /// no longer resolves therefore survives a round trip and is reported on the panel rather than
    /// dropped at load — the same courtesy an unresolved model reference gets.</para>
    /// </summary>
    public readonly record struct CharmTrace(string Spec, string PanelId, string? Label);

    /// <summary>
    /// R-h9r2-8 — one marker's VSWR-circle overlay, as the <c>.charm</c> carries it. Framework-free
    /// (this project may not reference <c>HarmonicaMarker</c>, which lives in <c>src/Ui</c>), so the
    /// caller supplies and reads a plain list of these rather than the marker objects themselves.
    /// Only markers with the overlay ON are ever in this list — see <see cref="VswrToJson"/>.
    /// </summary>
    public readonly record struct CharmMarkerVswr(TerminationSide Side, int Band, double Value);

    public static string Write(CircuitModel model)
        => JsonSerializer.Serialize(ToDocument(model), Options);

    public static void WriteFile(string path, CircuitModel model)
        => File.WriteAllText(path, Write(model));

    /// <summary>
    /// Reads a <c>.charm</c>. <paramref name="unresolved"/> lists every referenced artifact that is
    /// not there — a compiled model whose file has moved, an embedding file that is missing. The
    /// model still comes back, so the caller can offer to re-point the reference rather than losing
    /// the whole document.
    /// </summary>
    public static CircuitModel Read(string json, string? baseDirectory,
                                    out IReadOnlyList<UnresolvedReference> unresolved)
    {
        var doc = JsonSerializer.Deserialize<CharmDocument>(json, Options)
                  ?? throw new InvalidDataException("the .charm file is empty");

        var model = FromDocument(doc);
        unresolved = FindUnresolved(model, doc, baseDirectory);
        return model;
    }

    public static CircuitModel ReadFile(string path, out IReadOnlyList<UnresolvedReference> unresolved)
        => Read(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)), out unresolved);

    /// <summary>
    /// Resolves an embedding file the way the format promises: a bare filename against the
    /// <c>.charm</c>'s own folder; an absolute path as written.
    /// </summary>
    public static string ResolveRelative(string reference, string? baseDirectory)
        => Path.IsPathRooted(reference) || baseDirectory is null
            ? reference
            : Path.Combine(baseDirectory, reference);

    private static IReadOnlyList<UnresolvedReference> FindUnresolved(
        CircuitModel model, CharmDocument doc, string? baseDirectory)
    {
        var missing = new List<UnresolvedReference>();

        foreach (string file in model.Embedding.TouchstoneFiles)
        {
            string full = ResolveRelative(file, baseDirectory);
            if (!File.Exists(full))
                missing.Add(new UnresolvedReference("embedding", file,
                    $"The embedding file '{file}' was not found beside this .charm " +
                    $"(looked for '{full}'). Point it at the file, or remove that block from the " +
                    "embedding stack."));
        }

        // A compiled or licensed model is a REFERENCE. If the file states one, it must be there —
        // and if it is not, saying which one is missing is the whole point of storing a reference
        // rather than substituting something that would run.
        if (model.Dut.Kind == DutKind.External && doc.Dut?.ModelFile is { Length: > 0 } modelFile)
        {
            string full = ResolveRelative(modelFile, baseDirectory);
            if (!File.Exists(full))
                missing.Add(new UnresolvedReference("model", modelFile,
                    $"The model this .charm was built around, '{modelFile}', was not found " +
                    $"(looked for '{full}'). It is referenced rather than embedded because it is a " +
                    "compiled or licensed artifact. Re-point it to open the document; harmonicaRF " +
                    "will not substitute a different model."));
        }

        return missing;
    }

    // ── the document ──────────────────────────────────────────────────────────

    private static CharmDocument ToDocument(CircuitModel m) => new()
    {
        FormatVersion = CurrentFormatVersion,
        Dut = new CharmDut
        {
            Kind         = m.Dut.Kind.ToString(),
            TypeName     = m.Dut.TypeName,
            Provider     = m.Dut.Provider,
            Multiplicity = m.Dut.Multiplicity,
            // R-h9c-11 — additive, absent ⇒ 2 (CharmIo's own "no FormatVersion bump" rule): omitted
            // entirely at the default so an SDD2 document written before this brief re-serialises
            // byte-for-byte.
            SddPortCount = m.Dut.SddPortCount == 3 ? 3 : (int?)null,
            // Embedded whole for an SDD or a built-in — equation text included. For an external
            // model these are the parameters the kit's own model declares, which are settings rather
            // than the artifact, so they travel too.
            Parameters   = new Dictionary<string, string>(m.Dut.Parameters, StringComparer.Ordinal),
            // The referenced ARTIFACT, when there is a file to reference — and only then.
            //
            // This used to store the provider NAME verbatim, which is not a path in either external
            // case: a compiled model file's provider is the composed `VerilogA|<path>` form, so the
            // existence check resolved a nonsense path and reported a file that was sitting there;
            // and a KIT's provider is a kit name, so every kit-backed .charm would have reported its
            // model missing on every open. H4–H7 could not meet either, because nothing could create
            // an external DUT until Set DUT existed. Kept null for a kit deliberately: a kit is not a
            // file, and its absence is what the resolver reports when the document is solved.
            ModelFile    = m.Dut.Kind == DutKind.External && m.Dut.Provider is { Length: > 0 } p
                ? VerilogAFileResolver.ModelFileIn(p)
                : null,
            IntrinsicGate   = m.Dut.IntrinsicMapping?.GateNode,
            IntrinsicDrain  = m.Dut.IntrinsicMapping?.DrainNode,
            IntrinsicSource = m.Dut.IntrinsicMapping?.SourcePin,
        },
        Embedding = new CharmEmbedding
        {
            // Bare filenames — the .cdd portability rule.
            S2pIn  = BareName(m.Embedding.S2pInFile),
            S2pOut = BareName(m.Embedding.S2pOutFile),
            S4p    = BareName(m.Embedding.S4pFile),
            Rg = m.Embedding.Package.Rg, Lg = m.Embedding.Package.Lg,
            Rd = m.Embedding.Package.Rd, Ld = m.Embedding.Package.Ld,
            Rs = m.Embedding.Package.Rs, Ls = m.Embedding.Package.Ls,
            Cpg = m.Embedding.Package.Cpg, Cpd = m.Embedding.Package.Cpd,
            CgdExt = m.Embedding.Package.CgdExt,
        },
        Bias = new CharmBias { Vds = m.Bias.Vds, Vgs = m.Bias.Vgs, Idq = m.Bias.Idq },
        Settings = new CharmSettings
        {
            HarmonicCount = m.Settings.HarmonicCount,
            FrequencyHz   = m.Settings.FrequencyHz,
            FftOverSample = m.Settings.FftOverSample,
            Tol           = m.Settings.Tol,
            MaxIter       = m.Settings.MaxIter,
            GuardHarmonic = m.Settings.GuardHarmonic,
            Lambda        = m.Settings.Lambda,
            CompressionDb = m.Settings.CompressionDb,
            PinMaxDbm     = m.Settings.PinMaxDbm,
            PinStartDbm   = m.Settings.PinStartDbm,
            ComputeCharge = m.Settings.ComputeCharge,
            BiasChokeH    = m.Settings.BiasChokeHenries,
            DcBlockF      = m.Settings.DcBlockFarads,
            Z0            = m.Settings.Z0,
            LoadlineSamples = m.Settings.LoadlineSamples,
            DcivVgsMin    = m.Settings.DcivVgsMin,
            DcivVgsMax    = m.Settings.DcivVgsMax,
            DcivVgsSteps  = m.Settings.DcivVgsSteps,
            DcivVdsMin    = m.Settings.DcivVdsMin,
            DcivVdsMax    = m.Settings.DcivVdsMax,
            DcivVdsSteps  = m.Settings.DcivVdsSteps,
            PinStepDbm    = m.Settings.PinStepDbm,
            TickleEnabled = m.Settings.TickleEnabled,
            TickleDbm     = m.Settings.TickleDbm,
            ExactCompressionSolve = m.Settings.ExactCompressionSolve,
            SweepOverdriveDb = m.Settings.SweepOverdriveDb,
        },
        PavlDbm = m.PavlDbm,
    };

    private static CircuitModel FromDocument(CharmDocument d)
    {
        var defaults = new HarmonicaSettings();
        var s = d.Settings;
        var dut = d.Dut;

        return new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = Enum.TryParse<DutKind>(dut?.Kind, out var kind) ? kind : DutKind.NativeFet,
                TypeName     = dut?.TypeName ?? "FET_Angelov",
                Provider     = dut?.Provider,
                Multiplicity = dut?.Multiplicity ?? 1.0,
                SddPortCount = dut?.SddPortCount == 3 ? 3 : 2,
                Parameters   = dut?.Parameters is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(dut.Parameters, StringComparer.Ordinal),
                IntrinsicMapping = dut?.IntrinsicGate is { Length: > 0 } g
                                && dut.IntrinsicDrain is { Length: > 0 } dr
                                && dut.IntrinsicSource is { Length: > 0 } sp
                    ? new IntrinsicMapping(g, dr, sp)
                    : null,
            },
            Embedding = new EmbeddingStack
            {
                S2pInFile  = d.Embedding?.S2pIn,
                S2pOutFile = d.Embedding?.S2pOut,
                S4pFile    = d.Embedding?.S4p,
                Package = new LumpedPackage
                {
                    Rg = d.Embedding?.Rg ?? 0, Lg = d.Embedding?.Lg ?? 0,
                    Rd = d.Embedding?.Rd ?? 0, Ld = d.Embedding?.Ld ?? 0,
                    Rs = d.Embedding?.Rs ?? 0, Ls = d.Embedding?.Ls ?? 0,
                    Cpg = d.Embedding?.Cpg ?? 0, Cpd = d.Embedding?.Cpd ?? 0,
                    CgdExt = d.Embedding?.CgdExt ?? 0,
                },
            },
            Bias = new BiasSpec
            {
                Vds = d.Bias?.Vds ?? new BiasSpec().Vds,
                Vgs = d.Bias?.Vgs ?? new BiasSpec().Vgs,
                Idq = d.Bias?.Idq,
            },
            Settings = new HarmonicaSettings
            {
                HarmonicCount = s?.HarmonicCount ?? defaults.HarmonicCount,
                FrequencyHz   = s?.FrequencyHz   ?? defaults.FrequencyHz,
                FftOverSample = s?.FftOverSample ?? defaults.FftOverSample,
                Tol           = s?.Tol           ?? defaults.Tol,
                MaxIter       = s?.MaxIter       ?? defaults.MaxIter,
                GuardHarmonic = s?.GuardHarmonic ?? defaults.GuardHarmonic,
                Lambda        = s?.Lambda        ?? defaults.Lambda,
                CompressionDb = s?.CompressionDb ?? defaults.CompressionDb,
                PinMaxDbm     = s?.PinMaxDbm     ?? defaults.PinMaxDbm,
                PinStartDbm   = s?.PinStartDbm   ?? defaults.PinStartDbm,
                ComputeCharge = s?.ComputeCharge ?? defaults.ComputeCharge,
                BiasChokeHenries = s?.BiasChokeH ?? defaults.BiasChokeHenries,
                DcBlockFarads    = s?.DcBlockF   ?? defaults.DcBlockFarads,
                Z0               = s?.Z0         ?? defaults.Z0,
                LoadlineSamples  = s?.LoadlineSamples ?? defaults.LoadlineSamples,
                DcivVgsMin       = s?.DcivVgsMin,
                DcivVgsMax       = s?.DcivVgsMax,
                DcivVgsSteps     = s?.DcivVgsSteps,
                DcivVdsMin       = s?.DcivVdsMin,
                DcivVdsMax       = s?.DcivVdsMax,
                DcivVdsSteps     = s?.DcivVdsSteps,
                PinStepDbm       = s?.PinStepDbm       ?? defaults.PinStepDbm,
                TickleEnabled    = s?.TickleEnabled    ?? defaults.TickleEnabled,
                TickleDbm        = s?.TickleDbm        ?? defaults.TickleDbm,
                ExactCompressionSolve = s?.ExactCompressionSolve ?? defaults.ExactCompressionSolve,
                SweepOverdriveDb = s?.SweepOverdriveDb ?? defaults.SweepOverdriveDb,
            },
            PavlDbm = d.PavlDbm ?? 0.0,
        };
    }

    private static string? BareName(string? path)
        => path is null ? null : Path.GetFileName(path);

    // ── the serialised shape. Every field nullable, so absent takes the default. ──

    private sealed class CharmDocument
    {
        public int              FormatVersion { get; set; }
        public CharmDut?        Dut           { get; set; }
        public CharmEmbedding?  Embedding     { get; set; }
        public CharmBias?       Bias          { get; set; }
        public CharmSettings?   Settings      { get; set; }
        public double?          PavlDbm       { get; set; }
        /// <summary>Markers, per side and band, as "R+jX" — see <see cref="TerminationsToJson"/>.</summary>
        public Dictionary<string, string>? Terminations { get; set; }
        /// <summary>R-h45-12 — the resolved Harmonica.* role maps and the §7.2 fade parameters.
        /// Absent on every .charm written before the appearance block existed, and on every one
        /// nobody has recoloured.</summary>
        public CharmAppearanceBlock? Appearance { get; set; }
        /// <summary>R-h45-1 — the §7.1 panel placement. Absent means the default arrangement,
        /// locked.</summary>
        public CharmLayoutBlock? Layout { get; set; }
        /// <summary>R-h7-7 — the picked traces (§7.7). Absent on every .charm written before H7 and
        /// on every one nobody has picked a trace in.</summary>
        public List<CharmTraceBlock>? Traces { get; set; }
        /// <summary>R-h9r2-8 — VSWR-circle overlays, keyed "source:2"/"load:1" like
        /// <see cref="Terminations"/>. Absent on every .charm written before this brief and on every
        /// one nobody has turned the overlay on for.</summary>
        public Dictionary<string, string>? Vswr { get; set; }
    }

    private sealed class CharmTraceBlock
    {
        public string? Spec  { get; set; }
        public string? Panel { get; set; }
        public string? Label { get; set; }
    }

    private sealed class CharmDut
    {
        public string? Kind { get; set; }
        public string? TypeName { get; set; }
        public string? Provider { get; set; }
        public double? Multiplicity { get; set; }
        public int?    SddPortCount { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
        public string? ModelFile { get; set; }
        public string? IntrinsicGate { get; set; }
        public string? IntrinsicDrain { get; set; }
        public string? IntrinsicSource { get; set; }
    }

    private sealed class CharmEmbedding
    {
        public string? S2pIn { get; set; }
        public string? S2pOut { get; set; }
        public string? S4p { get; set; }
        public double? Rg { get; set; }
        public double? Lg { get; set; }
        public double? Rd { get; set; }
        public double? Ld { get; set; }
        public double? Rs { get; set; }
        public double? Ls { get; set; }
        public double? Cpg { get; set; }
        public double? Cpd { get; set; }
        public double? CgdExt { get; set; }
    }

    private sealed class CharmBias
    {
        public double? Vds { get; set; }
        public double? Vgs { get; set; }
        public double? Idq { get; set; }
    }

    private sealed class CharmSettings
    {
        public int?    HarmonicCount { get; set; }
        public double? FrequencyHz { get; set; }
        public int?    FftOverSample { get; set; }
        public double? Tol { get; set; }
        public int?    MaxIter { get; set; }
        public int?    GuardHarmonic { get; set; }
        public double? Lambda { get; set; }
        public double? CompressionDb { get; set; }
        public double? PinMaxDbm { get; set; }
        public double? PinStartDbm { get; set; }
        public bool?   ComputeCharge { get; set; }
        public double? BiasChokeH { get; set; }
        public double? DcBlockF { get; set; }

        /// <summary>R-h9b-6 — absent on every .charm written before this setting existed; such a
        /// file opens at the historical 50 Ω, no <c>FormatVersion</c> bump.</summary>
        public double? Z0 { get; set; }

        /// <summary>R-h9b-13 — absent on every .charm written before this setting existed; such a
        /// file opens at the default 64 samples.</summary>
        public int? LoadlineSamples { get; set; }

        /// <summary>R-h9b-12 — the DCIV Sweeps dialog's override, all-or-nothing (see
        /// <c>DcivFamily.OverrideOf</c>). Absent on every .charm written before the dialog existed.</summary>
        public double? DcivVgsMin { get; set; }
        public double? DcivVgsMax { get; set; }
        public int?    DcivVgsSteps { get; set; }
        public double? DcivVdsMin { get; set; }
        public double? DcivVdsMax { get; set; }
        public int?    DcivVdsSteps { get; set; }

        /// <summary>R-h9r2-18 — the explicit power sweep's own Step. Absent on every .charm written
        /// before this brief; such a file opens at the default 1 dB.</summary>
        public double? PinStepDbm { get; set; }

        /// <summary>R-h9r2-18a — absent on every .charm written before this brief; such a file opens
        /// at the default (tickle on, −50 dBm).</summary>
        public bool?   TickleEnabled { get; set; }
        public double? TickleDbm { get; set; }

        /// <summary>R-h9r2-17a — absent on every .charm written before this brief; such a file opens
        /// with the option off (interpolated compression, no extra solve).</summary>
        public bool?   ExactCompressionSolve { get; set; }

        /// <summary>brief-harmonicarf-r4 §1 — absent on every .charm written before this brief; such
        /// a file opens at the default margin (0 dB, stop exactly on the crossing rung).</summary>
        public double? SweepOverdriveDb { get; set; }
    }

    // ── markers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The marker set, keyed <c>"source:2"</c> / <c>"load:1"</c>. Only MARKED bands are written — an
    /// absent band is unmarked and takes <see cref="TerminationSet.UnmarkedBandOhms"/>, which is what
    /// makes "remove this marker" and "never had one" the same state on reload.
    /// </summary>
    public static Dictionary<string, string> TerminationsToJson(TerminationSet t)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int side = 0; side < 2; side++)
            foreach (int band in t.MarkedBands((TerminationSide)side).OrderBy(b => b))
            {
                Complex z = t.Z((TerminationSide)side, band);
                map[$"{(TerminationSide)side}:{band}".ToLowerInvariant()] =
                    $"{z.Real.ToString("R", CultureInfo.InvariantCulture)}," +
                    $"{z.Imaginary.ToString("R", CultureInfo.InvariantCulture)}";
            }
        return map;
    }

    /// <inheritdoc cref="TerminationsToJson"/>
    public static TerminationSet TerminationsFromJson(
        IReadOnlyDictionary<string, string>? map, int harmonicCount)
    {
        var t = new TerminationSet(harmonicCount);
        if (map is null) return t;

        foreach (var (key, value) in map)
        {
            string[] parts = key.Split(':');
            string[] nums  = value.Split(',');
            if (parts.Length != 2 || nums.Length != 2) continue;
            if (!Enum.TryParse<TerminationSide>(parts[0], ignoreCase: true, out var side)) continue;
            if (!int.TryParse(parts[1], out int band) || band < 1 || band > harmonicCount) continue;
            if (!double.TryParse(nums[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double re)) continue;
            if (!double.TryParse(nums[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double im)) continue;
            t.Set(side, band, new Complex(re, im));
        }
        return t;
    }

    /// <summary>Writes a document together with its marker set, and (R-h45-12) its appearance.</summary>
    /// <param name="appearance">
    /// Null, or <see cref="CharmAppearance.IsDefault"/>, writes NO appearance block at all — so a
    /// <c>.charm</c> nobody has recoloured re-serialises byte-for-byte, exactly as it did before this
    /// field existed.
    /// </param>
    public static string Write(CircuitModel model, TerminationSet terminations,
                               CharmAppearance? appearance = null, CharmLayout? layout = null,
                               IReadOnlyList<CharmTrace>? traces = null,
                               IReadOnlyList<CharmMarkerVswr>? vswr = null)
    {
        var doc = ToDocument(model);
        doc.Terminations = TerminationsToJson(terminations);
        doc.Appearance   = AppearanceToJson(appearance);
        doc.Layout       = LayoutToJson(layout);
        // No picked traces ⇒ NO block, so a .charm nobody has picked a trace in re-serialises
        // byte-for-byte, exactly as it did before this field existed (the same rule the appearance
        // and layout blocks already follow).
        doc.Traces       = traces is { Count: > 0 }
            ? [.. traces.Select(t => new CharmTraceBlock
                {
                    Spec = t.Spec, Panel = t.PanelId, Label = t.Label,
                })]
            : null;
        // R-h9r2-8 — same rule again: no marker has the overlay on ⇒ no block.
        doc.Vswr         = VswrToJson(vswr);
        return JsonSerializer.Serialize(doc, Options);
    }

    public static void WriteFile(string path, CircuitModel model, TerminationSet terminations,
                                 CharmAppearance? appearance = null, CharmLayout? layout = null,
                                 IReadOnlyList<CharmTrace>? traces = null,
                                 IReadOnlyList<CharmMarkerVswr>? vswr = null)
        => File.WriteAllText(path, Write(model, terminations, appearance, layout, traces, vswr));

    /// <summary>R-h9r2-8 — writes only the markers whose overlay is ON, keyed like
    /// <see cref="TerminationsToJson"/>. Null/empty input ⇒ null block, so an untouched document
    /// re-serialises byte-for-byte.</summary>
    public static Dictionary<string, string>? VswrToJson(IReadOnlyList<CharmMarkerVswr>? vswr)
    {
        if (vswr is not { Count: > 0 }) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in vswr)
            map[$"{e.Side}:{e.Band}".ToLowerInvariant()] =
                e.Value.ToString("R", CultureInfo.InvariantCulture);
        return map;
    }

    /// <inheritdoc cref="VswrToJson"/>
    public static IReadOnlyList<CharmMarkerVswr> VswrFromJson(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null) return [];
        var list = new List<CharmMarkerVswr>();
        foreach (var (key, value) in map)
        {
            string[] parts = key.Split(':');
            if (parts.Length != 2) continue;
            if (!Enum.TryParse<TerminationSide>(parts[0], ignoreCase: true, out var side)) continue;
            if (!int.TryParse(parts[1], out int band)) continue;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            list.Add(new CharmMarkerVswr(side, band, v));
        }
        return list;
    }

    /// <inheritdoc cref="Read(string, string?, out IReadOnlyList{UnresolvedReference})"/>
    public static (CircuitModel Model, TerminationSet Terminations) Read(
        string json, string? baseDirectory, out IReadOnlyList<UnresolvedReference> unresolved, bool withMarkers)
    {
        var all = ReadAll(json, baseDirectory);
        unresolved = all.Unresolved;
        _ = withMarkers;
        return (all.Model, all.Terminations);
    }

    /// <summary>
    /// The whole file from ONE parse — model, markers, appearance and unresolved references. Every
    /// other <c>Read</c> overload delegates here, so there is one place the document shape is
    /// interpreted and the narrower overloads cannot drift from it.
    /// </summary>
    public static CharmContents ReadAll(string json, string? baseDirectory)
    {
        var doc = JsonSerializer.Deserialize<CharmDocument>(json, Options)
                  ?? throw new InvalidDataException("the .charm file is empty");

        var model = FromDocument(doc);
        return new CharmContents(
            model,
            TerminationsFromJson(doc.Terminations, model.Settings.HarmonicCount),
            AppearanceFromJson(doc.Appearance),
            LayoutFromJson(doc.Layout),
            FindUnresolved(model, doc, baseDirectory),
            TracesFromJson(doc.Traces),
            VswrFromJson(doc.Vswr));
    }

    /// <summary>
    /// The picked traces. An entry with no spec is DROPPED (there is nothing to plot); one with no
    /// panel id gets a generated one, because a placement-less trace is still a trace the user asked
    /// for and Edit Display can move it once it exists.
    /// </summary>
    private static IReadOnlyList<CharmTrace> TracesFromJson(List<CharmTraceBlock>? blocks)
    {
        if (blocks is null) return [];

        var list = new List<CharmTrace>(blocks.Count);
        int n = 0;
        foreach (var b in blocks)
        {
            if (string.IsNullOrWhiteSpace(b.Spec)) continue;
            n++;
            list.Add(new CharmTrace(b.Spec!,
                                    string.IsNullOrWhiteSpace(b.Panel) ? $"trace.{n}" : b.Panel!,
                                    string.IsNullOrWhiteSpace(b.Label) ? null : b.Label));
        }
        return list;
    }

    // ── layout (R-h45-1) ──────────────────────────────────────────────────────

    private static CharmLayoutBlock? LayoutToJson(CharmLayout? l)
        => l is null || l.IsDefault
            ? null                          // untouched ⇒ no block, so the file does not churn
            : new CharmLayoutBlock
            {
                Locked = l.Locked,
                Panels = [.. l.Panels.Select(p => new CharmPanelBlock
                {
                    Id = p.PanelId, X = p.X, Y = p.Y, W = p.W, H = p.H,
                })],
            };

    private static CharmLayout LayoutFromJson(CharmLayoutBlock? b)
    {
        if (b is null) return CharmLayout.Default;

        // A placement with no id, or a degenerate size, is DROPPED rather than honoured — a panel
        // positioned at zero width is invisible with nothing on screen to say why, which is worse
        // than falling back to §7.1's own default for that one panel.
        var panels = b.Panels is null
            ? CharmLayout.DefaultPanels
            : (IReadOnlyList<CharmPanelPlacement>)
              [.. b.Panels.Where(p => !string.IsNullOrWhiteSpace(p.Id) && p.W > 0 && p.H > 0)
                          .Select(p => new CharmPanelPlacement(p.Id!, p.X, p.Y, p.W, p.H))];

        return new CharmLayout
        {
            Panels = panels.Count == 0 ? CharmLayout.DefaultPanels : panels,
            Locked = b.Locked ?? true,
        };
    }

    private sealed class CharmLayoutBlock
    {
        public bool?                  Locked { get; set; }
        public List<CharmPanelBlock>? Panels { get; set; }
    }

    private sealed class CharmPanelBlock
    {
        public string? Id { get; set; }
        public double  X  { get; set; }
        public double  Y  { get; set; }
        public double  W  { get; set; }
        public double  H  { get; set; }
    }

    /// <inheritdoc cref="ReadAll(string, string?)"/>
    public static CharmContents ReadAllFile(string path)
        => ReadAll(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)));

    // ── appearance (R-h45-12) ─────────────────────────────────────────────────

    private static CharmAppearanceBlock? AppearanceToJson(CharmAppearance? a)
    {
        if (a is null || a.IsDefault) return null;
        return new CharmAppearanceBlock
        {
            // Sorted for a stable, human-diffable file — the same courtesy ColorThemeIo extends.
            Light = Sorted(a.Light),
            Dark  = Sorted(a.Dark),
            IsoAlphaFloor          = a.IsoAlphaFloor,
            IsoAlphaExponent       = a.IsoAlphaExponent,
            ShowIsoLineLabels      = a.ShowIsoLineLabels,
            ShowGridPoints         = a.ShowGridPoints,
            ShowDiagnosticsOverlay = a.ShowDiagnosticsOverlay,
            ReadoutFormats         = Sorted(a.ReadoutFormats),
        };

        static Dictionary<string, string>? Sorted(IReadOnlyDictionary<string, string> src)
            => src.Count == 0
                ? null
                : src.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                     .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private static CharmAppearance AppearanceFromJson(CharmAppearanceBlock? b)
        => b is null
            ? CharmAppearance.Default
            : new CharmAppearance
            {
                Light = b.Light is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(b.Light, StringComparer.Ordinal),
                Dark = b.Dark is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(b.Dark, StringComparer.Ordinal),
                IsoAlphaFloor          = b.IsoAlphaFloor,
                IsoAlphaExponent       = b.IsoAlphaExponent,
                ShowIsoLineLabels      = b.ShowIsoLineLabels,
                ShowGridPoints         = b.ShowGridPoints,
                ShowDiagnosticsOverlay = b.ShowDiagnosticsOverlay,
                ReadoutFormats    = b.ReadoutFormats is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(b.ReadoutFormats, StringComparer.Ordinal),
            };

    private sealed class CharmAppearanceBlock
    {
        public Dictionary<string, string>? Light { get; set; }
        public Dictionary<string, string>? Dark  { get; set; }
        public double? IsoAlphaFloor          { get; set; }
        public double? IsoAlphaExponent       { get; set; }
        public bool?   ShowIsoLineLabels      { get; set; }
        public bool?   ShowGridPoints         { get; set; }
        public bool?   ShowDiagnosticsOverlay { get; set; }
        public Dictionary<string, string>? ReadoutFormats { get; set; }
    }
}
