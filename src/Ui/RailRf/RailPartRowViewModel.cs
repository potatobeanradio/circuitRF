using System;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One row of the parts table (§2.3 step 3, R-rail7-9).
/// </summary>
/// <remarks>
/// <b>Anything railRF could not resolve is listed AS unresolved rather than defaulted</b> — the
/// brief's own sentence, and the whole reason this row carries an <see cref="IsUnresolved"/> flag
/// instead of simply leaving a cell blank. A blank cell and a resolved zero look alike at a glance,
/// and §9's finding is that partial population must be visible.
///
/// <para><b>Three of its columns carry PROVENANCE rather than data</b>, and all three exist because
/// §9 says their absence is silent:</para>
/// <list type="bullet">
/// <item><see cref="ModelSourceText"/> — library row / attached file, and WHICH WON (R-rail2-11).</item>
/// <item><see cref="DeratedText"/> beside <see cref="MarkedText"/>, and which was used (Q-12).</item>
/// <item><see cref="EsrText"/> — measured / stated / class default, with a class default marked
///   <b>indicative</b> (Q-15).</item>
/// </list>
///
/// <para><b>Read-only, deliberately.</b> A part's model is the part library's (a model is entered once
/// and used twenty-two times, which is why it is a file of its own); a row that could be edited here
/// would be a second place the same number lives.</para>
/// </remarks>
public sealed class RailPartRowViewModel
{
    /// <summary>What an unresolvable cell reads. One spelling, in one place, so the table cannot say
    /// it three ways.</summary>
    public const string UnresolvedText = "unresolved";

    /// <param name="part">The RAIL's own row — <b>the subject</b> (R-rail18-5a). The table lists the
    /// selected rail's parts; the BOM, the placement and the library each fill a column and none of
    /// them decides whether a row exists.</param>
    /// <param name="bom">The BOM row, or null where the BOM names no such reference.</param>
    /// <param name="model">What the part library resolved, or null where there is no library.</param>
    /// <param name="mountingInductanceHenries">The mounting loop — the document's own where it
    /// states one, or the computed one — or null where neither exists.</param>
    /// <param name="position">Where the placement put it, already formatted, or null.</param>
    public RailPartRowViewModel(
        RailPart part,
        BomRow? bom,
        PartModelResolution? model,
        double? mountingInductanceHenries,
        string? position)
    {
        ArgumentNullException.ThrowIfNull(part);

        _part = part;
        _bom = bom;
        _model = model;
        Refdes = part.Refdes;
        MountingInductanceHenries = mountingInductanceHenries;
        Position = position;
    }

    private readonly RailPart _part;
    private readonly BomRow? _bom;
    private readonly PartModelResolution? _model;

    /// <summary>The reference.</summary>
    public string Refdes { get; }

    /// <summary>
    /// The internal part number, or <see cref="UnresolvedText"/>.
    /// </summary>
    /// <remarks>
    /// <b>The rail's own first, the BOM's where the rail states none</b> (R-rail18-5a). A refdes the
    /// rail names and the BOM does not is a row with an unresolved part number, which is the state
    /// this word already exists to say — not a missing row.
    /// </remarks>
    public string PartNumber =>
        _part.PartNumber is { Length: > 0 } own ? own
        : _bom?.PartNumber is { Length: > 0 } p ? p
        : UnresolvedText;

    /// <summary>Where this row came from — typed, or pre-filled from the bill of materials. Shown on
    /// the row, because a pre-filled number nobody checked is the one that will be wrong.</summary>
    public RailPartOrigin Origin => _part.Origin;

    /// <summary>The same, as the row reads it.</summary>
    public string OriginText => _part.Origin == RailPartOrigin.Bom
        ? "pre-filled from the bill of materials"
        : "typed on the rail";

    /// <summary>The marked value, as the BOM states it, or <see cref="UnresolvedText"/>.</summary>
    public string MarkedText => _bom?.Value is { Length: > 0 } v ? v : UnresolvedText;

    /// <summary>
    /// The derated value, where a bias curve produced one — <b>beside the marked one, never instead
    /// of it</b> (Q-12). Where no curve exists this reads <see cref="UnresolvedText"/>, and the count
    /// of such parts is a headline number on the status strip.
    /// </summary>
    public string DeratedText { get; init; } = UnresolvedText;

    /// <summary>Which of the two <see cref="MarkedText"/>/<see cref="DeratedText"/> the solve used.
    /// Stated, because a table showing both and saying neither is worse than showing one.</summary>
    public string ValueUsedText { get; init; } = "marked";

    /// <summary>
    /// Library row / attached file, and which won.
    /// </summary>
    public string ModelSourceText => _model switch
    {
        null                                                 => UnresolvedText,
        { Row: null }                                        => UnresolvedText,
        { Source: PartModelSource.AttachedFile, FilePath: { } f }
            => $"file — {System.IO.Path.GetFileName(f)}",
        _                                                    => "library row",
    };

    /// <summary>
    /// Measured / stated / class default — with a class default marked <b>indicative</b>.
    /// </summary>
    /// <remarks>
    /// The class default is the NORMAL case rather than the degraded one (Q-15), and the word matters
    /// because a mask margin in dB computed from an indicative peak looks exactly as authoritative as
    /// one computed from a measurement.
    /// </remarks>
    public string EsrText => _model?.EsrBasis switch
    {
        EsrProvenance.Measured     => "measured",
        EsrProvenance.Stated       => "stated",
        EsrProvenance.ClassDefault => "class default — indicative",
        _                          => UnresolvedText,
    };

    /// <summary>True where the ESR is a class default, so the row can be marked in the table.</summary>
    public bool IsEsrIndicative => _model?.EsrBasis == EsrProvenance.ClassDefault;

    /// <summary>The computed mounting inductance, base SI, or null.</summary>
    public double? MountingInductanceHenries { get; }

    /// <summary>The same, as the column reads it.</summary>
    public string MountingInductanceText =>
        RailValueFormat.FormatWithUnit(MountingInductanceHenries, RailQuantity.Inductance, UnresolvedText);

    /// <summary>Where the placement put it, or null where it did not land.</summary>
    public string? Position { get; }

    /// <summary>The same, as the column reads it.</summary>
    public string PositionText => Position is { Length: > 0 } p ? p : UnresolvedText;

    /// <summary>
    /// True when this row could not be resolved at all — no part number, or no library row for it.
    /// </summary>
    /// <remarks>
    /// <b>The window renders such a row as unresolved and populates NO numeric column for it</b>
    /// (R-rail7-9's own test). A half-filled row is the shape a defaulted one takes, and the whole
    /// point of listing it is that it is not one.
    /// </remarks>
    public bool IsUnresolved =>
        PartNumber == UnresolvedText || _model is null || _model.Row is null;

    /// <summary>What the dielectric class was parsed as, or empty — shown beside the description it
    /// came from, for correction (brief 2's R-rail2-5).</summary>
    public string DielectricClassText => _bom?.Parsed.DielectricClass ?? "";
}
