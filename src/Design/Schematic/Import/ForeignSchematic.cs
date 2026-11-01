// The neutral model every schematic reader fills — brief-schimp-1 R-si1-1, overview D2.
//
// A reader turns bytes into THIS and nothing else: no CellFolder, no Messages, no circuitRF component. Everything after
// it — which part becomes what, the drawing, the nets, the check, the write — is SchematicImportMap /
// SchematicImportBuild / SchematicImport, implemented once for every format.
//
// ── THE TWO COORDINATE CONVENTIONS, AND WHY THERE ARE TWO ─────────────────────────────────────
//
// A SYMBOL's geometry is in mils, Y UP — the convention ComponentPart.cs states for symbols, so the one flip in
//   ComponentImport.BuildSymbol applies once and only there.
// A SHEET's contents are in mils, Y DOWN — schematic orientation, so a sheet point IS a schematic point.
//
// An instance's pin lands at LocalToWorld((x, −y), origin, Rotation, Mirror): Y flipped into schematic orientation, then
// circuitRF's own transform (SchematicGeometry.LocalToWorld) — the mirror across the symbol's vertical axis FIRST, then
// the quarter turns clockwise on screen. A reader whose format composes its transform differently converts to this
// one; nothing downstream knows any other.

using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>What a pin is electrically, as the source declares it. Carried for the report; connectivity never reads it.</summary>
public enum ForeignPinType
{
    Unspecified, Input, Output, Bidirectional, TriState, Passive, PowerIn, PowerOut, OpenCollector, OpenEmitter, NoConnect,
}

/// <summary>Which way a pin's body extends from its connection point, in the symbol's own Y-up frame.</summary>
public enum ForeignPinDirection { Right, Up, Left, Down }

/// <summary>
/// One pin of one unit of a symbol (R-si1-1). <paramref name="Number"/> is the pin's identity (R-si1-2) — never its name,
/// which repeats. <paramref name="X"/>/<paramref name="Y"/> are the connection point in mils, Y up.
/// </summary>
/// <param name="ImplicitNet">A hidden power pin's implicit net — the global net it joins with no wire — or null.</param>
public sealed record ForeignPin(
    string Number, string Name, ForeignPinType ElectricalType, double X, double Y,
    ForeignPinDirection BodySide = ForeignPinDirection.Right, double Length = 0, bool Hidden = false, string? ImplicitNet = null);

/// <summary>One unit of a symbol — the whole symbol for a single-unit part, one gate of a multi-unit one. Unit numbers
/// start at 1. <paramref name="Graphics"/> are mils, Y up, in Core's neutral vocabulary.</summary>
public sealed record ForeignSymbolUnit(int Unit, IReadOnlyList<ForeignPin> Pins, IReadOnlyList<KitSymbolShape> Graphics);

/// <summary>A symbol as the source defines it. <paramref name="Key"/> is unique within the source; instances name it.</summary>
public sealed record ForeignSymbol(string Key, string DisplayName, string LibraryName, IReadOnlyList<ForeignSymbolUnit> Units)
{
    /// <summary>The source draws none of this symbol's pin names (a coaxial connector's "In" and "Ext"); its pins are
    /// then known by their numbers, which circuitRF does not draw either.</summary>
    public bool PinNamesHidden { get; init; }

    public int UnitCount => Units.Count;

    /// <summary>Unit <paramref name="unit"/>, or the only one when the symbol has one.</summary>
    public ForeignSymbolUnit? Unit(int unit) =>
        Units.FirstOrDefault(u => u.Unit == unit) ?? (Units.Count == 1 ? Units[0] : null);
}

/// <summary>A placed unit of a symbol, in sheet coordinates (mils, Y down). <paramref name="Rotation"/> is 0, 90, 180 or
/// 270, clockwise on screen; <paramref name="Mirror"/> flips across the symbol's vertical axis before the turn.</summary>
public sealed record ForeignInstance(
    string Reference, int Unit, string SymbolKey, double X, double Y, int Rotation = 0, bool Mirror = false,
    string? Value = null, IReadOnlyDictionary<string, string>? Properties = null)
{
    /// <summary>The part's text fields where the source shows them — its <c>Reference</c> and its <c>Value</c>; empty when
    /// the format places none.</summary>
    public IReadOnlyList<ForeignField> Fields { get; init; } = [];
}

/// <summary>Which point of a displayed field its anchor is, along the line of text.</summary>
public enum ForeignJustify { Left, Center, Right }

/// <summary>
/// A part's field as the source displays it (schematic-import.md §4, labels): <paramref name="Name"/> is <c>Reference</c> or <c>Value</c>.
/// <paramref name="X"/>,<paramref name="Y"/> is in sheet coordinates (mils, Y down): along the text, the point
/// <paramref name="Justify"/> names — the left end, the middle or the right end — and across it, the text's middle.
/// <paramref name="Vertical"/> is text the source runs up the page; <paramref name="Hidden"/> is a field it does not show.
/// </summary>
public sealed record ForeignField(string Name, string Text, double X, double Y, ForeignJustify Justify = ForeignJustify.Center,
                                  bool Vertical = false, bool Hidden = false);

/// <summary>A wire, as a polyline in sheet coordinates.</summary>
public sealed record ForeignWire(IReadOnlyList<(double X, double Y)> Points);

/// <summary>A junction dot: every wire through this point is joined.</summary>
public sealed record ForeignJunction(double X, double Y);

/// <summary>How far a label's name reaches.</summary>
public enum ForeignLabelKind
{
    /// <summary>This sheet only.</summary>
    Local,
    /// <summary>Every sheet.</summary>
    Global,
    /// <summary>The parent sheet's pin of the same name.</summary>
    Hierarchical,
    /// <summary>Every page of a flat multi-page design.</summary>
    OffPage,
    /// <summary>A power port: a global net named by its text.</summary>
    Power,
}

/// <summary>A label: a name attached at <paramref name="X"/>,<paramref name="Y"/> to whatever wire or pin is there.</summary>
public sealed record ForeignLabel(ForeignLabelKind Kind, string Text, double X, double Y);

/// <summary>A no-connect mark: the pin at this point is deliberately left open.</summary>
public sealed record ForeignNoConnect(double X, double Y);

/// <summary>One port pin of a placed sheet, on its rectangle's edge; it joins the child's hierarchical label of the
/// same name.</summary>
public sealed record ForeignSheetPin(string Name, double X, double Y);

/// <summary>A placed child sheet: <paramref name="SheetName"/> names the <see cref="ForeignSheet"/> it shows, and the
/// rectangle's top-left is <paramref name="X"/>,<paramref name="Y"/>.</summary>
public sealed record ForeignSheetInstance(
    string Name, string SheetName, double X, double Y, double Width, double Height, IReadOnlyList<ForeignSheetPin> Pins);

/// <summary>Free text, anchored at its top-left unless the source states where: <paramref name="Align"/> and
/// <paramref name="VAlign"/> are the source's own justification about (<paramref name="X"/>, <paramref name="Y"/>).</summary>
public sealed record ForeignText(string Text, double X, double Y, int Rotation = 0, double Size = 50,
                                 CircuitRF.Design.Symbol.SymbolTextAlign? Align = null,
                                 CircuitRF.Design.Symbol.SymbolTextVAlign? VAlign = null);

/// <summary>A free graphic line (a polyline; closed returns to its first point).</summary>
public sealed record ForeignGraphic(IReadOnlyList<(double X, double Y)> Points, bool Closed = false);

/// <summary>One sheet (or page) and its contents, in sheet coordinates — mils, Y down.</summary>
public sealed record ForeignSheet(string Name, double Width = 0, double Height = 0)
{
    public IReadOnlyList<ForeignInstance>      Instances      { get; init; } = [];
    public IReadOnlyList<ForeignWire>          Wires          { get; init; } = [];
    public IReadOnlyList<ForeignJunction>      Junctions      { get; init; } = [];
    public IReadOnlyList<ForeignLabel>         Labels         { get; init; } = [];
    public IReadOnlyList<ForeignNoConnect>     NoConnects     { get; init; } = [];
    public IReadOnlyList<ForeignSheetInstance> SheetInstances { get; init; } = [];
    public IReadOnlyList<ForeignText>          Texts          { get; init; } = [];
    public IReadOnlyList<ForeignGraphic>       Graphics       { get; init; } = [];
}

/// <summary>A pin's identity across the whole design (R-si1-2): the part's reference — one reference for every unit of
/// a multi-unit part — and the pin's number.</summary>
public readonly record struct ForeignPinRef(string Reference, string PinNumber)
{
    public override string ToString() => $"{Reference}.{PinNumber}";
}

/// <summary>
/// A schematic design as a reader read it (R-si1-1). <paramref name="Format"/> names the format by its extension
/// (overview D7); <paramref name="RootSheet"/> names the top sheet in <paramref name="Sheets"/>. A flat multi-page
/// design has several sheets and <paramref name="Hierarchical"/> false — its pages are tiled into one schematic; a
/// hierarchical one has its children placed by <see cref="ForeignSheetInstance"/>.
/// </summary>
public sealed record ForeignSchematic(
    string Format, string SourcePath, IReadOnlyList<ForeignSheet> Sheets, IReadOnlyList<ForeignSymbol> Symbols,
    string RootSheet, bool Hierarchical = false)
{
    /// <summary>The connectivity the format itself states, as sets of pins — null when it states none and the drawing's
    /// own geometry is the only account of it (overview D8).</summary>
    public IReadOnlyList<IReadOnlySet<ForeignPinRef>>? DeclaredNets { get; init; }

    /// <summary>What the reader could not carry, one sentence each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The format's version as the file states it (<c>20231120</c>, <c>2 0 0</c>) — reported, never branched on;
    /// null when the file states none.</summary>
    public string? Version { get; init; }

    public ForeignSymbol? Symbol(string key) => Symbols.FirstOrDefault(s => s.Key == key);

    public ForeignSheet? Sheet(string name) => Sheets.FirstOrDefault(s => s.Name == name);

    /// <summary>The sheets in the order they are drawn: the root first, then the rest in source order.</summary>
    public IEnumerable<ForeignSheet> Pages =>
        Sheets.Where(s => s.Name == RootSheet).Concat(Sheets.Where(s => s.Name != RootSheet));
}

/// <summary>Where a schematic Import Schematic wrote came from (R-si1-8). Plain data, persisted in the <c>.csch</c> as
/// its <c>SchematicImportSource</c> block. The source's file NAME only, never its path.</summary>
public sealed class SchematicImportProvenance
{
    /// <summary>The format, by its extension (overview D7).</summary>
    public string Format { get; set; } = "";

    /// <summary>The source file's name — never its path.</summary>
    public string Source { get; set; } = "";

    /// <summary>When, UTC, to the second — <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    public string ImportedUtc { get; set; } = "";
}
