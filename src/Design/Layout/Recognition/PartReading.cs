// Every two-terminal part on the board — brief-artsch-4-parts-and-parts-table.md R-as4-1 … R-as4-9;
// overview D9, D10, D16; docs/design/artwork-to-schematic.md §5.
//
// THE EVIDENCE, STRONGEST FIRST, EACH FIELD RECORDING WHERE IT CAME FROM:
//   1. placed footprint instances — designator, PartKind, the `smt-<case>@<density>` cell, their pads;
//   2. a placement file landed on the board's pads, and a bill of materials by designator;
//   2½. pads the artwork declares as one part's pins (AS-12, DeclaredPads) — after the placement file for the
//      designator, ahead of everything else, because the file says which pads form a part;
//   3. land patterns — two pads matching a case's generated lands, from the mask or paste openings where
//      the technology has those layers, else from pad-shaped copper and the trace review's "pad" ends;
//   3½. a body outline over a gap in the copper — a silkscreen or assembly rectangle whose two short ends lie on two
//      different pieces of copper, for a part bridging a gap cut in a line or a line's edge and a pour with no land of
//      its own (BodyOutlineParts); below land patterns, so it names no pads a land pattern already took;
//   4. silkscreen designators (AS-10, Silkscreen/), and any IPartEvidenceSource — each source's designators given
//      to the unnamed parts one to one (RefdesAssociation);
//   5. nothing — the pads matched, and kind and value are unknown.
//
// Nothing here is new knowledge of the board: the pads come from PlacedPins (railRF's and LVS's walk),
// a placement row lands where RailPartMarks says the part's body is, a bill of materials is read with
// the pasted-table recognisers, the terminal pairing is railRF's SeriesTerminals, and the connection is
// read off the AS-3 islands. A pad is in one part at most.

using Clipper2Lib;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What a part reading reads — the board graph and what AS-3 had in hand when it built it.</summary>
internal sealed class PartReadingContext
{
    public required RecognitionInput Input { get; init; }
    public required BoardGraph Board { get; init; }
    public required BoardCopper Copper { get; init; }

    /// <summary>The artwork in scope — every layer, clipped where a scope cut it.</summary>
    public required IReadOnlyList<LayoutShape> Shapes { get; init; }

    /// <summary>The scope, or null for the whole board.</summary>
    public Paths64? Scope { get; init; }

    /// <summary>Every placed part's pads, each on the layer its land is on.</summary>
    public required IReadOnlyList<PlacedPin> PlacedPads { get; init; }

    /// <summary>The parts the artwork's pad attributes declare that no placed instance claims (AS-12).</summary>
    public IReadOnlyList<DeclaredPart> DeclaredParts { get; init; } = [];

    /// <summary>The trace review's runs over the artwork in scope — asked for only when a side has no
    /// mask or paste openings to read pads from.</summary>
    public required Func<IReadOnlyList<TraceRun>> TraceRuns { get; init; }

    public required RailLengthFormat Format { get; init; }
}

/// <summary>Reads the parts. Called by <see cref="ArtworkRecognition.Recognize"/>; written once (D2).</summary>
public static class PartReading
{
    /// <summary>An opening whose area is below this share of its bounding box is not a rectangular pad —
    /// a round via opening is π/4 of its box; a rounded-rectangle land is above 0.9.</summary>
    public const double MinRectangularity = 0.82;

    // ── one part while it is being read ─────────────────────────────────────────────────────────────

    private sealed class Draft
    {
        public string? Refdes;
        public PartEvidenceSource RefdesSource;
        public PartEvidenceSource PadSource;
        public readonly List<PlacedPin> Pads = [];
        public SymbolKind? PlacedKind;
        /// <summary>The linked schematic component's kind and value text, for an instance the schematic owns.</summary>
        public SymbolKind? LinkedKind;
        public string? LinkedValue;
        public SmtCase? InstanceCase, LandCase, PlacementCase;
        public LandPatternCandidate? Land;
        public Bbox Body = Bbox.Empty;
        public SilkTextLine? Silk;
        public bool Placed;
        public readonly List<string> Notes = [];

        public long X => Pads.Count == 0 ? 0 : (long)Math.Round(Pads.Average(p => (double)p.X));
        public long Y => Pads.Count == 0 ? 0 : (long)Math.Round(Pads.Average(p => (double)p.Y));
    }

    internal static PartsTable Read(PartReadingContext ctx, RecognitionReport report)
    {
        var input = ctx.Input;
        var view = input.View;
        var tech = input.Technology!;
        var fmt = ctx.Format;
        int dbu = view.DbuPerMicron;

        string? clayDir = input.ClayPath is { } cp ? Path.GetDirectoryName(Path.GetFullPath(cp)) : null;
        string? baseDir = WorkspaceRootFinder.WorkspaceDirOf(clayDir) ?? clayDir;
        var drafts = new List<Draft>();

        // ── 1. placed footprint instances ─────────────────────────────────────────────────────────
        var cells = PlacedPins.FootprintsOf(view);
        var linked = LinkedComponents(input.ClayPath);
        foreach (var group in ctx.PlacedPads.Where(p => p.Refdes is { Length: > 0 })
                                            .GroupBy(p => p.Refdes!, StringComparer.OrdinalIgnoreCase))
        {
            var d = new Draft { Refdes = group.Key, RefdesSource = PartEvidenceSource.Instance, PadSource = PartEvidenceSource.Instance };
            d.Pads.AddRange(group);
            if (!InScope(ctx, d.X, d.Y)) continue;
            var placed = view.Instances.Where(i => string.Equals(i.DisplayRefDes, group.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            d.PlacedKind = placed.Select(LayoutPartKind.Of).FirstOrDefault(k => k is not null);
            // An instance the schematic owns stores no kind (LayoutPartKind.Of) - the schematic knows - so it is read there.
            if (placed.Select(i => i.SchematicId).FirstOrDefault(id => id is { Length: > 0 }) is { } id
                && linked.TryGetValue(id, out var comp))
            {
                d.LinkedKind = comp.Symbol;
                d.LinkedValue = LinkedValueText(comp);
            }
            if (cells.TryGetValue(group.Key, out string? cell)) d.InstanceCase = CaseOfCell(cell);
            drafts.Add(d);
        }

        // ── 2½. the parts the artwork's pad attributes declare, their pads in pin order ──────────────
        var declared = new List<Draft>();
        foreach (var part in ctx.DeclaredParts)
        {
            if (drafts.Any(d => string.Equals(d.Refdes, part.Refdes, StringComparison.OrdinalIgnoreCase))) continue;
            var d = new Draft { Refdes = part.Refdes, RefdesSource = PartEvidenceSource.DeclaredPads, PadSource = PartEvidenceSource.DeclaredPads, Body = part.Body };
            d.Pads.AddRange(part.Pads.Select(p => p.ToPlaced()));
            drafts.Add(d);
            declared.Add(d);
        }

        // ── 3. land patterns on the copper no instance claims ──────────────────────────────────────
        var outlines = Outlines(ctx, report);
        var claimed = ctx.PlacedPads;
        outlines = [.. outlines.Where(o => !claimed.Any(p => Grow(o.Bounds, o.Width / 10, o.Height / 10).Contains(p.X, p.Y)))];
        foreach (var c in LandPatternMatch.Find(outlines, dbu))
        {
            var a = outlines[c.A];
            var b = outlines[c.B];
            // A land on declared pads gives that part its case; it makes no part of its own.
            Draft? OnDeclared(PadOutline o) => declared.FirstOrDefault(
                d => d.Pads.Any(p => Grow(o.Bounds, o.Width / 10, o.Height / 10).Contains(p.X, p.Y)));
            Draft? da = OnDeclared(a), db = OnDeclared(b);
            if (da is not null || db is not null)
            {
                if (da is not null && ReferenceEquals(da, db) && da.Pads.Count == 2 && da.LandCase is null)
                {
                    da.LandCase = c.Best.Case;
                    da.Land = c;
                    if (c.RunnersUp.Count > 0)
                        da.Notes.Add($"land pattern {c.Best.Case.Code} or {string.Join(" or ", c.RunnersUp.Select(r => r.Case.Code))}");
                }
                continue;
            }
            var d = new Draft { PadSource = PartEvidenceSource.LandPattern, LandCase = c.Best.Case, Land = c, Body = a.Bounds.Union(b.Bounds) };
            d.Pads.Add(new PlacedPin(null, "1", null, a.CentreX, a.CentreY, PinSource.Artwork) { Layer = a.Layer });
            d.Pads.Add(new PlacedPin(null, "2", null, b.CentreX, b.CentreY, PinSource.Artwork) { Layer = b.Layer });
            if (c.RunnersUp.Count > 0)
                d.Notes.Add($"land pattern {c.Best.Case.Code} or {string.Join(" or ", c.RunnersUp.Select(r => r.Case.Code))}");
            drafts.Add(d);
        }

        // ── 3½. body outlines over gaps in the copper: parts with no land of their own ─────────────
        var bodies = BodyOutlineParts(ctx, drafts, out var overMany);
        drafts.AddRange(bodies);

        // ── 2a. the placement file, landed where RailPartMarks puts the part's body ────────────────
        var byRefdes = new Dictionary<string, Draft>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drafts) if (d.Refdes is { } r) byRefdes[r] = d;
        var placementMissed = new List<PlacementRow>();
        var placementRenamed = new List<(string Declared, PlacementRow Row)>();
        if (input.Placement is { Refusal: null } placement)
        {
            foreach (var row in placement.Rows)
            {
                if (!InScope(ctx, row.X, row.Y)) continue;
                SmtCase? rowCase = FootprintTokens.Match(row.Footprint) is { Outcome: FootprintTokenOutcome.Matched, Case: { } fc } ? fc : null;
                if (byRefdes.TryGetValue(row.Refdes, out var known))
                {
                    known.Placed = true;
                    known.PlacementCase ??= rowCase;
                    continue;
                }

                var body = RailPartMarks.For(new RailPortAnchor { Refdes = row.Refdes }, row.Refdes, [], placement)?.Body;
                var hit = body is { } box
                    ? drafts.Where(d => d.Refdes is null && (box.Contains(d.X, d.Y) || d.Pads.Any(p => box.Contains(p.X, p.Y))))
                            .OrderBy(d => Dist(d.X, d.Y, row.X, row.Y)).FirstOrDefault()
                    : null;
                // Declared pads under a placement row of another name: the placement file is the stronger source for
                // the designator, and the disagreement is reported (R-as12-6).
                if (hit is null && body is { } under
                    && declared.Where(d => d.RefdesSource == PartEvidenceSource.DeclaredPads && d.Pads.Any(p => under.Contains(p.X, p.Y)))
                               .OrderBy(d => Dist(d.X, d.Y, row.X, row.Y)).FirstOrDefault() is { } renamed)
                {
                    placementRenamed.Add((renamed.Refdes!, row));
                    renamed.Notes.Add($"its pads declare {renamed.Refdes}; the placement file names it {row.Refdes}, which was taken");
                    byRefdes.Remove(renamed.Refdes!);
                    hit = renamed;
                }
                if (hit is null) { placementMissed.Add(row); continue; }
                hit.Refdes = row.Refdes;
                hit.RefdesSource = PartEvidenceSource.Placement;
                hit.Placed = true;
                hit.PlacementCase = rowCase;
                byRefdes[row.Refdes] = hit;
            }
        }

        // ── 4. the silkscreen, then any further source: each one's designators given to the unnamed parts ──
        var silk = SilkscreenReading.Empty;
        var unassociated = new List<PartClaim>();
        // Designators typed as text first: they are exact, so a stroked reading only names what they leave (R-as10-4).
        var typed = new TypedSilkscreenEvidence(input.Shapes, tech);
        Name(typed.Claims());
        if (drafts.Any(d => d.Refdes is null))
        {
            silk = SilkscreenText.Read(input.Shapes, tech, GlyphTemplates.ForUser());
            Name(silk.Designators.Select(l => new PartClaim(PartEvidenceSource.Silkscreen, l.X, l.Y, l.Refdes!, 0) { Line = l }));

            // A line that would be a designator but for an uncertain glyph names nothing, but goes with the part it
            // is printed beside: correcting that part's generated designator can then learn the glyph (R-as10-5).
            var unsure = silk.Lines.Where(l => l.Refdes is null && l.Uncertain > 0)
                             .Select(l => new PartClaim(PartEvidenceSource.Silkscreen, l.X, l.Y, l.Text, 0) { Line = l }).ToList();
            var rest = drafts.Where(d => d.Refdes is null && !d.Body.IsEmpty).ToList();
            var beside = RefdesAssociation.Assign(unsure, [.. rest.Select(d => new RefdesCandidate(d.Body))]);
            for (int i = 0; i < unsure.Count; i++)
                if (beside[i] >= 0)
                {
                    rest[beside[i]].Silk = unsure[i].Line;
                    rest[beside[i]].Notes.Add($"the silkscreen beside it reads '{unsure[i].Refdes}', with a glyph that fits two characters");
                }
        }
        foreach (var source in input.EvidenceSources) Name(source.Claims(input, ctx.Board));

        void Name(IEnumerable<PartClaim> from)
        {
            // A designator a stronger source already gave is that part's, not a claim on another.
            var claims = from.Where(c => !byRefdes.ContainsKey(c.Refdes)).ToList();
            var unnamed = drafts.Where(d => d.Refdes is null && !d.Body.IsEmpty).ToList();
            var assigned = RefdesAssociation.Assign(claims, [.. unnamed.Select(d => new RefdesCandidate(d.Body))]);
            // The same designator printed twice names the part nearer its print.
            var order = Enumerable.Range(0, claims.Count).Where(i => assigned[i] >= 0)
                                  .OrderBy(i => RefdesAssociation.Distance(claims[i].X, claims[i].Y, unnamed[assigned[i]].Body));
            var named = new HashSet<int>();
            foreach (int i in order)
            {
                if (byRefdes.ContainsKey(claims[i].Refdes)) continue;
                var hit = unnamed[assigned[i]];
                hit.Refdes = claims[i].Refdes;
                hit.RefdesSource = claims[i].Source;
                hit.Silk = claims[i].Line;
                byRefdes[claims[i].Refdes] = hit;
                named.Add(i);
            }
            unassociated.AddRange(claims.Where((c, i) => !named.Contains(i) && !byRefdes.ContainsKey(c.Refdes) && InScope(ctx, c.X, c.Y)));
        }

        // ── 5. nothing: a generated designator, `_A` so it can never collide with the board's own ──
        int generated = 0;
        foreach (var d in drafts.Where(d => d.Refdes is null).OrderByDescending(d => d.Y).ThenBy(d => d.X).ToList())
        {
            d.Refdes = $"C_A{++generated}";
            d.RefdesSource = PartEvidenceSource.Generated;
        }

        report.Add(RecognitionFindingClass.PartsFromBodyOutlines, bodies.Count,
            $"{Plural(bodies.Count, "part was", "parts were")} read from {(bodies.Count == 1 ? "its body outline" : "their body outlines")} " +
            $"on the silkscreen, over a gap in the copper with no land of {(bodies.Count == 1 ? "its" : "their")} own: " +
            $"{string.Join(", ", bodies.Take(12).Select(d => d.Refdes))}{(bodies.Count > 12 ? ", …" : "")}.",
            [.. bodies.Select(d => new RecognitionAnchor(d.X, d.Y, d.Pads[0].Layer))]);
        report.Add(RecognitionFindingClass.BodyOutlinesOverManyPieces, overMany.Count,
            $"{Plural(overMany.Count, "body outline lies", "body outlines lie")} over more than two pieces of copper — a part of more " +
            $"than two terminals, or an outline that is not a part's body — and {(overMany.Count == 1 ? "was" : "were")} not read as " +
            $"{(overMany.Count == 1 ? "a part" : "parts")}: {string.Join(", ", overMany.Take(8).Select(b => fmt.Point((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2)))}" +
            $"{(overMany.Count > 8 ? ", …" : "")}.",
            [.. overMany.Select(b => new RecognitionAnchor((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2))]);

        // ── 2b. the bill of materials, by designator ─────────────────────────────────────────────
        var bom = input.Bom is { Refusal: null } table ? table : null;
        var resolver = new PartModelResolver(baseDir);
        var rows = new List<PartRow>();
        var wrongDimension = new List<RecognitionAnchor>();
        foreach (var d in drafts)
        {
            var bomRows = bom is not null && d.RefdesSource != PartEvidenceSource.Generated ? bom.RowsFor(d.Refdes!) : [];
            rows.Add(Build(ctx, d, bomRows, resolver, baseDir, wrongDimension, bomGiven: bom is not null));
        }
        rows.Sort((a, b) => PartsTable.NaturalCompare(a.Refdes, b.Refdes));

        // ── what the reading says about itself (R-as4-9) ─────────────────────────────────────────
        Report(ctx, rows, bom, byRefdes, placementMissed, wrongDimension, report);
        report.Add(RecognitionFindingClass.DeclaredRefdesDisagrees, placementRenamed.Count,
            $"{Plural(placementRenamed.Count, "placement row names", "placement rows name")} pads the artwork declares as another part, and " +
            $"the placement file's designator was taken: {string.Join(", ", placementRenamed.Take(12).Select(r => $"{r.Row.Refdes} (declared {r.Declared})"))}" +
            $"{(placementRenamed.Count > 12 ? ", …" : "")}.", [.. placementRenamed.Select(r => new RecognitionAnchor(r.Row.X, r.Row.Y))]);
        ReportSilkscreen(ctx, silk, typed, unassociated, report);
        return new PartsTable(rows, fmt, baseDir);
    }

    // ── one row ─────────────────────────────────────────────────────────────────────────────────────

    private static PartRow Build(PartReadingContext ctx, Draft d, IReadOnlyList<BomRow> bomRows,
                                 PartModelResolver resolver, string? baseDir, List<RecognitionAnchor> wrongDimension,
                                 bool bomGiven = false)
    {
        var evidence = new Dictionary<PartField, PartEvidenceSource> { [PartField.Refdes] = d.RefdesSource };
        var notes = new List<string>(d.Notes);
        int pads = d.Pads.Count;
        bool named = d.RefdesSource != PartEvidenceSource.Generated;

        // ── kind: refdes prefix, placed PartKind, bill-of-materials words ─────────────────────────
        PartKind? kind = null;
        if (named && KindFromRefdes(d.Refdes!, pads, notes) is { } byPrefix) { kind = byPrefix; evidence[PartField.Kind] = PartEvidenceSource.Refdes; }
        // The schematic a placed part is linked to states its kind outright; the designator only what is usual.
        if (FromSymbol(d.LinkedKind) is { } linkedKind)
        {
            if (kind is { } prefixKind && prefixKind != linkedKind && kind is PartKind.R or PartKind.L or PartKind.C)
                notes.Add($"its designator says {PartsTable.KindText(prefixKind)}; the linked schematic says {PartsTable.KindText(linkedKind)}, which was taken");
            kind = linkedKind;
            evidence[PartField.Kind] = PartEvidenceSource.Instance;
        }
        else if (FromSymbol(d.PlacedKind) is { } placedKind)
        {
            if (kind is null) { kind = placedKind; evidence[PartField.Kind] = PartEvidenceSource.Instance; }
            else if (kind != placedKind && kind is PartKind.R or PartKind.L or PartKind.C)
                notes.Add($"the placed part says {PartsTable.KindText(placedKind)}; its designator says {PartsTable.KindText(kind.Value)}");
        }
        // The bill of materials states what was FITTED, and a designator prefix only what is usual — so a kind the
        // bill of materials states beats the prefix (designer report, round 15: FB1, a ferrite bead listed as an R,
        // came out an L with its 0.01 Ω value refused). A placed part's own kind still beats both.
        if (kind is null || evidence[PartField.Kind] == PartEvidenceSource.Refdes)
            foreach (var row in bomRows)
                if (BomKind(row) is { } bomKind)
                {
                    if (kind is { } prefixKind && prefixKind != bomKind)
                        notes.Add($"its designator says {PartsTable.KindText(prefixKind)}; the bill of materials says {PartsTable.KindText(bomKind)}, which was taken");
                    kind = bomKind;
                    evidence[PartField.Kind] = PartEvidenceSource.Bom;
                    break;
                }
        if (pads > 2 && kind != PartKind.Connector)
        {
            kind = PartKind.MultiPin;
            evidence.TryAdd(PartField.Kind, d.PadSource);
        }
        kind ??= PartKind.Unknown;

        // ── case: instance, land pattern, placement, bill of materials ────────────────────────────
        var bomCase = bomRows.Select(r => CaseOfToken(r.Footprint) ?? SmtCaseTable.Find(r.Parsed.CaseCode)).FirstOrDefault(c => c is not null);
        SmtCase? smt = null;
        foreach (var (c, src) in new[]
                 {
                     (d.InstanceCase, PartEvidenceSource.Instance), (d.LandCase, PartEvidenceSource.LandPattern),
                     (d.PlacementCase, PartEvidenceSource.Placement), (bomCase, PartEvidenceSource.Bom),
                 })
        {
            if (c is null) continue;
            if (smt is null) { smt = c; evidence[PartField.Case] = src; }
            else if (!ReferenceEquals(smt, c))
                notes.Add($"the {PartEvidenceText.Of(src)} says case {c.Code}; {PartEvidenceText.Of(evidence[PartField.Case])} says {smt.Code}");
        }

        // ── not fitted, a jumper ──────────────────────────────────────────────────────────────────
        if (pads == 2 && bomRows.Any(NotFitted))
        {
            kind = PartKind.Open;
            evidence[PartField.Kind] = PartEvidenceSource.Bom;
        }
        // A bill of materials lists what is FITTED: a two-pad land pattern it does not list, and that no placed
        // part claims, is an empty footprint — do not populate (designer report, 2026-10-20: the open pads beside
        // the amplifier came out as a variable capacitor).
        if (bomGiven && pads == 2 && bomRows.Count == 0 && kind is PartKind.Unknown or PartKind.R or PartKind.L or PartKind.C
            && !(evidence.TryGetValue(PartField.Kind, out var kindFrom) && kindFrom == PartEvidenceSource.Instance))
        {
            kind = PartKind.Open;
            evidence[PartField.Kind] = PartEvidenceSource.Bom;
            notes.Add("the bill of materials does not list it, so it is read as not fitted");
        }
        if (smt?.Family == SmtCaseFamily.WireJumper && kind is PartKind.Unknown or PartKind.R)
        {
            kind = PartKind.Short;
            evidence[PartField.Kind] = evidence[PartField.Case];
        }

        // ── part number: one, or none when the rows disagree ──────────────────────────────────────
        var numbers = bomRows.Select(r => r.PartNumber).OfType<string>().Where(s => s.Trim().Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? pn = numbers.Count == 1 ? numbers[0].Trim() : null;
        if (numbers.Count > 1) notes.Add($"the bill of materials names {numbers.Count} part numbers ({string.Join(", ", numbers)}); none was taken");
        if (pn is not null) evidence[PartField.PartNumber] = PartEvidenceSource.Bom;

        // ── value: the value column, else the description; a wrong dimension is refused ───────────
        double? value = null;
        if (kind is PartKind.R or PartKind.L or PartKind.C)
        {
            var sym = ToSymbol(kind.Value);
            var dim = DimensionOf(kind.Value);
            var read = new List<double>();
            bool wrong = false;
            foreach (var row in bomRows)
            {
                if (ReadValue(row.Value, sym, false, dim, notes, ref wrong) is { } v) read.Add(v);
                else if (ReadValue(row.Description, sym, true, dim, notes, ref wrong) is { } v2) read.Add(v2);
            }
            if (wrong) wrongDimension.Add(new RecognitionAnchor(d.X, d.Y, d.Pads.FirstOrDefault().Layer));
            if (read.Count > 0)
            {
                if (read.All(v => Same(v, read[0]))) { value = read[0]; evidence[PartField.Value] = PartEvidenceSource.Bom; }
                else notes.Add("the bill-of-materials rows disagree about the value; none was taken");
            }
            // No bill of materials says: the linked schematic's own value, when it is a number of the right dimension
            // (an expression naming a variable is not read, and says so).
            else if (d.LinkedValue is { } text && FromSymbol(d.LinkedKind) == kind)
            {
                if (ReadLinkedValue(text, sym, dim) is { } si)
                {
                    value = si;
                    evidence[PartField.Value] = PartEvidenceSource.Instance;
                }
                else notes.Add($"the linked schematic's value '{text}' is not a number; none was taken");
            }
            if (kind == PartKind.R && value == 0)
            {
                kind = PartKind.Short;
                value = null;
                evidence[PartField.Kind] = evidence[PartField.Value];
                evidence.Remove(PartField.Value);
            }
        }

        // ── connection: measured off the AS-3 islands ────────────────────────────────────────────
        var (connection, terminals) = Connect(ctx, d);

        var row0 = new PartRow
        {
            Refdes = d.Refdes!, Kind = kind.Value, Connection = connection, Case = smt, Value = value,
            PartNumber = pn, X = d.X, Y = d.Y, PadCount = pads, Terminals = terminals, Silk = d.Silk,
        };

        // ── model ─────────────────────────────────────────────────────────────────────────────────
        var model = PartModelKind.Ideal;
        string? modelFile = null;
        if (row0.IsModelled && row0.TakesValue && pn is not null)
        {
            var choice = resolver.Resolve(pn);
            notes.AddRange(choice.Notes);
            if (choice is { Model: PartModelKind.SnP, FilePath: { } file, Source: { } src })
            {
                model = PartModelKind.SnP;
                modelFile = new PartsTable([], ctx.Format, baseDir).StoredPath(file);
                evidence[PartField.Model] = src;
            }
        }

        // ── confidence ────────────────────────────────────────────────────────────────────────────
        var confidence =
            d.PadSource is PartEvidenceSource.Instance or PartEvidenceSource.DeclaredPads || (bomRows.Count > 0 && d.Placed) ? PartConfidence.High
            : d.PadSource == PartEvidenceSource.LandPattern && !named && kind == PartKind.Unknown ? PartConfidence.Low
            : PartConfidence.Medium;

        return row0 with
        {
            Variable = row0.IsModelled && row0.TakesValue && value is null && model == PartModelKind.Ideal ? row0.DefaultVariable : null,
            Model = model, ModelFile = modelFile, Evidence = evidence, Confidence = confidence, Notes = notes,
        };
    }

    /// <summary>
    /// The kind a bill-of-materials row states: its type word, or — for a ferrite bead, which a bill of materials
    /// lists as whatever the designer models it as — the dimension of its value (0.01 Ω is an R, 600 nH an L).
    /// </summary>
    private static PartKind? BomKind(BomRow row)
    {
        if (FromSymbol(BomTablePaste.ReadTypeWord(row.Description).Kind) is { } stated) return stated;
        string words = new([.. (row.Description ?? "").Where(char.IsLetter).Select(char.ToLowerInvariant)]);
        bool bead = words.StartsWith("bead", StringComparison.Ordinal) || words.StartsWith("ferrite", StringComparison.Ordinal);
        if (!bead || !BomTablePaste.TryReadValue(row.Value, null, false, out _, out var dim)) return null;
        return dim switch
        {
            UnitDimension.Resistance => PartKind.R,
            UnitDimension.Inductance => PartKind.L,
            _ => null,
        };
    }

    /// <summary>A value of the part's own dimension, or null; a value in another dimension (a 10 nH on
    /// a C) is a note and is not used (R-as4-4).</summary>
    private static double? ReadValue(string? cell, SymbolKind sym, bool description, UnitDimension want,
                                     List<string> notes, ref bool wrong)
    {
        if (!BomTablePaste.TryReadValue(cell, sym, description, out double si, out var dim)) return null;
        if (dim == want) return si;
        notes.Add($"the bill of materials value '{cell!.Trim()}' is {Noun(dim)}, not {Noun(want)}; not used");
        wrong = true;
        return null;
    }

    private static string Noun(UnitDimension d) => d switch
    {
        UnitDimension.Capacitance => "a capacitance",
        UnitDimension.Inductance => "an inductance",
        UnitDimension.Resistance => "a resistance",
        _ => d.ToString().ToLowerInvariant(),
    };

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b));

    private static bool NotFitted(BomRow row)
    {
        if (BomTablePaste.IsNotFittedMarker(row.Value) || BomTablePaste.IsNotFittedMarker(row.Description)) return true;
        if (row.Value?.Trim() == "NF") return true;   // "nF" is a unit word; only the capital spelling is the marker
        foreach (string w in (row.Description ?? "").Split([' ', ',', ';', '/', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
            if (w is "DNP" or "DNF" or "DNI" or "NF") return true;
        string lower = (row.Description ?? "").ToLowerInvariant();
        return lower.Contains("not fitted", StringComparison.Ordinal) || lower.Contains("do not fit", StringComparison.Ordinal)
               || lower.Contains("do not populate", StringComparison.Ordinal);
    }

    /// <summary>A part's two ends — railRF's <see cref="RailPartDiscovery.SeriesTerminals"/> pairing —
    /// and what each lands on.</summary>
    private static (PartConnection, IReadOnlyList<PartTerminal>) Connect(PartReadingContext ctx, Draft d)
    {
        if (d.Pads.Count > 2) return (PartConnection.MultiPin, []);
        if (d.Pads.Count < 2) return (PartConnection.Unplaced, []);

        string key = d.Refdes!;
        var pads = d.Pads.Select(p => p with { Refdes = key }).ToList();
        if (RailPartDiscovery.SeriesTerminals(key, pads) is not { } ends) return (PartConnection.Unplaced, []);
        var (a, b) = ends;
        var ta = Site(ctx, a, pads);
        var tb = Site(ctx, b, pads);

        bool placedA = ta.Island >= 0 || ta.OnGround, placedB = tb.Island >= 0 || tb.OnGround;
        if (!placedA || !placedB) return (PartConnection.Unplaced, [ta, tb]);
        if (ta.OnGround && tb.OnGround) return (PartConnection.Shorted, [ta, tb]);
        if (ta.OnGround) return (PartConnection.Shunt, [tb, ta]);   // the signal end first
        if (tb.OnGround) return (PartConnection.Shunt, [ta, tb]);
        return (ta.Island == tb.Island ? PartConnection.Bridged : PartConnection.Series, [ta, tb]);
    }

    /// <summary>Where one terminal is and what it lands on: an island, ground copper, or nothing. The
    /// pad's own layer first, then every conductor in stackup order.</summary>
    private static PartTerminal Site(PartReadingContext ctx, RailPortAnchor anchor, List<PlacedPin> pads)
    {
        var pad = anchor.Point is { } pt
            ? pads.FirstOrDefault(p => p.X == pt.X && p.Y == pt.Y)
            : pads.FirstOrDefault(p => string.Equals(p.Pin, anchor.Pin, StringComparison.OrdinalIgnoreCase));
        long x = anchor.Point?.X ?? pad.X, y = anchor.Point?.Y ?? pad.Y;

        var board = ctx.Board;
        var layers = new List<LayerKey>();
        if (pad.Layer is { } own) layers.Add(own);
        layers.AddRange(Conductors.Of(ctx.Input.Technology!).SelectMany(c => c.DrawingLayers).Where(l => !layers.Contains(l)));
        foreach (var l in layers)
        {
            int piece = board.Copper.IndexAt(x, y, l);
            if (piece < 0) continue;
            int island = piece < board.IslandOfPiece.Length ? board.IslandOfPiece[piece] : -1;
            if (island < 0) return new PartTerminal(x, y, l, -1, OnGround: true);
            return new PartTerminal(x, y, l, island, board.Islands[island].Kind == IslandKind.PadGround);
        }
        return new PartTerminal(x, y, pad.Layer, -1, OnGround: false);
    }

    // ── kinds ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>R-as4-2's designator prefixes. A two-pad D, Q, U, Y, SW or TP is not an R, L or C and is
    /// left out with a note; with more pads it is a multi-pin part.</summary>
    internal static PartKind? KindFromRefdes(string refdes, int pads, List<string> notes)
    {
        string prefix = new string([.. refdes.TakeWhile(char.IsAsciiLetter)]).ToUpperInvariant();
        bool numbered = refdes.Length > prefix.Length && char.IsAsciiDigit(refdes[prefix.Length]);
        if (!numbered) return null;
        switch (prefix)
        {
            case "R": return PartKind.R;
            case "L" or "FB" or "FL": return PartKind.L;
            case "C": return PartKind.C;
            case "J" or "P" or "X": return PartKind.Connector;
            case "D" or "Q" or "U" or "IC" or "Y" or "SW" or "TP":
                if (pads > 2) return PartKind.MultiPin;
                notes.Add($"a two-pad {prefix} is not an R, L or C, so it is left out; set its kind to model it");
                return PartKind.Ignore;
            default: return null;
        }
    }

    /// <summary>
    /// The components of the schematic a layout's placed instances are linked to, by instance name: the cell's primary
    /// schematic, else the one beside the layout (Update Layout from Schematic's own pairing). Empty when there is none or
    /// it cannot be read; the instances then read as before, kind and value unknown.
    /// </summary>
    private static Dictionary<string, EditableComponent> LinkedComponents(string? clayPath)
    {
        var map = new Dictionary<string, EditableComponent>(StringComparer.OrdinalIgnoreCase);
        if (clayPath is null) return map;
        var sibling = CellFolder.SiblingView(Path.GetFullPath(clayPath), ViewType.Layout, ViewType.Schematic);
        string? path = sibling.CellDir is { } cell ? RecognitionTarget.PrimarySchematic(cell) : null;
        path ??= sibling.TargetPath;
        if (path is null || !File.Exists(path)) return map;
        try
        {
            foreach (var c in SchematicPersistence.LoadFromFile(path).model.Components)
                if (c.InstanceName is { Length: > 0 } name) map.TryAdd(name, c);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            map.Clear();
        }
        return map;
    }

    /// <summary>
    /// A linked component's value in base SI, read with the bill-of-materials reader: the expression and its unit as
    /// stored (<c>150 nH</c>), then run together (<c>70kΩ</c> — the prefix may sit in the expression), then the
    /// expression alone in the kind's dimension. Null when none reads as a number of <paramref name="dim"/>.
    /// </summary>
    private static double? ReadLinkedValue(string text, SymbolKind sym, UnitDimension dim)
    {
        int split = text.LastIndexOf(' ');
        string expr = split < 0 ? text : text[..split], unit = split < 0 ? "" : text[(split + 1)..];
        foreach (string spelling in new[] { text, expr + unit, expr })
            if (BomTablePaste.TryReadValue(spelling, sym, false, out double si, out var got) && got == dim) return si;
        return null;
    }

    /// <summary>An R, L or C component's value as text a bill of materials would hold (<c>150 nH</c>), or null.</summary>
    private static string? LinkedValueText(EditableComponent c)
    {
        string? name = c.Symbol switch
        {
            SymbolKind.Resistor => "R", SymbolKind.Inductor => "L", SymbolKind.Capacitor => "C", _ => null,
        };
        var p = name is null ? null : c.Parameters.FirstOrDefault(q => q.Name == name);
        return p is { Expression: { Length: > 0 } e } ? $"{e.Trim()} {p.Unit}".Trim() : null;
    }

    private static PartKind? FromSymbol(SymbolKind? kind) => kind switch
    {
        SymbolKind.Resistor => PartKind.R,
        SymbolKind.Inductor => PartKind.L,
        SymbolKind.Capacitor => PartKind.C,
        _ => null,
    };

    internal static SymbolKind ToSymbol(PartKind kind) => kind switch
    {
        PartKind.R => SymbolKind.Resistor,
        PartKind.L => SymbolKind.Inductor,
        _ => SymbolKind.Capacitor,
    };

    internal static UnitDimension DimensionOf(PartKind kind) => kind switch
    {
        PartKind.R => UnitDimension.Resistance,
        PartKind.L => UnitDimension.Inductance,
        PartKind.V => UnitDimension.Voltage,
        PartKind.I => UnitDimension.Current,
        PartKind.K => UnitDimension.None,
        _ => UnitDimension.Capacitance,
    };

    /// <summary>The case a placed land-pattern cell states: <c>smt-0402@N_…</c> exactly, else whatever
    /// unambiguous case code its name carries.</summary>
    private static SmtCase? CaseOfCell(string cellName)
    {
        if (cellName.StartsWith("smt-", StringComparison.OrdinalIgnoreCase))
        {
            int cut = cellName.LastIndexOf('_');
            string id = "smt:" + (cut > 4 ? cellName[4..cut] : cellName[4..]);
            if (FootprintRef.TryParse(id, out var reference, out _)) return reference!.Case;
        }
        return CaseOfToken(cellName);
    }

    private static SmtCase? CaseOfToken(string? token) =>
        FootprintTokens.Match(token) is { Outcome: FootprintTokenOutcome.Matched, Case: { } c } ? c : null;

    // ── body outlines over a gap (3½) ─────────────────────────────────────────────────────────────

    /// <summary>A body is at least this many times as long as it is wide, so its two ends are known.</summary>
    public const double MinBodyAspect = 1.2;

    /// <summary>Each half of a body has copper under at least this share of it: a part's end sits on its copper.</summary>
    public const double MinEndCover = 0.25;

    private const string BodyOutlineNote = "read from its body outline over a gap in the copper; it has no land of its own";

    /// <summary>
    /// R-as4-1 (3½): the parts whose body outline straddles a gap in the copper. A body is a closed, near-rectangular
    /// outline (<see cref="MinRectangularity"/>) on a silkscreen or assembly layer, longer than it is wide
    /// (<see cref="MinBodyAspect"/>) and no longer than a land pattern spans; drawn as one shape, a closed stroke, or lines
    /// of no width that chain into one loop. Its two halves along its length each have copper of its side under a quarter
    /// of them at least (<see cref="MinEndCover"/>), and the copper with the most of each half under it is a different piece
    /// at either end: a series part across a gap cut in a line, or a shunt part
    /// from a line's edge to a pour. A terminal is the middle of the copper under its half. Below land patterns: an outline
    /// round pads another rung found makes nothing, and one over a third piece of copper is a part of more terminals and is said
    /// instead. Kind and value come from the designator and the bill of materials, as
    /// for any rung; the pads are only where the drawing says the part is, so it is a medium confidence reading.
    /// </summary>
    private static List<Draft> BodyOutlineParts(PartReadingContext ctx, List<Draft> found, out List<Bbox> overMany)
    {
        var tech = ctx.Input.Technology!;
        var conductors = Conductors.Of(tech);
        var parts = new List<Draft>();
        overMany = [];
        if (conductors.Count == 0) return parts;
        int dbu = ctx.Input.View.DbuPerMicron;
        long maxLong = (long)(LandPatternMatch.References.MaxSpanUm * dbu), minShort = 100L * dbu;

        var layers = SilkscreenText.Layers(tech).ToList();
        foreach (var l in tech.Layers)
        {
            string alias = l.Interchange?.PcbLayerName ?? "", name = (l.Name ?? "").ToLowerInvariant();
            if ((alias.EndsWith(".Fab", StringComparison.OrdinalIgnoreCase) || name.Contains("assembly")) && layers.All(x => x.Layer != l.Key))
                layers.Add((l.Key, alias.StartsWith("B.", StringComparison.OrdinalIgnoreCase) || name.Contains("bottom") || name.Contains("bot")));
        }

        foreach (var (layer, bottom) in layers)
        {
            var side = (bottom ? conductors[^1] : conductors[0]).DrawingLayers.ToHashSet();
            var rings = new List<long[]>();
            var open = new List<long[]>();
            foreach (var shape in ctx.Input.Shapes)
            {
                if (shape.Layer != layer) continue;
                long tol = LayoutFlattener.ResolveTolDbu(shape, tech);
                switch (shape)
                {
                    case RectShape r: rings.Add([r.X1, r.Y1, r.X2, r.Y1, r.X2, r.Y2, r.X1, r.Y2]); break;
                    case PolygonShape { Holes: null or { Count: 0 } } or CurveShape { Holes: null or { Count: 0 } }:
                        rings.AddRange(LayoutFlattener.Flatten(shape, tol)); break;
                    // A closed stroke is an outline; open ones chain only when they are a drawing's lines (no width): a
                    // board's stroked legend is letters, and a D or a 0 chained from its strokes is near enough a box.
                    case PathShape path:
                        var xy = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, tol);
                        if (xy.Length >= 8 && xy[0] == xy[^2] && xy[1] == xy[^1]) rings.Add(xy[..^2]);
                        else if (path.Width <= 0) open.Add(xy);
                        break;
                }
            }
            // Lines a loop is chained from make a body only as a rectangle — four corners filling their box — so a legend
            // exploded into lines, whose 0s and Ds chain into loops too, makes none.
            if (open.Count > 0) rings.AddRange(DxfCopperLoops.Chain(open, [], Math.Max(1, dbu)).Loops.Where(IsRectangle));

            foreach (var ring in rings)
            {
                var box = BoundsOf(Clipper.MakePath(ring));
                long w = box.MaxX - box.MinX, h = box.MaxY - box.MinY, lo = Math.Min(w, h), hi = Math.Max(w, h);
                if (lo < minShort || hi > maxLong || hi < MinBodyAspect * lo) continue;
                if (Math.Abs(Clipper.Area(Clipper.MakePath(ring))) < MinRectangularity * w * h) continue;
                long cx = (box.MinX + box.MaxX) / 2, cy = (box.MinY + box.MaxY) / 2;
                if (!InScope(ctx, cx, cy)) continue;
                // An outline round a part another rung found — its pads under it, or its pads' box meeting it.
                if (found.Concat(parts).Any(d => d.Body.Intersects(box) || d.Pads.Any(p => box.Contains(p.X, p.Y)))) continue;

                bool alongX = w >= h;
                var halves = alongX
                    ? new[] { new Bbox(box.MinX, box.MinY, cx, box.MaxY), new Bbox(cx, box.MinY, box.MaxX, box.MaxY) }
                    : new[] { new Bbox(box.MinX, box.MinY, box.MaxX, cy), new Bbox(box.MinX, cy, box.MaxX, box.MaxY) };
                var ends = halves.Select(half => CopperUnder(ctx.Copper, half, side)).ToArray();
                if (ends[0] is not { } a || ends[1] is not { } b || a.Piece == b.Piece) continue;
                double halfArea = 0.5 * (double)w * h;
                if (a.Area < MinEndCover * halfArea || b.Area < MinEndCover * halfArea) continue;
                // A third piece of copper under it as an end sits on its copper — under an end or the middle: the body of a part
                // of three terminals or more, which two ends would misread. A pour's sliver along the long edges is no terminal.
                var middle = alongX ? new Bbox(box.MinX + w / 4, box.MinY, box.MaxX - w / 4, box.MaxY)
                                    : new Bbox(box.MinX, box.MinY + h / 4, box.MaxX, box.MaxY - h / 4);
                if (PiecesUnder(ctx.Copper, [halves[0], halves[1], middle], MinEndCover * halfArea, side) > 2) { overMany.Add(box); continue; }

                var d = new Draft { PadSource = PartEvidenceSource.BodyOutline, Body = box };
                d.Pads.Add(new PlacedPin(null, "1", null, a.X, a.Y, PinSource.Artwork) { Layer = a.Layer });
                d.Pads.Add(new PlacedPin(null, "2", null, b.X, b.Y, PinSource.Artwork) { Layer = b.Layer });
                d.Notes.Add(BodyOutlineNote);
                parts.Add(d);
            }
        }
        return parts;
    }

    /// <summary>Four corners, filling their axis-aligned box: a rectangle drawn as lines.</summary>
    private static bool IsRectangle(long[] ring)
    {
        int n = ring.Length / 2, corners = 0;
        for (int i = 0; i < n; i++)
        {
            long ax = ring[2 * ((i + n - 1) % n)], ay = ring[2 * ((i + n - 1) % n) + 1], bx = ring[2 * i], by = ring[2 * i + 1];
            long cx = ring[2 * ((i + 1) % n)], cy = ring[2 * ((i + 1) % n) + 1];
            if ((double)(bx - ax) * (cy - by) - (double)(by - ay) * (cx - bx) != 0) corners++;
        }
        var box = BoundsOf(Clipper.MakePath(ring));
        return corners == 4 && Math.Abs(Clipper.Area(Clipper.MakePath(ring))) >= 0.98 * (box.MaxX - box.MinX) * (double)(box.MaxY - box.MinY);
    }

    /// <summary>How many pieces of copper on <paramref name="side"/> have <paramref name="least"/> or more under one of
    /// <paramref name="windows"/>.</summary>
    private static int PiecesUnder(BoardCopper copper, Bbox[] windows, double least, HashSet<LayerKey> side)
    {
        var paths = windows.Select(b => new Paths64 { Clipper.MakePath(new[] { b.MinX, b.MinY, b.MaxX, b.MinY, b.MaxX, b.MaxY, b.MinX, b.MaxY }) }).ToList();
        var all = windows.Aggregate(Bbox.Empty, (u, b) => u.Union(b));
        int count = 0;
        for (int p = 0; p < copper.Pieces.Count; p++)
            if (copper.IsConductor[p] && side.Contains(copper.Pieces.LayerOfPiece(p)) && copper.Pieces.BoundsOfPiece(p).Intersects(all)
                && paths.Any(w => Math.Abs(Clipper.Area(Clipper.Intersect(copper.Pieces.PathsOfPiece(p), w, LayoutClipper.Rule))) >= least))
                count++;
        return count;
    }

    /// <summary>The piece of copper on <paramref name="side"/> with the most area under <paramref name="half"/>, and a point
    /// on it there: the middle of that area's box, or one of its corners where the middle is off the copper; and that area.</summary>
    private static (int Piece, long X, long Y, LayerKey Layer, double Area)? CopperUnder(BoardCopper copper, Bbox half, HashSet<LayerKey> side)
    {
        var window = new Paths64 { Clipper.MakePath(new[] { half.MinX, half.MinY, half.MaxX, half.MinY, half.MaxX, half.MaxY, half.MinX, half.MaxY }) };
        (int Piece, long X, long Y, LayerKey Layer, double Area)? best = null;
        double most = 0;
        for (int p = 0; p < copper.Pieces.Count; p++)
        {
            var layer = copper.Pieces.LayerOfPiece(p);
            if (!copper.IsConductor[p] || !side.Contains(layer) || !copper.Pieces.BoundsOfPiece(p).Intersects(half)) continue;
            var under = Clipper.Intersect(copper.Pieces.PathsOfPiece(p), window, LayoutClipper.Rule);
            double area = Math.Abs(Clipper.Area(under));
            if (area <= most || under.Count == 0) continue;
            var box = Bbox.Empty;
            foreach (var path in under) box = box.Union(BoundsOf(path));
            long x = (box.MinX + box.MaxX) / 2, y = (box.MinY + box.MaxY) / 2;
            if (copper.Pieces.IndexAt(x, y, layer) != p) (x, y) = (under[0][0].X, under[0][0].Y);
            (best, most) = ((p, x, y, layer, area), area);
        }
        return best;
    }

    // ── pad outlines: mask or paste openings where the technology has them, else copper ────────────

    private static List<PadOutline> Outlines(PartReadingContext ctx, RecognitionReport report)
    {
        var tech = ctx.Input.Technology!;
        var conductors = Conductors.Of(tech);
        var outlines = new List<PadOutline>();
        if (conductors.Count == 0) return outlines;

        var fallback = new List<string>();
        foreach (bool top in new[] { true, false })
        {
            var side = (top ? conductors[0] : conductors[^1]).DrawingLayers.ToList();
            if (side.Count == 0 || (!top && conductors.Count == 1)) continue;
            bool sideHasSignal = ctx.Board.Islands.Any(i => i.Kind != IslandKind.PadGround && i.Layers.Any(side.Contains));
            if (!sideHasSignal) continue;

            var (mask, paste) = OpeningLayers(tech, top);
            var found = new List<PadOutline>();
            if (paste is { } pl) found = Openings(ctx, pl, PadOutlineSource.Paste, side);
            if (found.Count == 0 && mask is { } ml) found = Openings(ctx, ml, PadOutlineSource.Mask, side);
            if (mask is null && paste is null)
            {
                fallback.Add(top ? "top" : "bottom");
                found = CopperOutlines(ctx, side);
            }
            outlines.AddRange(found);
        }

        report.Add(RecognitionFindingClass.MaskPasteAbsent, fallback.Count,
            $"The technology has no solder-mask or paste layer on the {string.Join(" and ", fallback)} " +
            $"side{(fallback.Count == 1 ? "" : "s")}, so land patterns were read from the copper itself: pad-shaped " +
            "copper and pads at line ends. A pad joined to a pour is not seen this way.");
        return outlines;
    }

    /// <summary>A side's solder-mask and paste layers: the board-format alias, the purpose, then the name.</summary>
    private static (LayerKey? Mask, LayerKey? Paste) OpeningLayers(Technology tech, bool top)
    {
        LayerKey? mask = null, paste = null;
        foreach (var layer in tech.Layers)
        {
            string alias = layer.Interchange?.PcbLayerName ?? "";
            string name = (layer.Name ?? "").ToLowerInvariant();
            string purpose = (layer.Purpose ?? "").Trim().ToLowerInvariant();

            bool isPaste = alias.EndsWith(".Paste", StringComparison.OrdinalIgnoreCase) || purpose is "paste" or "solderpaste" || name.Contains("paste");
            bool isMask = !isPaste && (alias.EndsWith(".Mask", StringComparison.OrdinalIgnoreCase) || purpose == "soldermask"
                          || (name.Contains("mask") && !name.Contains("carbon") && !name.Contains("peel") && !name.Contains("heatsink")));
            if (!isPaste && !isMask) continue;

            bool front = alias.StartsWith("F.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("f.") || name.Contains("top") || name.Contains("front");
            bool back = alias.StartsWith("B.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("b.") || name.Contains("bottom")
                        || name.Contains("bot") || name.Contains("back");
            bool onSide = top ? front || !back : back && !front;
            if (!onSide) continue;
            if (isPaste) paste ??= layer.Key;
            else mask ??= layer.Key;
        }
        return (mask, paste);
    }

    /// <summary>The rectangular openings on <paramref name="layer"/> that have copper of the side under them.</summary>
    private static List<PadOutline> Openings(PartReadingContext ctx, LayerKey layer, PadOutlineSource source, List<LayerKey> side)
    {
        var tech = ctx.Input.Technology!;
        var paths = new Paths64();
        foreach (var shape in ctx.Shapes)
            if (shape.Layer == layer && shape is not (LabelShape or ViaShape or BitmapShape))
                paths.AddRange(LayoutClipper.ToClipperPaths(shape, LayoutFlattener.ResolveTolDbu(shape, tech)));
        if (paths.Count == 0) return [];

        long maxSize = (long)(LandPatternMatch.References.MaxSpanUm * ctx.Input.View.DbuPerMicron);
        var outlines = new List<PadOutline>();
        foreach (var path in Clipper.Union(paths, LayoutClipper.Rule))
        {
            double area = Clipper.Area(path);
            if (area <= 0) continue;   // a hole
            if (Rectangle(path, area, maxSize) is not { } box) continue;
            long cx = (box.MinX + box.MaxX) / 2, cy = (box.MinY + box.MaxY) / 2;
            foreach (var l in side)
                if (ctx.Board.Copper.IndexAt(cx, cy, l) >= 0) { outlines.Add(new PadOutline(box, l, source)); break; }
        }
        return outlines;
    }

    /// <summary>Pad-shaped copper — whole pieces by the trace review's own pad rule — and the copper at
    /// the end of every trace the review names as ending in a "pad".</summary>
    private static List<PadOutline> CopperOutlines(PartReadingContext ctx, List<LayerKey> side)
    {
        var copper = ctx.Copper;
        var board = ctx.Board;
        long maxSize = (long)(LandPatternMatch.References.MaxSpanUm * ctx.Input.View.DbuPerMicron);
        var outlines = new List<PadOutline>();

        for (int p = 0; p < copper.Pieces.Count; p++)
        {
            if (!copper.IsConductor[p] || !side.Contains(copper.Pieces.LayerOfPiece(p))) continue;
            if (p >= board.IslandOfPiece.Length || board.IslandOfPiece[p] < 0 || !copper.IsPadShaped(p)) continue;
            var ring = copper.Pieces.PathsOfPiece(p);
            if (ring.Count != 1 || Rectangle(ring[0], Math.Abs(Clipper.Area(ring[0])), maxSize) is not { } box) continue;
            outlines.Add(new PadOutline(box, copper.Pieces.LayerOfPiece(p), PadOutlineSource.Copper));
        }

        double maxAlong = LandPatternMatch.References.All.Max(r => r.AlongUm) * ctx.Input.View.DbuPerMicron;
        double maxAcross = LandPatternMatch.References.All.Max(r => r.AcrossUm) * ctx.Input.View.DbuPerMicron;
        foreach (var run in ctx.TraceRuns())
        {
            if (!side.Contains(run.Layer) || run.Pieces.Count == 0) continue;
            var first = run.Pieces[0];
            var last = run.Pieces[^1];
            if (run.StartsAt == "pad")
                EndPad(run.StartX, run.StartY, first.X0 - first.X1, first.Y0 - first.Y1, run.Layer);
            if (run.EndsAt == "pad")
                EndPad(run.EndX, run.EndY, last.X1 - last.X0, last.Y1 - last.Y0, run.Layer);
        }
        return outlines;

        // The copper ahead of a trace end, inside a window as long and as wide as the largest land: an
        // axis-aligned end only, because a land pattern is matched at 0° and 90°.
        void EndPad(long x, long y, double dx, double dy, LayerKey layer)
        {
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return;
            dx /= len; dy /= len;
            if (Math.Abs(dx) < 0.99 && Math.Abs(dy) < 0.99) return;
            if (outlines.Any(o => o.Layer == layer && o.Bounds.Contains(x + (long)(dx * 10), y + (long)(dy * 10)))) return;

            long fwd = (long)(1.3 * maxAlong), half = (long)(0.65 * maxAcross);
            long x0 = (long)Math.Min(x, x + dx * fwd), x1 = (long)Math.Max(x, x + dx * fwd);
            long y0 = (long)Math.Min(y, y + dy * fwd), y1 = (long)Math.Max(y, y + dy * fwd);
            if (Math.Abs(dx) >= 0.99) { y0 = y - half; y1 = y + half; } else { x0 = x - half; x1 = x + half; }

            long px = x + (long)(dx * Math.Max(1, ctx.Input.View.DbuPerMicron)), py = y + (long)(dy * Math.Max(1, ctx.Input.View.DbuPerMicron));
            int piece = copper.Pieces.IndexAt(px, py, layer);
            if (piece < 0) return;
            var window = new Paths64 { Clipper.MakePath(new[] { x0, y0, x1, y0, x1, y1, x0, y1 }) };
            var inside = Clipper.Intersect(copper.Pieces.PathsOfPiece(piece), window, LayoutClipper.Rule);
            foreach (var path in inside)
            {
                if (Clipper.PointInPolygon(new Point64(px, py), path) == PointInPolygonResult.IsOutside) continue;
                var box = BoundsOf(path);
                if (box.MaxX - box.MinX > 0 && box.MaxY - box.MinY > 0)
                    outlines.Add(new PadOutline(box, layer, PadOutlineSource.Copper));
                return;
            }
        }
    }

    /// <summary>The box of a path that is nearly a rectangle and no larger than any land, else null.</summary>
    private static Bbox? Rectangle(Path64 path, double area, long maxSize)
    {
        var box = BoundsOf(path);
        double w = box.MaxX - box.MinX, h = box.MaxY - box.MinY;
        if (w <= 0 || h <= 0 || w > maxSize || h > maxSize) return null;
        return area / (w * h) >= MinRectangularity ? box : null;
    }

    private static Bbox BoundsOf(Path64 path)
    {
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        foreach (var pt in path)
        {
            minX = Math.Min(minX, pt.X); maxX = Math.Max(maxX, pt.X);
            minY = Math.Min(minY, pt.Y); maxY = Math.Max(maxY, pt.Y);
        }
        return new Bbox(minX, minY, maxX, maxY);
    }

    private static Bbox Grow(Bbox b, long dx, long dy) => new(b.MinX - dx, b.MinY - dy, b.MaxX + dx, b.MaxY + dy);

    private static double Dist(long x0, long y0, long x1, long y1) => Math.Sqrt((double)(x1 - x0) * (x1 - x0) + (double)(y1 - y0) * (y1 - y0));

    private static bool InScope(PartReadingContext ctx, long x, long y) => ctx.Scope is not { } s || Regions.Contains(s, x, y);

    // ── the report ──────────────────────────────────────────────────────────────────────────────────

    private static void Report(PartReadingContext ctx, List<PartRow> rows, BomTable? bom, Dictionary<string, Draft> byRefdes,
                               List<PlacementRow> placementMissed, List<RecognitionAnchor> wrongDimension, RecognitionReport report)
    {
        var fmt = ctx.Format;
        static RecognitionAnchor A(PartRow r) => new(r.X, r.Y, r.Terminals.FirstOrDefault()?.Layer);
        string Names(IEnumerable<PartRow> these) =>
            string.Join(", ", these.Take(12).Select(r => r.Refdes)) + (these.Count() > 12 ? ", …" : "");
        void Add(RecognitionFindingClass cls, List<PartRow> these, string sentence) =>
            report.Add(cls, these.Count, sentence, [.. these.Select(A)]);

        var bySource = rows.GroupBy(r => r.Evidence[PartField.Refdes]).ToDictionary(g => g.Key, g => g.ToList());
        var instance = bySource.GetValueOrDefault(PartEvidenceSource.Instance) ?? [];
        Add(RecognitionFindingClass.PartsFromInstances, instance,
            $"{Plural(instance.Count, "part was", "parts were")} read from placed footprints: {Names(instance)}.");
        var placed = bySource.GetValueOrDefault(PartEvidenceSource.Placement) ?? [];
        Add(RecognitionFindingClass.PartsFromPlacement, placed,
            $"{Plural(placed.Count, "part was", "parts were")} named by the placement file on land patterns found on the board: {Names(placed)}.");
        var declared = bySource.GetValueOrDefault(PartEvidenceSource.DeclaredPads) ?? [];
        Add(RecognitionFindingClass.PartsFromDeclaredPads, declared,
            $"{Plural(declared.Count, "part", "parts")} from pad attributes declared in the artwork: {Names(declared)}.");
        var silk = bySource.GetValueOrDefault(PartEvidenceSource.Silkscreen) ?? [];
        Add(RecognitionFindingClass.PartsFromSilkscreen, silk,
            $"{Plural(silk.Count, "part was", "parts were")} named by the silkscreen: {Names(silk)}.");
        var land = bySource.GetValueOrDefault(PartEvidenceSource.Generated) ?? [];
        Add(RecognitionFindingClass.PartsFromLandPatternOnly, land,
            $"{Plural(land.Count, "part was", "parts were")} found from land patterns " +
            (land.Any(r => r.Notes.Any(n => n.StartsWith(BodyOutlineNote, StringComparison.Ordinal))) ? "or body outlines " : "") +
            $"alone and given generated designators: {Names(land)}.");

        var unknown = rows.Where(r => r.Kind == PartKind.Unknown && r.IsModelled).ToList();
        Add(RecognitionFindingClass.PartKindsUnknown, unknown,
            $"{Plural(unknown.Count, "part has", "parts have")} no known kind and will be generated as a capacitor: {Names(unknown)}.");
        var vars = rows.Where(r => r.Variable is not null).ToList();
        Add(RecognitionFindingClass.PartValuesUnknown, vars,
            $"{Plural(vars.Count, "part has no known value and becomes a variable", "parts have no known value and become variables")} " +
            $"with a transparent starting value: {string.Join(", ", vars.Take(12).Select(r => r.Variable))}{(vars.Count > 12 ? ", …" : "")}.");
        report.Add(RecognitionFindingClass.PartValueWrongDimension, wrongDimension.Count,
            $"{Plural(wrongDimension.Count, "bill-of-materials value is", "bill-of-materials values are")} in the wrong " +
            "dimension for the part's kind and were not used.", wrongDimension);
        var snp = rows.Where(r => r.Model == PartModelKind.SnP).ToList();
        Add(RecognitionFindingClass.PartModelsSnp, snp,
            $"{Plural(snp.Count, "part uses", "parts use")} a measured Touchstone model: " +
            string.Join(", ", snp.Take(12).Select(r => $"{r.Refdes} ({Path.GetFileName(r.ModelFile)})")) + ".");
        var ambiguous = rows.Where(r => r.Notes.Any(n => n.StartsWith("land pattern ", StringComparison.Ordinal))).ToList();
        Add(RecognitionFindingClass.LandPatternAmbiguous, ambiguous,
            $"{Plural(ambiguous.Count, "land pattern fits", "land patterns fit")} more than one case about as well: " +
            string.Join("; ", ambiguous.Take(8).Select(r => $"{r.Refdes} {r.Notes.First(n => n.StartsWith("land pattern ", StringComparison.Ordinal))[13..]}")) + ".");
        var shorted = rows.Where(r => r.Connection == PartConnection.Shorted).ToList();
        Add(RecognitionFindingClass.PartsShortedLeftOut, shorted,
            $"{Plural(shorted.Count, "part has", "parts have")} both pads on ground, does nothing and {(shorted.Count == 1 ? "was" : "were")} left out: {Names(shorted)}.");
        var bridged = rows.Where(r => r.Connection == PartConnection.Bridged).ToList();
        Add(RecognitionFindingClass.PartsBridgedLeftOut, bridged,
            $"{Plural(bridged.Count, "part has", "parts have")} copper running round {(bridged.Count == 1 ? "it" : "them")} — both pads " +
            $"on one island — and {(bridged.Count == 1 ? "was" : "were")} left out: {Names(bridged)}.");
        var unplaced = rows.Where(r => r.Connection == PartConnection.Unplaced).ToList();
        Add(RecognitionFindingClass.PartsOffCopper, unplaced,
            $"{Plural(unplaced.Count, "part has", "parts have")} a pad on no copper in scope and {(unplaced.Count == 1 ? "was" : "were")} left out: {Names(unplaced)}.");
        var multi = rows.Where(r => r.Kind is PartKind.MultiPin or PartKind.Connector).ToList();
        Add(RecognitionFindingClass.MultiPinPartsCut, multi,
            $"{Plural(multi.Count, "part has", "parts have")} more than two pads or {(multi.Count == 1 ? "is a connector" : "are connectors")} " +
            $"and {(multi.Count == 1 ? "was" : "were")} cut out; {(multi.Count == 1 ? "its" : "their")} pads on RF lines are ports: " +
            string.Join(", ", multi.Take(12).Select(r => r.PartNumber is { } pn ? $"{r.Refdes} ({pn})" : r.Refdes)) + ".");

        if (bom is not null)
        {
            // A designator the board has elsewhere (outside the selection) is not "not on the board".
            var elsewhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ctx.Scope is not null)
            {
                foreach (var i in ctx.Input.View.Instances) if (i.DisplayRefDes is { Length: > 0 } r) elsewhere.Add(r);
                if (ctx.Input.Placement is { Refusal: null } pt) foreach (var r in pt.Rows) elsewhere.Add(r.Refdes);
            }
            var missing = bom.Rows.Select(r => r.Refdes).Distinct(StringComparer.OrdinalIgnoreCase)
                             .Where(r => !byRefdes.ContainsKey(r) && !elsewhere.Contains(r))
                             .OrderBy(r => r, PartsTable.NaturalOrder).ToList();
            report.Add(RecognitionFindingClass.BomRowsNotOnBoard, missing.Count,
                $"{Plural(missing.Count, "bill-of-materials designator names", "bill-of-materials designators name")} no part found " +
                $"on the board: {string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? ", …" : "")}.");
        }
        report.Add(RecognitionFindingClass.PlacementRowsNotOnBoard, placementMissed.Count,
            $"{Plural(placementMissed.Count, "placement row lands", "placement rows land")} on no two-pad land pattern found on the " +
            $"board: {string.Join(", ", placementMissed.Take(12).Select(r => $"{r.Refdes} at {fmt.Point(r.X, r.Y)}"))}" +
            $"{(placementMissed.Count > 12 ? ", …" : "")}.",
            [.. placementMissed.Select(r => new RecognitionAnchor(r.X, r.Y))]);
    }

    /// <summary>R-as10-6: what the silkscreen read, what it named, what it could not place, what it left out.</summary>
    private static void ReportSilkscreen(PartReadingContext ctx, SilkscreenReading silk, TypedSilkscreenEvidence typed,
                                         List<PartClaim> unassociated, RecognitionReport report)
    {
        var fmt = ctx.Format;
        static RecognitionAnchor At(SilkTextLine l) => new(l.X, l.Y, l.Layer);
        // Said whether or not any part was left to name: with none found, it is what says the designators were there.
        var labels = typed.Lines.Where(l => InScope(ctx, l.X, l.Y)).ToList();
        int others = typed.OtherLabels.Count(l => InScope(ctx, l.X, l.Y));
        report.Add(RecognitionFindingClass.SilkscreenLabelsRead, labels.Count,
            $"{Plural(labels.Count, "designator was", "designators were")} read from text labels on the silkscreen: " +
            $"{string.Join(", ", labels.Take(16).Select(l => l.Refdes))}{(labels.Count > 16 ? ", …" : "")}" +
            (others > 0 ? $"; {Plural(others, "other label there is", "other labels there are")} no designator." : "."),
            [.. labels.Select(At)]);
        var lines = silk.Lines.Where(l => InScope(ctx, l.X, l.Y)).ToList();
        int designators = lines.Count(l => l.Refdes is not null);
        report.Add(RecognitionFindingClass.SilkscreenTextRead, lines.Count,
            $"{Plural(lines.Count, "line of text was", "lines of text were")} read on the silkscreen, {designators} of them " +
            $"designator{(designators == 1 ? "" : "s")}: {string.Join(", ", lines.Take(16).Select(l => l.Refdes ?? $"'{l.Text}'"))}" +
            $"{(lines.Count > 16 ? ", …" : "")}.", [.. lines.Select(At)]);
        report.Add(RecognitionFindingClass.SilkscreenRefdesNotAssociated, unassociated.Count,
            $"{Plural(unassociated.Count, "designator on the silkscreen names", "designators on the silkscreen name")} no part " +
            $"found within {RefdesAssociation.ReachInBodyDiagonals:0} body diagonals, and {(unassociated.Count == 1 ? "was" : "were")} " +
            $"not used: {string.Join(", ", unassociated.Take(12).Select(c => $"{c.Refdes} at {fmt.Point(c.X, c.Y)}"))}" +
            $"{(unassociated.Count > 12 ? ", …" : "")}.", [.. unassociated.Select(c => new RecognitionAnchor(c.X, c.Y, c.Line?.Layer))]);
        var unsure = lines.Where(l => StrokeGlyphs.LowMarginGlyphs(l) > 0).ToList();
        int glyphs = unsure.Sum(StrokeGlyphs.LowMarginGlyphs);
        report.Add(RecognitionFindingClass.SilkscreenGlyphsUncertain, glyphs,
            $"{Plural(glyphs, "silkscreen glyph fits", "silkscreen glyphs fit")} two characters about as well, so " +
            $"{(unsure.Count == 1 ? "a line that looks like a designator was" : "lines that look like designators were")} not read as one: " +
            $"{string.Join(", ", unsure.Take(12).Select(l => $"'{l.Text}'"))}{(unsure.Count > 12 ? ", …" : "")}. " +
            "Correct one in the parts table and learn its glyphs.", [.. unsure.Select(At)]);
        report.Add(RecognitionFindingClass.SilkscreenStrokesExcluded, silk.ExcludedStrokes,
            $"{Plural(silk.ExcludedStrokes, "silkscreen stroke is", "silkscreen strokes are")} not text — outlines, logos and " +
            "marks — and " + (silk.ExcludedStrokes == 1 ? "was" : "were") + " left out.");
        report.Add(RecognitionFindingClass.SilkscreenFilledNotRead, silk.FilledShapes,
            $"{Plural(silk.FilledShapes, "silkscreen shape is", "silkscreen shapes are")} filled rather than stroked and " +
            $"{(silk.FilledShapes == 1 ? "was" : "were")} not read: text drawn as filled outlines is not read.");
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
