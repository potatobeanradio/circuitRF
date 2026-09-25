using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout;

// ──────────────────────────────────────────────────────────────────────────────
//  .ctech file format — rev 1 (alpha, no back-compat per policy).
//  Clones LayoutPersistence/SymbolPersistence conventions exactly, including the gzip sniff on load.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class CtechFile
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public LayoutUnit DefaultDisplayUnit { get; set; }
    public long DefaultSnapDbu { get; set; }
    public long DefaultFlattenTolDbu { get; set; }
    public long DefaultLabelHeightDbu { get; set; }
    public long DefaultViaPadDbu { get; set; }
    public long DefaultViaDrillDbu { get; set; }
    public List<LayerDef> Layers { get; set; } = [];

    /// <summary>The stipple table layers name (rev 1, additive). Absent in every .ctech written
    /// before stipples existed, which reads back as an empty table and a solid fill everywhere —
    /// exactly what those files meant.</summary>
    public List<FillPattern>? FillPatterns { get; set; }
    public Stackup Stackup { get; set; } = new();
    public List<DrcRule> DrcRules { get; set; } = [];

    /// <summary>The LVS property tolerances this process overrides (rev 1, additive) — nullable
    /// for <see cref="FillPatterns"/>' reason: absent in every .ctech written before brief 10, and
    /// absent is what "use the shipped table" already meant, so nothing on disk changes.</summary>
    public List<LvsToleranceRule>? LvsTolerances { get; set; }

    /// <summary>The geometric device-recognition deck (rev 1, additive) — nullable for
    /// <see cref="LvsTolerances"/>' reason: absent in every .ctech written before brief 14, and
    /// absent is what "this process recognises nothing" already meant.</summary>
    public List<DeviceRule>? DeviceRules { get; set; }

    /// <summary>The process's own named constants, which a <see cref="DeviceRule"/>'s parameter
    /// formula may refer to (rev 1, additive).</summary>
    public List<TechConstant>? Constants { get; set; }

    /// <summary>The process's named materials (brief-em3d-2 R-em3d2-1b, additive) — nullable for
    /// <see cref="Constants"/>' reason: absent in every .ctech written before the list existed, and
    /// omitted on write when empty so every one of those round-trips byte-identically.</summary>
    public List<TechMaterial>? Materials { get; set; }

    /// <summary>3D-only solids (brief-em3d-2 R-em3d2-3b, additive), omitted when empty — an older
    /// build ignores the key and the planar extractors never see one.</summary>
    public List<TechBody>? Bodies { get; set; }
}

/// <summary>Reads and writes .ctech files. Framework-free (no Avalonia / Skia).</summary>
public static class TechPersistence
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Serialize(Technology tech)
        => JsonSerializer.Serialize(ToFileModel(tech), JsonOpts);

    public static void SaveToFile(string path, Technology tech)
        => AtomicFile.WriteAllText(path, Serialize(tech));

    // ── Read ──────────────────────────────────────────────────────────────────

    public static Technology Deserialize(string json)
    {
        var tech = DeserializeUnresolved(json);
        ResolveMaterials(tech);
        return tech;
    }

    /// <summary>
    /// The technology exactly as the file states it, <b>before</b> any stackup entry takes its named
    /// material's values (brief-em3d-2 R-em3d2-4a). For <see cref="TechValidation.AnalyzeRaw"/>
    /// alone: after <see cref="Deserialize"/> an entry's numbers always equal its material's, so the
    /// "a hand edit made them disagree" warning can only be computed from this. Nothing that uses a
    /// technology may load it this way.
    /// </summary>
    public static Technology DeserializeUnresolved(string json)
    {
        var file = JsonSerializer.Deserialize<CtechFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .ctech file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".ctech format_version {file.FormatVersion} is newer than " +
                $"expected {CurrentFormatVersion}. Update the application.");

        return FromFileModel(file);
    }

    /// <summary>
    /// brief-em3d-2 R-em3d2-2b — <b>the one place a named material reaches a stackup entry's
    /// numbers.</b> An entry naming a material has its own <c>Epsr</c>/<c>TanD</c>/<c>Mur</c>
    /// (dielectric) or <c>SigmaSm</c> (conductor or via, from <see cref="TechMaterial.Sigma20"/>, at
    /// 20 °C) overwritten with the material's, so every existing reader of those four numbers is
    /// correct with no change — and a save writes numbers equal to the material's, which is what an
    /// older build then reads.
    ///
    /// <para>A property the material leaves null leaves the entry's own number alone (R-em3d2-2c),
    /// and a name the technology does not define leaves all four alone (R-em3d2-2d) — both
    /// reported by <c>check</c>, neither an exception, because a typo must not make the whole
    /// technology unopenable. Public so an editor that changes a material or an entry's name can
    /// re-apply the same rule rather than writing a second copy of it.</para>
    /// </summary>
    public static void ResolveMaterials(Technology tech)
    {
        if (tech.Materials.Count == 0) return;
        foreach (var layer in tech.Stackup.Layers)
        {
            if (tech.FindMaterial(layer.Material) is not { } m) continue;
            if (layer.Kind == StackupKind.Dielectric)
            {
                if (m.Epsr is { } epsr) layer.Epsr = epsr;
                if (m.TanD is { } tand) layer.TanD = tand;
                if (m.Mur  is { } mur)  layer.Mur  = mur;
            }
            else if (m.Sigma20 is { } sigma)
            {
                layer.SigmaSm = sigma;
            }
        }
    }

    public static Technology LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    // ── Convert Technology <-> CtechFile ──────────────────────────────────────

    private static CtechFile ToFileModel(Technology tech) => new()
    {
        FormatVersion        = CurrentFormatVersion,
        Name                 = tech.Name,
        DefaultDisplayUnit   = tech.DefaultDisplayUnit,
        DefaultSnapDbu       = tech.DefaultSnapDbu,
        DefaultFlattenTolDbu = tech.DefaultFlattenTolDbu,
        DefaultLabelHeightDbu = tech.DefaultLabelHeightDbu,
        DefaultViaPadDbu     = tech.DefaultViaPadDbu,
        DefaultViaDrillDbu   = tech.DefaultViaDrillDbu,
        Layers               = [.. tech.Layers],
        FillPatterns         = tech.FillPatterns.Count > 0 ? [.. tech.FillPatterns] : null,
        Stackup              = tech.Stackup,
        DrcRules             = [.. tech.DrcRules],
        LvsTolerances        = tech.LvsTolerances.Count > 0 ? [.. tech.LvsTolerances] : null,
        DeviceRules          = tech.DeviceRules.Count > 0 ? [.. tech.DeviceRules] : null,
        Constants            = tech.Constants.Count > 0 ? [.. tech.Constants] : null,
        Materials            = tech.Materials.Count > 0 ? [.. tech.Materials] : null,
        Bodies               = tech.Bodies.Count > 0 ? [.. tech.Bodies] : null,
    };

    private static Technology FromFileModel(CtechFile file)
    {
        ClampDrawLanes(file.Stackup);
        return FromFileModelCore(file);
    }

    /// <summary>
    /// R-stk5-8. <c>StackupLayer.DrawLaneFraction</c> is a fraction of the drawn band column, and a
    /// hand-edited file carrying 7 (or -0.5) would put a barrel off the page — where it cannot be
    /// seen, cannot be hovered and cannot be dragged back. Clamped on READ rather than refused: the
    /// field is a drawing position, and a picture is never worth failing a load over.
    ///
    /// <para>Clamped here rather than in the property setter so the model stays a plain data class,
    /// and so what is clamped is exactly what came off disk — a value the application itself wrote is
    /// already in range.</para>
    /// </summary>
    private static void ClampDrawLanes(Stackup stackup)
    {
        foreach (var layer in stackup.Layers)
        {
            if (layer.DrawLaneFraction is not { } f) continue;
            layer.DrawLaneFraction = double.IsNaN(f) ? null : Math.Clamp(f, 0d, 1d);
        }
    }

    private static Technology FromFileModelCore(CtechFile file) => new()
    {
        Name                 = file.Name,
        DefaultDisplayUnit   = file.DefaultDisplayUnit,
        DefaultSnapDbu       = file.DefaultSnapDbu,
        DefaultFlattenTolDbu = file.DefaultFlattenTolDbu,
        DefaultLabelHeightDbu = file.DefaultLabelHeightDbu,
        DefaultViaPadDbu     = file.DefaultViaPadDbu,
        DefaultViaDrillDbu   = file.DefaultViaDrillDbu,
        Layers               = [.. file.Layers],
        FillPatterns         = file.FillPatterns is { Count: > 0 } fp ? [.. fp] : [],
        Stackup              = file.Stackup,
        DrcRules             = [.. file.DrcRules],
        LvsTolerances        = file.LvsTolerances is { Count: > 0 } lt ? [.. lt] : [],
        DeviceRules          = file.DeviceRules is { Count: > 0 } dr ? [.. dr] : [],
        Constants            = file.Constants is { Count: > 0 } tc ? [.. tc] : [],
        Materials            = file.Materials is { Count: > 0 } tm ? [.. tm] : [],
        Bodies               = file.Bodies is { Count: > 0 } tb ? [.. tb] : [],
    };
}
