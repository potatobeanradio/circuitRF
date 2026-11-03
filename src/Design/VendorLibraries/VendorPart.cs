using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>
/// One vendor library part, as the palette arms it, a drag carries it and Recently Used remembers it
/// (brief vendorlib-2 R-vl2-2b/2d).
///
/// <para><b>Identified by library + part, never by <see cref="SymbolKind"/></b>: every vendor part is
/// placed as <see cref="SymbolKind.SpiceModel"/>, so comparing kinds would arm every row at once. The
/// other fields are what placing it needs without a look-up — the file it lives in, the glyph, and the
/// case pair the footprint comes from — so a part remembered from an earlier session places correctly
/// while its library is still being indexed.</para>
/// </summary>
/// <param name="Library">The library's name in the workspace's <c>.cws</c>.</param>
/// <param name="Part">The subcircuit's name.</param>
/// <param name="File">The file, relative to the library root, with <c>/</c> separators.</param>
/// <param name="Glyph"><c>C</c>, <c>L</c> or empty — see <see cref="VendorPartPlacement.GlyphOf"/>.</param>
/// <param name="IsModel">A model folder (brief vendorlib-9 R-vl9-2d): <see cref="File"/> is then the model folder and the
/// part places as a cell instance of its <c>vendormodel://</c> reference, not as a SpiceModel.</param>
public sealed record VendorPartRef(
    string Library,
    string Part,
    string File,
    string Glyph,
    string? CaseMetric = null,
    string? CaseImperial = null,
    bool IsModel = false)
{
    /// <summary>The part for one index entry of the library named <paramref name="library"/>.</summary>
    public static VendorPartRef For(string library, VendorLibraryEntry e)
        => new(library, e.Part, e.File, VendorPartPlacement.GlyphOf(e), e.Header.CaseMetric, e.Header.CaseImperial,
               e.IsModel);

    /// <summary>A placed model's cell reference, <c>vendormodel://&lt;library&gt;/&lt;folder&gt;</c>; null for a SPICE part.</summary>
    public string? ModelRef => IsModel ? ModelLibrary.VendorModelRefs.RefFor(Library, File) : null;

    /// <summary>True when <paramref name="other"/> is the same part of the same library. Case-insensitive,
    /// as the index and the <c>.cws</c> look names up.</summary>
    public bool IsSamePart(VendorPartRef? other)
        => other is not null
        && string.Equals(Library, other.Library, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Part, other.Part, StringComparison.OrdinalIgnoreCase);

    /// <summary>A key for <see cref="IsSamePart"/>, for a dictionary or a de-dupe.</summary>
    public string IdentityKey => Library.ToUpperInvariant() + "/" + Part.ToUpperInvariant();

    /// <summary>The stored <c>File</c> parameter: <c>vendorlib://&lt;library&gt;/&lt;file&gt;</c>.</summary>
    public string FileRef => VendorLibraryRefs.RefFor(Library, File);

    private const string Marker = "vendorpart";

    /// <summary>
    /// One line of text, for a drag payload and the persisted Recently Used list. Tab-separated: no
    /// field can hold a tab (a library name, a SPICE name, a case code), while a path can hold almost
    /// anything else.
    /// </summary>
    /// <remarks>A model carries an eighth field, its kind word (<see cref="VendorModelInfo.KindWord"/>); a SPICE
    /// part's line is unchanged, so a Recently Used list written before models existed still reads.</remarks>
    public string Serialize()
        => string.Join('\t', IsModel
            ? [Marker, Library, Part, File, Glyph, CaseMetric ?? "", CaseImperial ?? "", VendorModelInfo.KindWord]
            : new[] { Marker, Library, Part, File, Glyph, CaseMetric ?? "", CaseImperial ?? "" });

    public static bool TryParse(string? text, out VendorPartRef result)
    {
        result = null!;
        if (text is null) return false;
        var f = text.Split('\t');
        if (f.Length is not (7 or 8) || f[0] != Marker || f[1].Length == 0 || f[2].Length == 0 || f[3].Length == 0) return false;
        bool model = f.Length == 8 && f[7] == VendorModelInfo.KindWord;
        if (f.Length == 8 && !model) return false;
        result = new VendorPartRef(f[1], f[2], f[3], f[4], f[5].Length == 0 ? null : f[5], f[6].Length == 0 ? null : f[6], model);
        return true;
    }
}

/// <summary>
/// What placing a vendor part writes (brief vendorlib-2 R-vl2-3) — one function each, called by the
/// palette's placement and by <c>circuitrf find --parts</c>, so an agent writing a <c>.csch</c> by
/// hand writes exactly what a drop would have.
/// </summary>
public static class VendorPartPlacement
{
    /// <summary>
    /// The <c>Glyph</c> parameter for a part: <c>C</c> or <c>L</c> when its header states a
    /// capacitance or an inductance AND it has two ports, otherwise blank.
    ///
    /// <para><b>Two ports is part of the rule</b>, so a stored <c>C</c> or <c>L</c> always means a
    /// two-terminal part. That is what lets the symbol be drawn from the instance alone when the
    /// library is missing — there is then no index to ask for the port count.</para>
    /// </summary>
    /// <remarks>A model's glyph is the one its <c>part.cpart</c> names, whatever it is (brief vendorlib-9 R-vl9-2b).</remarks>
    public static string GlyphOf(VendorLibraryEntry e)
        => e.Model is { } m ? m.Glyph
        : e.PortCount != 2 ? "" : e.Header.Quantity switch
        {
            VendorPartQuantity.Capacitance => "C",
            VendorPartQuantity.Inductance  => "L",
            _                              => "",
        };

    /// <summary>The built-in kind a glyph draws as, or null for a blank or unknown glyph.</summary>
    public static SymbolKind? GlyphKind(string? glyph) => (glyph ?? "").Trim().ToUpperInvariant() switch
    {
        "C" => SymbolKind.Capacitor,
        "L" => SymbolKind.Inductor,
        _   => null,
    };

    /// <summary>
    /// What a part with this glyph draws as before it is placed (the ghost): the built-in C or L, or
    /// the unconfigured SPICE box for a part with none. The placed instance resolves the same answer
    /// through <see cref="SpiceModelSymbolProvider"/>.
    /// </summary>
    public static Symbol.Symbol GhostSymbol(string? glyph)
        => GlyphGhost(glyph);

    /// <summary>The ghost of <paramref name="part"/>: a model's glyph bound to its pins, or its own symbol; a SPICE
    /// part's C, L or box.</summary>
    public static Symbol.Symbol GhostSymbol(VendorPartRef part, string? workspaceRoot)
    {
        if (!part.IsModel) return GhostSymbol(part.Glyph);
        var r = ModelLibrary.VendorModelSymbolProvider.Resolve(part.ModelRef!, null, workspaceRoot);
        return r.Symbol ?? ModelLibrary.VendorModelSymbolProvider.GlyphSymbol(part.Glyph, null);
    }

    private static Symbol.Symbol GlyphGhost(string? glyph)
        => GlyphKind(glyph) is { } kind
            ? BuiltInSymbols.Primitives(kind, 2)
            : SpiceModelSymbolProvider.UnconfiguredSymbol(Symbol.SnpPinConfig.Standard, Symbol.SnpPitch.Loose);

    /// <summary>The instance-name prefix: <c>C</c> / <c>L</c> for those glyphs, else null for the
    /// SpiceModel's own (R-vl2-3c).</summary>
    public static string? InstancePrefix(string? glyph) => GlyphKind(glyph) switch
    {
        SymbolKind.Capacitor => "C",
        SymbolKind.Inductor  => "L",
        _                    => null,
    };

    /// <summary>The instance parameters a placed part carries, in the order they are written.</summary>
    /// <remarks>A model's are its <c>Glyph</c> alone: its reference is the instance's <c>CellRef</c>
    /// (<see cref="VendorPartRef.ModelRef"/>), and its cell parameters are seeded from <c>part.cpart</c>.</remarks>
    public static IReadOnlyList<KeyValuePair<string, string>> Parameters(VendorPartRef part)
        => part.IsModel
        ? [new(ModelLibrary.VendorModelRefs.GlyphParameter, part.Glyph)]
        :
        [
            new(SpiceModelSymbolProvider.FileParameter,  part.FileRef),
            new(SpiceModelSymbolProvider.NameParameter,  part.Part),
            new(SpiceModelSymbolProvider.GlyphParameter, part.Glyph),
        ];

    /// <summary>The instance-name prefix a part is placed under: a SPICE part's <see cref="InstancePrefix(string?)"/>; a
    /// model's from its glyph — <c>R</c>/<c>C</c>/<c>L</c>, <c>Q</c> for a FET — else null for a cell's own.</summary>
    public static string? InstancePrefix(VendorPartRef part)
        => !part.IsModel ? InstancePrefix(part.Glyph)
         : part.Glyph.Trim().ToUpperInvariant() switch
           {
               "R" => "R", "C" => "C", "L" => "L", "FET" => "Q",
               _   => null,
           };

    /// <summary>The <c>Footprint</c> a placed part takes on <paramref name="technology"/>, or null for
    /// None — <see cref="FootprintDefaults.ForCaseCodes"/>.</summary>
    public static string? Footprint(VendorPartRef part, Technology? technology)
        => FootprintDefaults.ForCaseCodes(part.CaseMetric, part.CaseImperial, technology);
}

/// <summary>
/// Choosing a different part for an instance already placed (brief vendorlib-3 R-vl3-1): the
/// parameters the change writes, the footprint rule, and Value ▲/▼'s idea of "the same kind of part".
/// One definition, called by the Parameter Editor's part picker and by an in-place edit of the
/// instance's label.
/// </summary>
public static class VendorPartChange
{
    /// <summary>
    /// The instance's <c>Footprint</c> after the part changes from <paramref name="old"/> to
    /// <paramref name="new"/> (R-vl3-1d). The footprint follows the part only when it CAME from the
    /// part — when <paramref name="current"/> equals what <see cref="FootprintDefaults.ForCaseCodes"/>
    /// gives the old part — and is otherwise the user's choice and comes back unchanged. No state is
    /// stored to remember which: the comparison is the record. Null and blank both mean None.
    /// </summary>
    public static string? FootprintAfter(string? current, VendorPartRef old, VendorPartRef @new, Technology? technology)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(@new);
        string? cur = string.IsNullOrWhiteSpace(current) ? null : current.Trim();
        string? fromOld = VendorPartPlacement.Footprint(old, technology);
        return string.Equals(cur, fromOld, StringComparison.Ordinal)
            ? VendorPartPlacement.Footprint(@new, technology)
            : cur;
    }

    /// <summary>
    /// The parts of <paramref name="fileEntries"/> (one file's index entries) that are the same kind
    /// as <paramref name="current"/>, ordered by value and then by part name (R-vl3-1b). The same kind
    /// is the same file and the same Description fields except the value; for a part with no
    /// Description it is the same file only.
    /// </summary>
    public static IReadOnlyList<VendorLibraryEntry> SameKind(
        IReadOnlyList<VendorLibraryEntry> fileEntries, VendorLibraryEntry current)
    {
        ArgumentNullException.ThrowIfNull(fileEntries);
        ArgumentNullException.ThrowIfNull(current);
        if (current.IsModel)
        {
            // A model steps within its CATEGORY folder, among passive models of its quantity that state a Value, with
            // the same pins (brief vendorlib-9 R-vl9-2c) — the folder standing where a SPICE part's file stands.
            if (current.Header.Value is null || current.Model!.Kind != ModelLibrary.ModelPartManifest.KindPassive) return [current];
            string category = VendorModelInfo.CategoryOf(current);
            return [.. fileEntries
                .Where(e => e.IsModel && e.Header.Value is not null && e.Header.Quantity == current.Header.Quantity
                         && e.Model!.Kind == ModelLibrary.ModelPartManifest.KindPassive
                         && string.Equals(VendorModelInfo.CategoryOf(e), category, StringComparison.OrdinalIgnoreCase)
                         && e.Ports.SequenceEqual(current.Ports, StringComparer.Ordinal))
                .OrderBy(e => e.Header.Value)
                .ThenBy(e => e.Part, StringComparer.OrdinalIgnoreCase)];
        }

        string? key = KindKey(current);
        return [.. fileEntries
            .Where(e => string.Equals(e.File, current.File, StringComparison.OrdinalIgnoreCase)
                     && (key is null || string.Equals(KindKey(e), key, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Header.Value ?? double.PositiveInfinity)
            .ThenBy(e => e.Part, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The next (<paramref name="direction"/> &gt; 0) or previous part of the same kind, or
    /// null at either end and for a part not in <paramref name="fileEntries"/>.</summary>
    public static VendorLibraryEntry? Step(
        IReadOnlyList<VendorLibraryEntry> fileEntries, VendorLibraryEntry current, int direction)
    {
        var kind = SameKind(fileEntries, current);
        int at = -1;
        for (int i = 0; i < kind.Count; i++)
            if (kind[i].Part.Equals(current.Part, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
        if (at < 0 || direction == 0) return null;
        int next = at + Math.Sign(direction);
        return (uint)next < (uint)kind.Count ? kind[next] : null;
    }

    /// <summary>
    /// The Description fields that say what KIND of part this is — every field but the value — or
    /// null for a part with no Description. The value field is the one that reads as a value equal to
    /// the part's stated <c>Property</c> value; a header that states none keeps every field.
    /// </summary>
    internal static string? KindKey(VendorLibraryEntry e)
    {
        var fields = e.Header.DescriptionFields;
        if (e.Header.Description is null || fields.Count == 0) return null;
        int value = ValueFieldIndex(e);
        return string.Join("/", fields.Where((_, i) => i != value));
    }

    private static int ValueFieldIndex(VendorLibraryEntry e)
    {
        if (e.Header.Value is not { } have || have == 0) return -1;
        var fields = e.Header.DescriptionFields;
        for (int i = 0; i < fields.Count; i++)
        {
            // "1.5[pF]", "1.5pF" and "1.00e-05[uF]" are all spellings a header uses: the header's own
            // value reader takes every one of them, and the search grammar's is a fallback.
            string token = fields[i].Replace(" ", "");
            bool read = VendorLibraryHeader.TryParseValue(token, out double v)
                     || VendorLibrarySearch.TryReadValue(token.Replace("[", "").Replace("]", ""), out v);
            if (read && Math.Abs(v - have) <= VendorLibrarySearch.ValueTolerance * Math.Abs(have))
                return i;
        }
        return -1;
    }
}
