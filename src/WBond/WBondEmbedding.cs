using System.Text;

namespace CircuitRF.WBond;

/// <summary>
/// The design a wBond component carries INSIDE the schematic (wbond.md §5.1).
///
/// <h3>Why a placed wBond embeds its design rather than naming a file</h3>
/// <para>A <c>.wBond</c> is a wirebond design <i>plus</i>, optionally, the layout artwork it was drawn
/// over — cells, rectangles, MLINs. A schematic component has nowhere to put artwork, so pointing one
/// at a whole <c>.wBond</c> asks it to reference something most of which it cannot express. It also
/// made a freshly-dropped component reference NOTHING, which is what the "Not Found" placeholder was
/// reporting: correctly, and uselessly.</para>
///
/// <para>So the component carries its own wires. A dropped wBond arrives with
/// <see cref="DefaultDesign"/> — one array, one wire — which renders, wires up and simulates
/// immediately; File ▸ Import ▸ Wirebond Wires… replaces that payload from a real design. Nothing is
/// resolved at render time, so a schematic is self-contained and "Not Found" is unrepresentable.</para>
///
/// <h3>Base64, and why not raw JSON</h3>
/// <para>The payload has to survive BOTH <c>.csch</c> (a JSON string) and <c>.cnl</c> (a
/// whitespace-delimited line format whose only string escape is a pair of quotes, with no way to
/// escape a quote inside one). A wBond design's JSON is full of quotes, so it cannot be a quoted
/// <c>.cnl</c> token. Base64 is a single bare token with no quote, space or newline in it — it needs
/// no quoting rule on either side and no reader change at all.</para>
///
/// <para><b>The padding is stripped, and that is load-bearing.</b> Standard base64 pads with
/// <c>=</c>, and <c>CnlReader.MergeSpacedAssignments</c> reads a token ENDING in <c>=</c> as
/// <c>name=</c> with an empty value and glues the NEXT token on as that value (the known
/// empty-parameter-value defect recorded in <c>src/Core/CLAUDE.md</c>). So a padded payload followed
/// by any other parameter on the same instance line — <c>Design=…Cn0= LoopHeight=loopH</c> — arrives
/// at the factory as one run-on string and decodes to nothing, while an unpadded one is unambiguous.
/// <see cref="TryDecode"/> re-pads before decoding. Do not "restore" the padding here.</para>
///
/// <para><see cref="TryDecode"/> also accepts raw JSON, so a hand-authored netlist can still be
/// written readably; only the writer standardises on base64.</para>
/// </summary>
public static class WBondEmbedding
{
    /// <summary>The component parameter that carries the design.</summary>
    public const string DesignParameter = "Design";

    /// <summary>
    /// Encodes a design for storage on a component — <b>wires only</b>.
    ///
    /// <para>Embedded layout geometry and view state are dropped deliberately, not incidentally: a
    /// schematic component is the wires, and carrying a copy of someone's artwork inside every
    /// placed instance is the thing this whole mechanism exists to avoid. The artwork route is
    /// File ▸ Import ▸ Wirebond as Cell…, which makes it a real layout view instead.</para>
    /// </summary>
    public static string Encode(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);

        string? geometry = design.EmbeddedGeometryJson;
        string? viewState = design.ViewStateJson;
        try
        {
            design.EmbeddedGeometryJson = null;
            design.ViewStateJson = null;
            // TrimEnd('=') — see the class doc: a trailing '=' makes the .cnl reader swallow the
            // next parameter on the line.
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(WBondIo.Write(design))).TrimEnd('=');
        }
        finally
        {
            // The caller's design is theirs; stripping is a property of the PAYLOAD, not an edit.
            design.EmbeddedGeometryJson = geometry;
            design.ViewStateJson = viewState;
        }
    }

    /// <summary>
    /// Decodes a stored payload. Accepts base64 (what <see cref="Encode"/> writes) or raw JSON (what
    /// a hand-authored <c>.cnl</c> may carry). Returns false rather than throwing — an unreadable
    /// payload is a reported, repairable state, not a crash on a render pass.
    /// </summary>
    public static bool TryDecode(string? payload, out WBondDesign? design)
    {
        design = null;
        if (string.IsNullOrWhiteSpace(payload)) return false;

        string text = payload.Trim();
        try
        {
            // A design's JSON always starts with '{'; base64 never does.
            if (text[0] != '{')
                text = Encoding.UTF8.GetString(Convert.FromBase64String(RePad(text)));

            design = WBondIo.Read(text);
            return true;
        }
        catch
        {
            design = null;
            return false;
        }
    }

    /// <summary>
    /// Restores the <c>=</c> padding <see cref="Encode"/> strips, so an already-padded payload (a
    /// hand-authored file, or one written before the padding was dropped) decodes unchanged.
    /// </summary>
    private static string RePad(string text) =>
        (text.Length % 4) switch { 2 => text + "==", 3 => text + "=", _ => text };

    /// <summary>
    /// The ordered array names a payload declares — everything the schematic SYMBOL depends on, and
    /// the identity a placed instance's wiring was drawn against.
    ///
    /// <para>Memoised by payload string: the symbol resolver asks this on every render pass, and the
    /// answer cannot change without the payload changing.</para>
    /// </summary>
    public static IReadOnlyList<string> ArrayNamesOf(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return [];

        lock (_gate)
            if (_arrayNames.TryGetValue(payload, out var hit)) return hit;

        IReadOnlyList<string> names = TryDecode(payload, out var design) && design is not null
            ? [.. design.Arrays.Select(a => a.Name)]
            : [];

        lock (_gate)
        {
            // Bounded: a schematic holds a handful of distinct payloads, but an import loop could
            // otherwise grow this without limit. Dropping the whole map is fine — it re-derives.
            if (_arrayNames.Count > 256) _arrayNames.Clear();
            _arrayNames[payload] = names;
        }
        return names;
    }

    private static readonly Dictionary<string, IReadOnlyList<string>> _arrayNames = new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();

    /// <summary>
    /// <b>The default wire, as five numbers in one place.</b> Owner, 2026-08-16: "make it easy to
    /// change the default wire — we may tweak it many times." Every value the shipped wire is built
    /// from lives here, so a tweak is an edit to a constant rather than a hunt through a constructor.
    ///
    /// <para>The wire runs <b>north/south</b> (along +y) and spans <see cref="DefaultWire.SpanMils"/>
    /// foot-to-foot. It used to run east/west over 100 mil; both were arbitrary, and north/south is
    /// the orientation the profile view's new default plane (YZ) shows side-on rather than
    /// foreshortened to nothing.</para>
    /// </summary>
    public static class DefaultWire
    {
        /// <summary>Foot-to-foot span, in mils — the length of the shipped wire.</summary>
        public const double SpanMils = 30.0;

        /// <summary>Peak loop height above the chord, in mils, for the shipped ball-bond profile.</summary>
        public const double LoopHeightMils = 20.0;

        /// <summary>Wire diameter, in mils.</summary>
        public const double DiameterMils = 1.0;

        /// <summary>
        /// The z BOTH feet land at, in mils — the shipped value of the <b>Wire z-height</b> setting
        /// (Settings ▸ Wirebonds; <c>WBondDefaults.FootZNm</c> is what reads it, and every creation
        /// path in the UI passes that rather than this).
        ///
        /// <para><b>One number, and the feet are level</b> (owner, 2026-08-17). It was an asymmetric
        /// 4 mil → 1 mil descent, die pad to package lead. That is a real case but the wrong STARTING
        /// point: a level wire is the shape a user reads as neutral, its loop is symmetric about
        /// mid-span, and a drop is something they add by moving a foot rather than something they
        /// have to notice and undo. Nothing in the geometry cares either way — a profile scales its
        /// loop about the CHORD, so feet at different z were never a special case, and moving one foot
        /// still produces exactly the descent that was shipped before.</para>
        /// </summary>
        public const double FootZMils = 4.0;

        /// <summary>The group the shipped wire lands in.</summary>
        public const string GroupName = "G1";

        /// <summary>The input foot's position, at the shipped z.</summary>
        public static Point3 Start => StartAt(WBondUnits.ToNm(FootZMils, WBondUnit.Mil));

        /// <summary>
        /// The output foot's position — <b>north of the input foot</b>, which is what makes the
        /// shipped wire north/south. Change the axis here and nothing else needs to move.
        /// </summary>
        public static Point3 End => EndAt(WBondUnits.ToNm(FootZMils, WBondUnit.Mil));

        /// <summary>The input foot at a given z — what a caller holding the user's own setting uses.</summary>
        public static Point3 StartAt(long footZNm) => new(0, 0, footZNm);

        /// <summary>The output foot at a given z, one span north of <see cref="StartAt"/>.</summary>
        public static Point3 EndAt(long footZNm) =>
            new(0, WBondUnits.ToNm(SpanMils, WBondUnit.Mil), footZNm);
    }

    /// <summary>
    /// A minimal valid design: one array, one wire arched on the seed shape.
    ///
    /// <para><b>This is the one definition</b> — the blank wBond editor and a freshly-dropped
    /// schematic component both start here, so "what a new wBond is" cannot come to mean two
    /// different things. Every number it uses is a named constant on <see cref="DefaultWire"/>.</para>
    /// </summary>
    /// <param name="footZNm">
    /// The z both feet land at, or null for the shipped <see cref="DefaultWire.FootZMils"/>.
    ///
    /// <para><b>A parameter rather than a preference read, because this project cannot read one</b> —
    /// <c>src/WBond</c> is framework-free and the settings live in the UI. Every UI creation path
    /// passes <c>WBondDefaults.FootZNm</c>, which is the user's Settings ▸ Wirebonds value; the
    /// no-argument form is what a test, a headless caller and <see cref="DefaultPayload"/> use.</para>
    /// </param>
    public static WBondDesign DefaultDesign(long? footZNm = null)
    {
        long footZ = footZNm ?? WBondUnits.ToNm(DefaultWire.FootZMils, WBondUnit.Mil);

        var design = new WBondDesign();
        design.Arrays.Add(new WireArray
        {
            Name = DefaultWire.GroupName,
            Wires =
            {
                LoopShape.CreateSeedWire(
                    DefaultWire.StartAt(footZ), DefaultWire.EndAt(footZ),
                    WBondUnits.ToNm(DefaultWire.DiameterMils, WBondUnit.Mil),
                    WireMaterials.Default.Name,
                    WBondUnits.ToNm(DefaultWire.LoopHeightMils, WBondUnit.Mil)),
            },
        });
        return design;
    }

    /// <summary>
    /// The encoded default, computed once. Every freshly-placed wBond carries this exact string, so
    /// two of them share one generated symbol and one cache entry.
    /// </summary>
    public static string DefaultPayload => _defaultPayload ??= Encode(DefaultDesign());

    private static string? _defaultPayload;
}
