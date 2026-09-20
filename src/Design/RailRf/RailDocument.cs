using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// The settings that are neither a rail's nor a part's — the ones §11.2 puts behind <c>Settings</c>
/// rather than on the face of the window.
///
/// <para><b>Every one of them is stated on the report</b>, which is the whole reason they are
/// document state rather than session state: §2.7's "everything is computed at 20 °C and every report
/// says so" is only true if the 20 is a number the report can read.</para>
/// </summary>
public sealed class RailSettings
{
    /// <summary>
    /// The temperature every resistance in this document is computed at, in DEGREES CELSIUS.
    ///
    /// <para><b>There is no thermal model</b> (§2.7) and this is not one: it is the single stated
    /// basis the copper conductivity is taken at, so a report can say which. 20 °C is the default and
    /// the value every published sheet resistance is quoted at.</para></summary>
    public double CopperTemperatureCelsius { get; set; } = 20.0;

    /// <summary>
    /// The plated barrel thickness a via's current limit is computed from, in MICROMETRES.
    ///
    /// <para><b>Null is not a default</b> (Q-22): brief 6 reads the thickness from the stackup's via
    /// entry where one states it, takes this where it does not, and falls back to the drill-size
    /// table as a sanity band — and <b>every flag says which basis produced it</b>. A defaulted
    /// number here would make all three indistinguishable on the report.</para></summary>
    public double? ViaPlatingThicknessMicrometres { get; set; }

    /// <summary>
    /// The temperature rise a via's current limit is stated at, in KELVIN of rise (°C of rise).
    ///
    /// <para><b>This is a budget, not a temperature</b> (§2.7, brief 6 §5). There is no thermal model
    /// anywhere in railRF and this is not one: the via check computes a limit from the barrel's own
    /// annulus, its span and this number, and <b>nothing else in railRF reads it</b>. 10 °C is what
    /// review's own drill-size table is quoted at, which is why it is the value the setting opens
    /// on — see <see cref="PdnViaCurrentLimit"/> for what the table turns out to be a table OF.</para>
    /// </summary>
    public double ViaTemperatureRiseCelsius { get; set; } = PdnViaCurrentLimit.ReferenceRiseCelsius;
}

/// <summary>
/// Which of the window's four panels are on screen (railrf.md §11.3, point 5).
///
/// <para><b>Absent from the file means all four are shown</b>, which is why every one of these
/// defaults to true and why <see cref="RailDocumentIo"/> writes the block only when something is
/// hidden: a document nobody has collapsed a panel in says nothing about panels, and a `.crail`
/// written before this existed opens exactly as it always did.</para>
///
/// <para><b>It is the one piece of pure VIEW state in this document</b>, and it is here because the
/// owner asked for it to be — a reader who gives the board the whole window for a board review wants
/// it that way again the next time they open that board, not the next time they open any of them.
/// Nothing reads it but the window: no extractor, no solve and no report.</para>
/// </summary>
public sealed class RailPanels
{
    /// <summary>The left column. It is fixed-width or gone and never expands — a rail selector, two
    /// short lists and a target are legible at 300 px and gain nothing from more.</summary>
    public bool ShowSpecification { get; set; } = true;

    /// <summary>The artwork and the map strip above it.</summary>
    public bool ShowBoard { get; set; } = true;

    /// <summary>The parts table under the board.</summary>
    public bool ShowParts { get; set; } = true;

    /// <summary>The results column.</summary>
    public bool ShowResults { get; set; } = true;

    /// <summary>
    /// The text cards under the results plot — the drop, the breakdown, the via check, the
    /// coincidences. <b>Not a fifth panel</b>: it divides the results column rather than the window,
    /// so it takes no part in <see cref="AnyShown"/>'s "at least one panel" rule.
    /// </summary>
    /// <remarks>
    /// Off, the plot is free to take the height the cards were using — which is the whole of the
    /// request (owner, 2026-09-19): the readouts can be a sizable share of a column whose other half
    /// is the one picture in that panel.
    /// </remarks>
    public bool ShowResultText { get; set; } = true;

    /// <summary>The state a document that says nothing opens in — what the file OMITS.</summary>
    public bool AllShown =>
        ShowSpecification && ShowBoard && ShowParts && ShowResults && ShowResultText;

    /// <summary>
    /// Whether anything at all is showing. <b>At least one panel always is</b> (owner,
    /// 2026-09-19): four hidden panels is a window with a toolbar, a status strip and nothing
    /// between them. The window's own buttons cannot reach that state, but a hand-edited
    /// <c>.crail</c> can, so <see cref="RailDocumentIo"/> reads all-hidden as all-shown rather
    /// than opening a window with no content in it.
    /// </summary>
    public bool AnyShown => ShowSpecification || ShowBoard || ShowParts || ShowResults;
}

/// <summary>
/// A railRF document — what a railRF window opens, what <c>circuitrf rail</c> reads, and what
/// revision control keeps (railrf.md §2.2, §2.3).
///
/// <para><b>It holds the RAIL SET.</b> Review's boards run a primary cell into a converter or an LDO
/// that makes a second voltage, so a document that could describe one net could not describe one of
/// these boards. <b>One rail is ANALYSED at a time</b> — Q-6 re-closed rather than reversed — and the
/// set is solved in the dependency order <see cref="RailOrder"/> computes, because a regulator is a
/// load on its input rail and a source on its output rail.</para>
///
/// <para><b>Framework-free, and it draws nothing.</b> It is a document, on the same terms as a
/// <c>.cem</c> beside it: brief 3's extractor reads it, brief 7's window edits it, and neither of
/// those is here.</para>
/// </summary>
public sealed class RailDocument
{
    /// <summary>What this document is called on a report and in a window title. The file name serves
    /// where it is empty.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The imported artwork, as a reference to a CELL in the workspace — never a private copy of the
    /// geometry (§2.3). By default the import lands in the open workspace as an ordinary circuitRF
    /// cell with a layout view, which is what makes the artwork open in the layout editor, run DRC,
    /// render headlessly and be kept by revision control.
    ///
    /// <para><b>Relative to the document</b>, so an archived workspace still resolves — the
    /// repointing rule the workspace archive already follows. Null before anything has been
    /// imported.</para>
    ///
    /// <para>Brief 1 holds it and does not know how it got there: the import dialog is brief 7's, and
    /// nothing here resolves this path.</para></summary>
    public string? ArtworkCellRef { get; set; }

    /// <summary>
    /// The stackup — layer order, copper and dielectric thicknesses, ε_r and tan δ — as a reference to
    /// a <c>.ctech</c>. circuitRF's existing technology model, the same one the EM solver reads and
    /// edited in the same place (§2.2), so railRF adds no stackup format of its own.
    ///
    /// <para>Null resolves the way every other document's technology does: through the workspace.
    /// Document-relative when it is set, for <see cref="ArtworkCellRef"/>'s reason.</para></summary>
    public string? TechnologyRef { get; set; }

    /// <summary>
    /// The part library, keyed by internal part number and holding the model, the voltage rating, the
    /// dielectric class and the footprint (§2.2). A model is entered once and used twenty-two times,
    /// which is why it is a file of its own rather than a column on a BOM.
    ///
    /// <para><b>A path here, and nothing resolves it yet</b> — brief 2 writes the reader.</para></summary>
    public string? PartLibraryRef { get; set; }

    /// <summary>
    /// The board netlist shipped beside the artwork — a net name per pad, the component reference and
    /// pin where there is one, and the plating flag per hole (<c>BoardNetlistFile</c>).
    ///
    /// <para><b>This is the reference that makes a refdes mean anything.</b> Until it resolves,
    /// <c>PdnPad</c> has no source at all: every source and load anchor has to be a COORDINATE, and
    /// <c>PdnMountingLoopExtractor</c> can compute no mounting loop for any part, because a part's
    /// loop is a property of where it was placed and nothing said where that is. Both degrade
    /// silently to the typed path, which is why this lives on the document rather than only in the
    /// import session: an import that read a netlist and was then SAVED used to lose it, and the
    /// reopened document reported typed inductances with nothing to say a computed set had been
    /// available.</para>
    ///
    /// <para>Document-relative, for <see cref="ArtworkCellRef"/>'s reason. Null where the artwork
    /// came with no netlist, which is the ordinary assisted-Gerber case.</para></summary>
    public string? BoardNetlistRef { get; set; }

    /// <summary>
    /// The placement file — a centroid and a side per refdes (<c>PlacementFile</c>).
    ///
    /// <para>What the parts table's <b>Position</b> column reads. It is separate from
    /// <see cref="BoardNetlistRef"/> because the two answer different questions and boards ship them
    /// separately: the netlist says which copper a pin is on, the placement says where the BODY sits
    /// and which side it is on. A board with one and not the other is ordinary.</para>
    ///
    /// <para>Document-relative. Null where none was given.</para></summary>
    public string? PlacementRef { get; set; }

    /// <summary>
    /// The net the reference return is on — <c>GND</c>, <c>AGND</c>.
    ///
    /// <para><b>Named rather than inferred, and the mounting loop has no second side without it.</b>
    /// <c>PdnMountingLoopExtractor</c> tells a part's power pad from its return pad by NET and
    /// nothing else; with no reference net every part is unresolved and says so. railRF will not
    /// guess it from a name that looks like a ground — a board carrying both <c>GND</c> and
    /// <c>PGND</c> would have half its parts measured against the wrong plane, silently.</para></summary>
    public string? ReferenceNet { get; set; }

    /// <summary>Every rail on this board, in declaration order. <see cref="RailOrder"/> computes the
    /// SOLVE order, which is not this one.</summary>
    public List<RailSpec> Rails { get; } = [];

    /// <summary>The settings §11.2 puts behind <c>Settings</c>.</summary>
    public RailSettings Settings { get; set; } = new();

    /// <summary>Which of the window's four panels are on screen. All four, in a document that says
    /// nothing — see <see cref="RailPanels"/>.</summary>
    public RailPanels Panels { get; set; } = new();

    /// <summary>
    /// The drawing layers the WINDOW is not drawing — its own layer-visibility list
    /// (brief-railrf-20-layer-visibility.md R-rail20-1b).
    /// </summary>
    /// <remarks>
    /// <b>Not the <c>.ctech</c>'s, and that is the point.</b> Layer visibility is a property of the
    /// VIEW, not of the process: a technology is a manufacturing document shared across a whole
    /// workspace, so using its <c>Vis</c> boxes as a per-window display switch makes one reader's
    /// navigation another reader's diff. Nothing here is ever written back to a technology.
    ///
    /// <para><b>It is a set of HIDDEN layers, unioned with the technology's own</b> — never a second
    /// answer to the same question. A layer the <c>.ctech</c> does not draw stays undrawn whatever
    /// this says, and exactly one hidden-layer set reaches the renderer and the map overlay
    /// (R-rail20-1c).</para>
    ///
    /// <para><b>Document state, beside <see cref="Panels"/> and for its reason.</b> Which layers a
    /// reader wants to see is a question about a BOARD — this board, read this way — rather than
    /// about a sitting. Absent from the file means nothing is hidden, so a <c>.crail</c> written
    /// before this existed opens exactly as it always did.</para>
    /// </remarks>
    public HashSet<LayerKey> HiddenLayers { get; } = [];

    /// <summary>
    /// Which regions of copper the user has forced the fast model to read either way
    /// (<see cref="PdnCopperClass"/>), keyed by region identity — brief 4's R-rail4-3.
    ///
    /// <para><b>They live on the DOCUMENT, and that is the whole point.</b> §2.9's rule 2 says the
    /// classification is visible and correctable; a correction that lived in the window would be lost
    /// the next time the artwork was re-imported, <i>which is exactly when a classification silently
    /// changes</i>. <see cref="PdnRegionRef"/> is a drawing layer and a vertex the geometry itself
    /// determines, so the same copper re-imported keys the same override and copper that MOVED does
    /// not — a region that has been re-laid out is not the one the user looked at.</para>
    ///
    /// <para>Nothing here classifies anything. The decision is
    /// <see cref="PdnCopperClassifier"/>'s and this is only what overrides it.</para>
    /// </summary>
    public Dictionary<PdnRegionRef, PdnCopperClass> ClassOverrides { get; } = [];

    /// <summary>The rail this name identifies, or null. Case-insensitive, because <c>--rail +1v8</c>
    /// on a shell that lower-cased it is the same request.</summary>
    public RailSpec? Rail(string name) =>
        Rails.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Null when every rail is well formed and no two share a name, or the first refusal sentence.
    /// <see cref="RailDocumentIo"/> applies this on both read AND write, so a document that cannot be
    /// read back is one that was never written.
    /// </summary>
    public string? Refusal()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rail in Rails)
        {
            if (rail.Refusal() is { } r) return r;
            if (!seen.Add(rail.Name))
                return $"Two rails are both called '{rail.Name}'. A rail's name is how --rail picks " +
                       "one and how the solve order names one, so they are distinct.";
        }
        return null;
    }
}
