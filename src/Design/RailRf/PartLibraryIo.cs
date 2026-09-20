// .crlib file format — rev 1.
// Mirrors RailDocumentIo exactly, which mirrors EmSetupPersistence, which mirrors TechPersistence:
// System.Text.Json, WriteIndented, enum-as-string, WhenWritingNull, reject-on-newer-format-version,
// AtomicFile write, gzip sniff on load. A fifth spelling of the same thing would be a fifth thing to
// keep in step.
//
// NUMBERS ARE STORED IN BASE SI and carry their display scale nowhere near them: farads, hertz,
// henries, volts, ohms. src/Engine/RESOLVED.md records why — a mark read without its scale once
// produced a run at 2 Hz that looked entirely normal.
//
// WHAT IS NOT STORED: the DERIVED inductance. L = 1/((2·π·f0)^2·C) follows exactly from two fields
// that are already here, and a stored third copy is a field that will be edited on one side only
// (R-rail2-8). `stated_inductance_henries` exists only so a table that carries L as well can be
// COMPARED against the derivation and reported — it is never the value used.

using System.Text.Json;
using System.Text.Json.Serialization;

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;

namespace CircuitRF.Design.RailRf;

/// <summary>Reads and writes <c>.crlib</c>. Framework-free (no Avalonia / Skia), so the <c>rail</c>
/// verb works in CI.</summary>
public static class PartLibraryIo
{
    public const string Extension = ".crlib";
    public const int    CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The library as it goes on disk.
    ///
    /// <para><b>Validated on the way OUT as well as in</b>: a library that cannot be read back is a
    /// library that was never written, and the alternative is a file on the user's disk whose only
    /// symptom is that it refuses to open next week.</para>
    /// </summary>
    /// <param name="validate">
    /// Leave it true — that is the rule this header states, and every caller that is writing a
    /// FINISHED library takes it.
    ///
    /// <para><b>False is the EDITOR's case and only the editor's</b>
    /// (docs/sonnet-briefs/brief-railrf-24-part-library-editor.md R-rail24-3a): a library being
    /// typed passes through states it would be refused in — the commonest is a row added a second
    /// before its part number — and an editor that will not let you save work in progress is an
    /// editor people work around, by editing the JSON in something else. So the refusal TRAVELS
    /// WITH THE FILE instead of stopping the write: the editor states it on the row and in its
    /// status strip, a validated read (the one every run takes) still refuses it by name, and
    /// nothing anywhere silently repairs it. <b>The BYTES are identical either way</b> — this flag
    /// gates the check, never the shape, so there is no second spelling of the format.</para>
    /// </param>
    /// <exception cref="InvalidDataException">The library is not well formed — the sentence names the
    /// part.</exception>
    public static string Serialize(PartLibrary library, bool validate = true)
    {
        if (validate && library.Refusal() is { } r) throw new InvalidDataException(r);
        return JsonSerializer.Serialize(ToFileModel(library), JsonOpts);
    }

    /// <inheritdoc cref="Serialize(PartLibrary, bool)"/>
    public static void SaveToFile(string path, PartLibrary library, bool validate = true)
        => AtomicFile.WriteAllText(path, Serialize(library, validate));

    /// <param name="validate">See <see cref="Serialize(PartLibrary, bool)"/>. False is the editor's
    /// case: it has to be able to OPEN the work-in-progress file it was allowed to write, and then
    /// SAY what is wrong with it. A malformed JSON document or a newer format version is refused
    /// either way — those are not conditions a row can be edited out of.</param>
    /// <exception cref="InvalidDataException">The file is empty, is from a newer circuitRF, or is not
    /// well formed.</exception>
    public static PartLibrary Deserialize(string json, string baseDirectory = "", bool validate = true)
    {
        var file = JsonSerializer.Deserialize<CrlibFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .crlib file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".crlib format_version {file.FormatVersion} is newer than expected " +
                $"{CurrentFormatVersion}. Update the application.");

        var library = FromFileModel(file, baseDirectory);
        if (validate && library.Refusal() is { } r) throw new InvalidDataException(r);
        return library;
    }

    /// <inheritdoc cref="Deserialize(string, string, bool)"/>
    public static PartLibrary LoadFromFile(string path, bool validate = true)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path),
                       System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "",
                       validate);

    // ── convert ───────────────────────────────────────────────────────────────

    private static CrlibFile ToFileModel(PartLibrary l) => new()
    {
        FormatVersion = CurrentFormatVersion,
        Name          = NullIfEmpty(l.Name),
        Parts         = l.Rows.Count > 0 ? [.. l.Rows.Select(ToFile)] : null,
    };

    private static CrlibPart ToFile(PartLibraryRow r) => new()
    {
        PartNumber              = r.PartNumber,
        Description             = NullIfEmpty(r.Description),
        Footprint               = NullIfEmpty(r.Footprint),
        DielectricClass         = NullIfEmpty(r.DielectricClass),
        VoltageRatingV          = r.VoltageRatingV,
        CapacitanceFarads       = r.CapacitanceFarads,
        SelfResonantFrequencyHz = r.SelfResonantFrequencyHz,
        StatedInductanceHenries = r.StatedInductanceHenries,
        EsrOhms                 = r.EsrOhms,
        ModelRef                = NullIfEmpty(r.ModelRef),
        BiasCurve               = r.BiasCurve.Count > 0
                                    ? [.. r.BiasCurve.Select(p => new CrlibBiasPoint
                                      {
                                          BiasVolts         = p.BiasVolts,
                                          CapacitanceFarads = p.CapacitanceFarads,
                                      })]
                                    : null,
    };

    private static PartLibrary FromFileModel(CrlibFile f, string baseDirectory)
    {
        var library = new PartLibrary { Name = f.Name ?? "", BaseDirectory = baseDirectory };
        foreach (var part in f.Parts ?? [])
        {
            var row = new PartLibraryRow
            {
                PartNumber              = part.PartNumber ?? "",
                Description             = part.Description,
                Footprint               = part.Footprint,
                DielectricClass         = part.DielectricClass,
                VoltageRatingV          = part.VoltageRatingV,
                CapacitanceFarads       = part.CapacitanceFarads,
                SelfResonantFrequencyHz = part.SelfResonantFrequencyHz,
                StatedInductanceHenries = part.StatedInductanceHenries,
                EsrOhms                 = part.EsrOhms,
                ModelRef                = part.ModelRef,
            };
            foreach (var point in part.BiasCurve ?? [])
                row.BiasCurve.Add(new PartBiasPoint(point.BiasVolts, point.CapacitanceFarads));

            // The curve is read in whatever order the file states it and SORTED by bias here, because
            // an interpolation over an unsorted curve is a plausible wrong number rather than an
            // error (brief 11 interpolates it).
            row.BiasCurve.Sort((a, b) => a.BiasVolts.CompareTo(b.BiasVolts));
            library.Rows.Add(row);
        }
        return library;
    }

    private static string? NullIfEmpty(string? s) => s is { Length: > 0 } ? s : null;

    // ── the serialised shape. Every field nullable, so absent takes the default, and an unknown
    //    key read by a newer circuitRF is ignored rather than losing the keys beside it. ──────

    private sealed class CrlibFile
    {
        public int              FormatVersion { get; set; }
        public string?          Name          { get; set; }
        public List<CrlibPart>? Parts         { get; set; }
    }

    private sealed class CrlibPart
    {
        public string?               PartNumber              { get; set; }
        public string?               Description             { get; set; }
        public string?               Footprint               { get; set; }
        public string?               DielectricClass         { get; set; }
        public double?               VoltageRatingV          { get; set; }
        public double?               CapacitanceFarads       { get; set; }
        public double?               SelfResonantFrequencyHz { get; set; }
        public double?               StatedInductanceHenries { get; set; }
        public double?               EsrOhms                 { get; set; }
        public string?               ModelRef                { get; set; }
        public List<CrlibBiasPoint>? BiasCurve               { get; set; }
    }

    private sealed class CrlibBiasPoint
    {
        public double BiasVolts         { get; set; }
        public double CapacitanceFarads { get; set; }
    }
}
