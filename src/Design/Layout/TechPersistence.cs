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

    /// <summary>The <c>.cmat</c> libraries this technology names (brief-em3d-53 R-em3d53-1a, additive),
    /// relative to the <c>.ctech</c>'s directory; omitted when null.</summary>
    public List<string>? MaterialLibraries { get; set; }

    /// <summary>The process's named materials (brief-em3d-2 R-em3d2-1b, additive) — nullable for
    /// <see cref="Constants"/>' reason: absent in every .ctech written before the list existed, and
    /// omitted on write when empty so every one of those round-trips byte-identically.</summary>
    public List<TechMaterial>? Materials { get; set; }

    /// <summary>3D-only solids (brief-em3d-2 R-em3d2-3b, additive), omitted when empty — an older
    /// build ignores the key and the planar extractors never see one.</summary>
    public List<TechBody>? Bodies { get; set; }

    /// <summary>brief-em3d-73 R-em3d73-3a — thermal boundary resistances between material pairs (additive), omitted when
    /// null.</summary>
    public List<TechThermalInterface>? ThermalInterfaces { get; set; }
}

/// <summary>Reads and writes .ctech files. Framework-free (no Avalonia / Skia).</summary>
public static class TechPersistence
{
    public const int CurrentFormatVersion = 1;

    /// <summary>The one serializer contract — the <c>.cmat</c> reader uses it too, so a material record
    /// reads identically in either file (brief-em3d-53 §1a).</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Serialize(Technology tech)
        => JsonSerializer.Serialize(ToFileModel(tech), JsonOpts);

    /// <remarks>
    /// brief-em3d-53 R-em3d53-1c — <b>a technology loaded from circuitRF's own resources brings its shipped
    /// libraries with it.</b> Its references (<c>generic-materials.cmat</c>) are relative, and resolved against the
    /// resources while it was in the assembly; written to disk they resolve against the new file's directory, where
    /// nothing is — so the New Technology starters and any other "save a shipped technology here" would write a
    /// technology that refuses to load. Each shipped library is written beside it, and ONLY where no file of that name
    /// exists: a workspace's copy is its own file and is never overwritten.
    /// </remarks>
    public static void SaveToFile(string path, Technology tech)
    {
        AtomicFile.WriteAllText(path, Serialize(tech));
        foreach (string reference in tech.MaterialLibraries ?? [])
        {
            string shipped = MaterialLibraries.Shipped.Locate(reference);
            if (!tech.ResolvedLibraryPaths.Contains(shipped, StringComparer.Ordinal)) continue;
            string target = MaterialLibraries.ResolvePath(path, reference);
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, MaterialLibraries.ShippedRawJson(Path.GetFileName(target)));
        }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A technology from its JSON <b>with no library resolved</b> — a <c>.ctech</c> read with no path has
    /// nothing to resolve its <see cref="Technology.MaterialLibraries"/> against. A file is read with
    /// <see cref="LoadFromFile(string)"/>; a copy of a loaded technology is made with <see cref="Clone"/>,
    /// which keeps what its libraries contributed.
    /// </summary>
    public static Technology Deserialize(string json)
    {
        var tech = DeserializeUnresolved(json);
        ResolveMaterials(tech);
        return tech;
    }

    /// <summary>
    /// A technology from its JSON with its libraries resolved through <paramref name="libraries"/>
    /// (brief-em3d-53 §1c) and then its named materials applied. Throws
    /// <see cref="MaterialLibraryException"/> for a library that cannot be read and for a name two
    /// sources define differently (§1d).
    /// </summary>
    public static Technology Deserialize(string json, MaterialLibraryLoader libraries, string ctechLabel)
    {
        var tech = DeserializeUnresolved(json);
        MaterialLibraries.Resolve(tech, libraries, ctechLabel);
        ResolveMaterials(tech);
        return tech;
    }

    /// <summary>
    /// Re-resolves <paramref name="tech"/>'s libraries in place — for a library edited while a technology
    /// naming it is loaded (R-em3d53-1f) — then re-applies its named materials. Throws as
    /// <see cref="Deserialize(string, MaterialLibraryLoader, string)"/> does.
    /// </summary>
    public static void ResolveLibraries(Technology tech, MaterialLibraryLoader libraries, string ctechLabel)
    {
        MaterialLibraries.Resolve(tech, libraries, ctechLabel);
        ResolveMaterials(tech);
    }

    /// <summary>
    /// A deep copy that keeps what the libraries contributed — a round trip through the writer and
    /// <see cref="Deserialize(string)"/> would drop it, since the file stores only the references. The
    /// library records themselves are shared: nothing edits a library through a technology (M4).
    /// </summary>
    /// <remarks>
    /// <b>It copies <see cref="LayerDef.Visible"/>/<see cref="LayerDef.Selectable"/> across by hand.</b>
    /// They are view state and the writer leaves them out (brief-layout-layers-panel.md R-lyr-1b), so
    /// the round trip alone would reset every one to <c>true</c> — silently, in the layout editor's draw
    /// copy, <see cref="TechnologyLayerSelection"/> and every other caller. Layers are matched by
    /// position, which the round trip preserves.
    /// </remarks>
    public static Technology Clone(Technology tech)
    {
        var clone = DeserializeUnresolved(Serialize(tech));
        clone.UnplacedFilmLayers = [.. tech.UnplacedFilmLayers];
        for (int i = 0; i < tech.Layers.Count && i < clone.Layers.Count; i++)
        {
            clone.Layers[i].Visible    = tech.Layers[i].Visible;
            clone.Layers[i].Selectable = tech.Layers[i].Selectable;
        }
        clone.LibraryMaterials = tech.LibraryMaterials;
        clone.ResolvedLibraryPaths = tech.ResolvedLibraryPaths;
        clone.LibraryThermalInterfaces = tech.LibraryThermalInterfaces;
        ResolveMaterials(clone);
        return clone;
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

        var tech = FromFileModel(file);
        MoveLegacyFilms(json, tech);
        return tech;
    }

    /// <summary>
    /// A sheet resistance used to be stated on a drawing layer (<c>SheetResistanceOhmPerSq</c>, with
    /// <c>FilmThicknessDbu</c> and <c>FilmAt</c>), and such files exist. This reads them and moves each to
    /// its level; nothing writes that form back. A layer bound into a conductor leaves that conductor's
    /// <c>DrawingLayers</c> for its <c>Films</c>. A layer bound to nothing goes to the level the
    /// <c>TFR</c> part draws its contacts on. With no conductor at all the value cannot be kept, and
    /// <see cref="Technology.UnplacedFilmLayers"/> lets <c>TechValidation</c> say so.
    /// </summary>
    private static void MoveLegacyFilms(string json, Technology tech)
    {
        if (!json.Contains("SheetResistanceOhmPerSq", StringComparison.OrdinalIgnoreCase)) return;

        using var doc = JsonDocument.Parse(json);
        if (!TryGet(doc.RootElement, "Layers", out var layers) || layers.ValueKind != JsonValueKind.Array) return;

        int index = -1;
        foreach (var layer in layers.EnumerateArray())
        {
            index++;
            if (!TryGet(layer, "SheetResistanceOhmPerSq", out var rs) || rs.ValueKind != JsonValueKind.Number) continue;
            if (index >= tech.Layers.Count) break;
            var def = tech.Layers[index];

            var film = new StackupFilm { Layer = def.Key, SheetResistanceOhmPerSq = rs.GetDouble() };
            if (TryGet(layer, "FilmThicknessDbu", out var t) && t.ValueKind == JsonValueKind.Number) film.ThicknessDbu = t.GetInt64();
            if (TryGet(layer, "FilmAt", out var at))
                film.At = at.ValueKind == JsonValueKind.String && Enum.TryParse<ConductorSheetSurface>(at.GetString(), true, out var face) ? face
                        : at.ValueKind == JsonValueKind.Number ? (ConductorSheetSurface)at.GetInt32() : null;

            var bound = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(def.Key)).ToList();
            foreach (var entry in bound) entry.DrawingLayers.Remove(def.Key);

            var level = bound.FirstOrDefault() ?? PCells.MmicStackResolver.Resolve(tech).BaseMetal;
            if (level is null) tech.UnplacedFilmLayers.Add(def.Name);
            else (level.Films ??= []).Add(film);
        }

        static bool TryGet(JsonElement element, string name, out JsonElement value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
            return false;
        }
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
        if (tech.Materials.Count == 0 && tech.LibraryMaterials.Count == 0) return;
        foreach (var layer in tech.Stackup.Layers)
        {
            if (tech.FindMaterial(layer.Material) is not { } m) continue;
            if (layer.Kind == StackupKind.Dielectric)
            {
                if (m.Epsr is { } epsr) layer.Epsr = epsr;
                if (m.TanD is { } tand) layer.TanD = tand;
                if (m.Mur  is { } mur)  layer.Mur  = mur;
                // SI-SIG — a material with both is a conducting dielectric. One with σ and no εr stays a
                // conductor, which is what a body decides too, and puts nothing on this entry.
                if (m.Epsr is not null && m.Sigma20 is { } conducting) layer.SigmaSm = conducting;
            }
            else if (m.Sigma20 is { } sigma)
            {
                layer.SigmaSm = sigma;
            }
        }
    }

    /// <summary>A <c>.ctech</c> from disk, its libraries resolved against its own directory.</summary>
    public static Technology LoadFromFile(string path)
        => LoadFromFile(path, MaterialLibraries.Disk(path));

    /// <summary>As <see cref="LoadFromFile(string)"/>, with the caller's loader — the cache passes one
    /// that answers a library open with unsaved edits.</summary>
    public static Technology LoadFromFile(string path, MaterialLibraryLoader libraries)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path), libraries, Path.GetFullPath(path));

    /// <summary>
    /// A <c>.ctech</c>'s OWN content with no library resolved — what the technology editor opens, so a
    /// technology whose libraries refuse to load can still be opened and fixed (brief-em3d-53 §1d).
    /// Nothing that USES a technology may load it this way.
    /// </summary>
    public static Technology LoadOwnFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    // ── Convert Technology <-> CtechFile ──────────────────────────────────────

    private static CtechFile ToFileModel(Technology tech)
    {
        // An empty film list is omitted on write, like every additive list here: a level that lost its
        // last film reads back exactly as a level that never had one.
        foreach (var entry in tech.Stackup.Layers)
            if (entry.Films is { Count: 0 }) entry.Films = null;
        return ToFileModelCore(tech);
    }

    private static CtechFile ToFileModelCore(Technology tech) => new()
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
        MaterialLibraries    = tech.MaterialLibraries is { Count: > 0 } ml ? [.. ml] : null,
        Materials            = tech.Materials.Count > 0 ? [.. tech.Materials] : null,
        Bodies               = tech.Bodies.Count > 0 ? [.. tech.Bodies] : null,
        ThermalInterfaces    = tech.ThermalInterfaces is { } ti ? [.. ti] : null,
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
        MaterialLibraries    = file.MaterialLibraries is { Count: > 0 } fml ? [.. fml] : null,
        Materials            = file.Materials is { Count: > 0 } tm ? [.. tm] : [],
        Bodies               = file.Bodies is { Count: > 0 } tb ? [.. tb] : [],
        ThermalInterfaces    = file.ThermalInterfaces is { } fti ? [.. fti] : null,
    };
}
