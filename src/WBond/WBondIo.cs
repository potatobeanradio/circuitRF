using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.WBond;

/// <summary>
/// Reads and writes the <c>.wBond</c> file (wbond.md §9, R-wb-11).
///
/// <para>Versioned JSON following the <c>DataDisplayConfig</c> / <c>.charm</c> pattern: a
/// <c>FormatVersion</c>, and an absent field takes its built-in default rather than failing. That is
/// what lets a field be added without a version bump.</para>
///
/// <para><b>Setup only — results are never stored (D9).</b> A cold fill at 600 wires is ~0.15 s
/// parallel, so re-deriving on open costs nothing and eliminates the entire stale-data class of
/// bug.</para>
///
/// <para><b>Embedded layout geometry is an opaque passthrough here (§0.3 item 1).</b> The
/// <c>.clay</c> model lives on the far side of the UI firewall, so this project stores the raw JSON
/// and re-emits it verbatim via <see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/>. Nothing in
/// WB-A parses it, and a load/save cycle must not alter a byte.</para>
/// </summary>
public static class WBondIo
{
    /// <summary>Bumped only when a change cannot be expressed as "absent field takes its default".</summary>
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return JsonSerializer.Serialize(ToDocument(design), Options);
    }

    public static void WriteFile(string path, WBondDesign design) =>
        File.WriteAllText(path, Write(design));

    public static WBondDesign Read(string json)
    {
        var doc = JsonSerializer.Deserialize<WBondDocument>(json, Options)
                  ?? throw new InvalidDataException("The .wBond file is empty or not an object.");

        if (doc.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $"This .wBond file is format version {doc.FormatVersion}; this build reads up to " +
                $"{CurrentFormatVersion}. Newer files are refused rather than partly read, because a " +
                "silently dropped field is worse than a clear refusal.");

        return FromDocument(doc);
    }

    public static WBondDesign ReadFile(string path) => Read(File.ReadAllText(path));

    // ---------------------------------------------------------------- mapping

    private static WBondDocument ToDocument(WBondDesign design) => new()
    {
        FormatVersion = CurrentFormatVersion,
        AssemblyRef = design.AssemblyRef,
        GroundPlaneEnabled = design.GroundPlane.Enabled,
        OperatingTempC = design.OperatingTempC,
        Materials = [.. design.Materials.Select(m => new MaterialDto
        {
            Name = m.Name,
            Sigma20 = m.Sigma20,
            Alpha20 = m.Alpha20,
            DensityKgM3 = m.DensityKgM3,
        })],
        Profiles = [.. design.Profiles.Select(p => new ProfileDto
        {
            Name = p.Name,
            LoopHeightNm = p.LoopHeightNm,
            Shape = [.. p.Shape.Select(s => new[] { s.Span, s.Height })],
        })],
        Arrays = [.. design.Arrays.Select(a => new ArrayDto
        {
            Name = a.Name,
            Profile = a.Profile,
            Wires = [.. a.Wires.Select(w => new WireDto
            {
                DiameterNm = w.DiameterNm,
                Material = w.Material,
                ProfileBinding = w.ProfileBinding,
                Locked = w.Locked ? true : null,
                Points = [.. w.Points.Select(p => new[] { p.X, p.Y, p.Z })],
            })],
        })],
        EmbeddedGeometry = design.EmbeddedGeometryJson,
        ViewState = design.ViewStateJson,
    };

    private static WBondDesign FromDocument(WBondDocument doc)
    {
        var design = new WBondDesign
        {
            OperatingTempC = doc.OperatingTempC ?? WireMaterials.DefaultOperatingTempC,
            AssemblyRef = doc.AssemblyRef,
            EmbeddedGeometryJson = doc.EmbeddedGeometry,
            ViewStateJson = doc.ViewState,
        };

        design.GroundPlane.Enabled = doc.GroundPlaneEnabled ?? true;

        if (doc.Materials is { Count: > 0 })
        {
            design.Materials.Clear();
            foreach (var m in doc.Materials)
                design.Materials.Add(new WireMaterial(m.Name, m.Sigma20, m.Alpha20, m.DensityKgM3));
        }

        foreach (var p in doc.Profiles ?? [])
        {
            design.Profiles.Add(new LoopProfile
            {
                Name = p.Name,
                LoopHeightNm = p.LoopHeightNm,
                Shape = [.. p.Shape.Select(s => new ProfilePoint(s[0], s[1]))],
            });
        }

        foreach (var a in doc.Arrays ?? [])
        {
            var array = new WireArray { Name = a.Name, Profile = a.Profile };
            foreach (var w in a.Wires ?? [])
            {
                var wire = new Wire
                {
                    DiameterNm = w.DiameterNm,
                    Material = w.Material ?? WireMaterials.Default.Name,
                    ProfileBinding = w.ProfileBinding,
                    Locked = w.Locked ?? false,
                };
                foreach (var p in w.Points ?? [])
                    wire.Points.Add(new Point3(p[0], p[1], p[2]));
                array.Wires.Add(wire);
            }
            design.Arrays.Add(array);
        }

        return design;
    }

    // ---------------------------------------------------------------- DTOs

    private sealed class WBondDocument
    {
        public int FormatVersion { get; set; }

        /// <summary>See <see cref="WBondDesign.AssemblyRef"/>. Additive and nullable — no version bump.</summary>
        public string? AssemblyRef { get; set; }

        public bool? GroundPlaneEnabled { get; set; }
        public double? OperatingTempC { get; set; }
        public List<MaterialDto>? Materials { get; set; }
        public List<ProfileDto>? Profiles { get; set; }
        public List<ArrayDto>? Arrays { get; set; }

        [JsonConverter(typeof(RawJsonConverter))]
        public string? EmbeddedGeometry { get; set; }

        [JsonConverter(typeof(RawJsonConverter))]
        public string? ViewState { get; set; }
    }

    private sealed class MaterialDto
    {
        public string Name { get; set; } = "";
        public double Sigma20 { get; set; }
        public double Alpha20 { get; set; }
        public double DensityKgM3 { get; set; }
    }

    private sealed class ProfileDto
    {
        public string Name { get; set; } = "";
        public long LoopHeightNm { get; set; }

        /// <summary>Span/height pairs, kept as arrays so the file stays compact for a long profile.</summary>
        public List<double[]> Shape { get; set; } = [];
    }

    private sealed class ArrayDto
    {
        public string Name { get; set; } = "";
        public string? Profile { get; set; }
        public List<WireDto>? Wires { get; set; }
    }

    private sealed class WireDto
    {
        public long DiameterNm { get; set; }
        public string? Material { get; set; }
        public string? ProfileBinding { get; set; }
        public bool? Locked { get; set; }

        /// <summary>x/y/z triples in DBU. Integer, so a round trip is exact by construction.</summary>
        public List<long[]>? Points { get; set; }
    }

    /// <summary>
    /// Carries a JSON subtree as raw text, unparsed and unmodified.
    ///
    /// <para>Reading captures <see cref="JsonElement.GetRawText"/>; writing emits it with
    /// <c>WriteRawValue</c>. The subtree therefore nests naturally in the file — it is a real object,
    /// not an escaped string — while this project never has to know what is in it.</para>
    /// </summary>
    private sealed class RawJsonConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.GetRawText();
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteRawValue(value);
        }
    }
}
