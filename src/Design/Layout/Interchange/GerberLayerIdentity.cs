// Which layer a Gerber file IS (docs/sonnet-briefs/brief-L4g-gerber-import-orchestration.md §2).
//
// NOTHING IN A GERBER FILE RELIABLY SAYS WHICH LAYER IT IS. That is the whole difficulty of importing
// a set, and R-L4g-5 answers it with a RANKED CASCADE rather than a guess — four sources, strongest
// first, and the rung that settled each file is reported so a confident answer and a plausible one are
// never presented as the same thing:
//
//   0. the .gbrjob job file      — settles set membership and identity together
//   1. %TF.FileFunction (X2)     — the file's own statement of what it is; what L4c's writer emits
//   2. a .ctech GerberSuffix     — ours too, and it closes the loop against a technology already held
//   3. a generic name/extension heuristic — the one that can be CONFIDENTLY WRONG, so it is flagged
//   4. the shared layer-mapping dialog for whatever is left (GerberImport, not this file)
//
// The rung-3 table is DATA, not a chain of ifs, and it is GENERIC: patterns for what a layer IS, never
// a table keyed to a particular tool's or vendor's private naming, which root CLAUDE.md
// §"Commercial Vendor References" forbids outright.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>Which rung of R-L4g-5's cascade identified a file. Ordered strongest first, so a
/// comparison reads as "at least as strong as".</summary>
public enum GerberLayerRung
{
    JobFile,
    FileFunction,
    TechnologySuffix,
    Heuristic,
    Unidentified,
}

/// <summary>A parsed <c>FileFunction</c> value — <c>Copper,L2,Inr,Signal</c>, <c>Soldermask,Top</c>,
/// <c>Profile,NP</c>. <see cref="CopperIndex"/> is the copper layer's POSITION IN THE STACK, which is
/// what R-L4g-10 depends on and the single most valuable thing this attribute carries.</summary>
public sealed record GerberFileFunction(string Kind, string? Side, int? CopperIndex, string Raw);

/// <summary>What one artwork (or drill) file was decided to be, and on what authority.</summary>
public sealed record GerberLayerIdentity(
    string FilePath,
    string Extension,
    GerberLayerRung Rung,
    string LayerName,
    string? FileFunction,
    string? Purpose,
    string? Side,
    int? CopperIndex,
    LayerKey? DestLayer)
{
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>R-L4g-5's flag: rung 3 is the one that can be confidently wrong, so anything identified
    /// there is reported AS A GUESS by name.</summary>
    public bool IsGuess => Rung == GerberLayerRung.Heuristic;

    public bool IsConductor => string.Equals(Purpose, GerberLayerCascade.ConductorPurpose, StringComparison.Ordinal);
}

public static class GerberLayerCascade
{
    public const string ConductorPurpose = "conductor";
    public const string DrillPurpose = "drill";

    /// <summary>The name a layer nothing identified gets — the dialog's row, and the name the user sees
    /// when they have to answer for it.</summary>
    public const string UnidentifiedPurpose = "drawing";

    // ── The cascade ───────────────────────────────────────────────────────────

    /// <summary>
    /// Identifies one artwork file. <paramref name="jobFunction"/> is the <c>FileFunction</c> the set's
    /// job file gave for this path (rung 0), or null; <paramref name="read"/> supplies the file's own
    /// X2 attributes (rung 1); <paramref name="destTech"/> supplies the <c>GerberSuffix</c> aliases
    /// (rung 2). Everything else falls to the generic table (rung 3) and then to nothing at all.
    /// </summary>
    public static GerberLayerIdentity Identify(
        string filePath, GerberReadResult? read, string? jobFunction, Technology? destTech)
    {
        string extension = Path.GetExtension(filePath).TrimStart('.');

        // Rung 0 — the job file. It is the only source that can say a file belongs to this board at
        // all, so it outranks the file's own statement about itself.
        if (ParseFileFunction(jobFunction) is { } fromJob)
            return FromFunction(filePath, extension, GerberLayerRung.JobFile, fromJob, destTech);

        // Rung 1 — %TF.FileFunction. An explicit statement of what the file is, and what L4c's own
        // writer emits, so a file circuitRF produced is identified exactly with no heuristic involved.
        if (ParseFileFunction(read?.FileFunction) is { } fromX2)
            return FromFunction(filePath, extension, GerberLayerRung.FileFunction, fromX2, destTech);

        // Rung 2 — a .ctech layer whose GerberSuffix matches this file's extension. Export wrote
        // "<cell>.<GerberSuffix>", so import reads the suffix back to the same layer: the loop closes
        // against a technology the user already has.
        if (SuffixOwner(destTech, extension) is { } owner)
            return new GerberLayerIdentity(
                filePath, extension, GerberLayerRung.TechnologySuffix, owner.Name,
                owner.Interchange?.GerberFileFunction, PurposeOf(destTech, owner),
                SideOfName(owner.Name), CopperIndexOf(destTech, owner), owner.Key);

        // Rung 3 — the generic table. Flagged as a guess wherever it is reported.
        if (Heuristic(filePath) is { } guess)
            return guess with { DestLayer = NameOwner(destTech, guess.LayerName)?.Key };

        // Rung 4 — nothing identified it. The file's own base name is the only thing left to call it,
        // and GerberImport hands the row to the shared layer-mapping dialog.
        return new GerberLayerIdentity(
            filePath, extension, GerberLayerRung.Unidentified,
            Path.GetFileNameWithoutExtension(filePath) is { Length: > 0 } stem ? stem : "Unidentified",
            null, UnidentifiedPurpose, null, null, null);
    }

    /// <summary>The drill layer minted for one drill file. Drill data is never in doubt about what it
    /// is — the classification already settled that by content — so this is a naming decision, not a
    /// cascade. Rung 2 still applies: a technology that already declares a drill layer by suffix
    /// donates it, which is what keeps a re-import landing on the same layer as the first.</summary>
    public static GerberLayerIdentity IdentifyDrill(
        string filePath, string? fileFunction, Technology? destTech, string layerName)
    {
        string extension = Path.GetExtension(filePath).TrimStart('.');

        if (SuffixOwner(destTech, extension) is { } owner)
            return new GerberLayerIdentity(
                filePath, extension, GerberLayerRung.TechnologySuffix, owner.Name,
                fileFunction, DrillPurpose, null, null, owner.Key);

        return new GerberLayerIdentity(
            filePath, extension,
            fileFunction is { Length: > 0 } ? GerberLayerRung.FileFunction : GerberLayerRung.Heuristic,
            layerName, fileFunction, DrillPurpose, null, null,
            NameOwner(destTech, layerName)?.Key);
    }

    // ── FileFunction (rungs 0 and 1) ──────────────────────────────────────────

    /// <summary>Parses a <c>FileFunction</c> value into its kind, side and copper index. Returns null
    /// for null, blank, or a value whose first word means nothing here — an unknown function is NOT an
    /// identification, and pretending otherwise would put artwork on a layer named after a word nobody
    /// recognized.</summary>
    public static GerberFileFunction? ParseFileFunction(string? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        var fields = raw.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length == 0 || fields[0].Length == 0) return null;

        string kind = fields[0];
        string? side = null;
        int? copperIndex = null;

        if (kind.Equals("Copper", StringComparison.OrdinalIgnoreCase))
        {
            // Copper,L<n>,<Top|Inr|Bot>[,<Signal|Plane|Mixed|Hatched>]
            if (fields.Length > 1 && fields[1].StartsWith('L') &&
                int.TryParse(fields[1][1..], out int n) && n >= 1) copperIndex = n;
            if (fields.Length > 2) side = NormalizeSide(fields[2]);
        }
        else if (kind.Equals("Plated", StringComparison.OrdinalIgnoreCase) ||
                 kind.Equals("NonPlated", StringComparison.OrdinalIgnoreCase))
        {
            // Plated,<from>,<to>,<PTH|Blind|Buried> — a drill file's own function.
        }
        else if (fields.Length > 1)
        {
            side = NormalizeSide(fields[1]);
        }

        if (!IsKnownKind(kind)) return null;
        return new GerberFileFunction(kind, side, copperIndex, raw);
    }

    private static bool IsKnownKind(string kind) => KindNames.ContainsKey(kind);

    private static string? NormalizeSide(string field) => field.ToLowerInvariant() switch
    {
        "top" => "Top",
        "bot" or "bottom" => "Bot",
        "inr" or "inner" => "Inr",
        _ => null,
    };

    /// <summary>Every <c>FileFunction</c> first word this import understands, and the circuitRF layer
    /// name it becomes when the function carries no side. Names deliberately match the shipped starter
    /// technologies' own layer names, so a set imported next to one of those reconciles by NAME rather
    /// than minting a near-duplicate.</summary>
    private static readonly Dictionary<string, string> KindNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Copper"] = "Copper",
        ["Soldermask"] = "Soldermask",
        ["Legend"] = "Silk",
        ["Paste"] = "Paste",
        ["Glue"] = "Glue",
        ["Profile"] = "Outline",
        ["Plated"] = "Drill",
        ["NonPlated"] = "Drill",
        ["Drillmap"] = "Drill Map",
        ["Pads"] = "Pads",
        ["Component"] = "Component",
        ["Carbonmask"] = "Carbon Mask",
        ["Peelablesoldermask"] = "Peelable Mask",
        ["Viafill"] = "Via Fill",
        ["Heatsinkmask"] = "Heatsink Mask",
        ["Depthrout"] = "Depth Rout",
        ["Vcut"] = "V-Cut",
        ["Vcutmap"] = "V-Cut Map",
        ["AssemblyDrawing"] = "Assembly",
        ["ArrayDrawing"] = "Array Drawing",
        ["FabricationDrawing"] = "Fabrication",
        ["Drawing"] = "Drawing",
        ["Other"] = "Other",
    };

    private static GerberLayerIdentity FromFunction(
        string filePath, string extension, GerberLayerRung rung, GerberFileFunction fn, Technology? destTech)
    {
        string name = NameFor(fn);
        string? purpose =
            fn.Kind.Equals("Copper", StringComparison.OrdinalIgnoreCase) ? ConductorPurpose :
            fn.Kind.Equals("Plated", StringComparison.OrdinalIgnoreCase) ||
            fn.Kind.Equals("NonPlated", StringComparison.OrdinalIgnoreCase) ? DrillPurpose :
            UnidentifiedPurpose;

        // A destination layer that already declares this exact file function donates its key and name.
        // The IDENTITY still came from the file — the rung is unchanged — but reusing the existing
        // layer is what keeps a round trip landing back where it started.
        var owner = FunctionOwner(destTech, fn.Raw) ?? NameOwner(destTech, name) ?? SuffixOwner(destTech, extension);

        // The purpose comes from the FUNCTION, never from the donor layer: the shipped starter
        // technologies mark every layer "drawing", and letting that overwrite a declared Copper
        // function would silently drop the layer out of the stackup and out of the copper order.
        return new GerberLayerIdentity(
            filePath, extension, rung, owner?.Name ?? name, fn.Raw,
            purpose == UnidentifiedPurpose && owner is not null ? PurposeOf(destTech, owner) : purpose,
            fn.Side, fn.CopperIndex, owner?.Key);
    }

    /// <summary>The circuitRF layer name a parsed function becomes. Copper is named by its stack
    /// position when the function states one — <c>Copper,L2,Inr</c> is "Inner 1", the same spelling the
    /// shipped four-layer starter technology uses.</summary>
    public static string NameFor(GerberFileFunction fn)
    {
        string baseName = KindNames.TryGetValue(fn.Kind, out var known) ? known : fn.Kind;

        if (fn.Kind.Equals("Copper", StringComparison.OrdinalIgnoreCase))
            return fn.Side switch
            {
                "Top" => "Top Copper",
                "Bot" => "Bottom Copper",
                "Inr" => fn.CopperIndex is { } n && n >= 2 ? $"Inner {n - 1}" : "Inner Copper",
                _ => fn.CopperIndex is { } idx ? $"Copper L{idx}" : "Copper",
            };

        return fn.Side switch
        {
            "Top" => $"{baseName} Top",
            "Bot" => $"{baseName} Bottom",
            _ => baseName,
        };
    }

    // ── The generic table (rung 3) ────────────────────────────────────────────

    /// <summary>
    /// One row of R-L4g-5's rung-3 table: the word GROUPS that must all be satisfied by a file's name
    /// signature — one word from each group, so a row states "paste, on the bottom" once instead of
    /// once per spelling of "bottom" — what the layer then is, and which side it sits on.
    ///
    /// <para><b>Every row describes what a layer IS.</b> No row names a tool, a vendor or a product,
    /// and none may ever be added: this table is exactly the place a private naming convention would
    /// leak into the repo, and root <c>CLAUDE.md</c> §"Commercial Vendor References" forbids it
    /// outright.</para>
    /// </summary>
    /// <param name="IndexAfter">The word a stack NUMBER is read from — "inner 2", "layer 3". When it
    /// is set the row matches ONLY if a number actually follows that word, which is what keeps the
    /// broad rows below from claiming every file whose name happens to contain the word.</param>
    /// <param name="IndexOffset">Added to that number to get the inner-layer index, because the two
    /// conventions differ by exactly this: "inner 2" is already the second INNER layer, while "layer 2"
    /// counts the whole copper stack from the top and is therefore the FIRST inner layer.</param>
    private sealed record NamePattern(
        string[][] WordGroups, string LayerName, string Purpose, string? Side,
        string? IndexAfter = null, int IndexOffset = 0, int MinIndex = 1);

    // The side words, once. Grouping them is not tidying: they were written out row by row and "bot"
    // was missing from every row while "bottom", "back", "top" and "front" were present, so a set
    // spelling its sides the way the format's own FileFunction does ("Top"/"Bot") had its top layers
    // guessed and its bottom layers dropped to the mapping dialog — an asymmetry no one would choose
    // and nothing announced.
    private static readonly string[] TopWords = ["top", "front"];
    private static readonly string[] BotWords = ["bottom", "bot", "back"];

    // Ordered: the FIRST match wins, so the more specific rows come first. "solder mask top" must be
    // tested before "top", and "paste" before "mask", because a paste file's name commonly says both.
    private static readonly NamePattern[] Patterns =
    [
        // A drill DRAWING is a dimensioned fabrication sheet with a tool legend beside the board, not
        // drill data — so it must not land on the layer the actual hits go to, where its legend table
        // extends the drill layer far outside the board outline. Ahead of the plain "drill" row.
        new([["drill"], ["drawing", "map", "legend", "chart"]], "Drill Map", UnidentifiedPurpose, null),
        new([["drill"]], "Drill", DrillPurpose, null),
        new([["paste"], TopWords], "Paste Top", UnidentifiedPurpose, "Top"),
        new([["paste"], BotWords], "Paste Bottom", UnidentifiedPurpose, "Bot"),
        new([["mask"], TopWords], "Soldermask Top", UnidentifiedPurpose, "Top"),
        new([["mask"], BotWords], "Soldermask Bottom", UnidentifiedPurpose, "Bot"),
        new([["silk", "legend"], TopWords], "Silk Top", UnidentifiedPurpose, "Top"),
        new([["silk", "legend"], BotWords], "Silk Bottom", UnidentifiedPurpose, "Bot"),
        new([["outline"]], "Outline", UnidentifiedPurpose, null),
        new([["profile"]], "Outline", UnidentifiedPurpose, null),
        new([["keepout"]], "Outline", UnidentifiedPurpose, null),
        new([["edge"], ["cut", "cuts"]], "Outline", UnidentifiedPurpose, null),
        new([["board"], ["shape"]], "Outline", UnidentifiedPurpose, null),
        new([["mechanical"]], "Mechanical", UnidentifiedPurpose, null),
        new([["copper"], TopWords], "Top Copper", ConductorPurpose, "Top"),
        new([["copper"], BotWords], "Bottom Copper", ConductorPurpose, "Bot"),
        new([TopWords, ["layer"]], "Top Copper", ConductorPurpose, "Top"),
        new([BotWords, ["layer"]], "Bottom Copper", ConductorPurpose, "Bot"),
        new([["copper"], ["inner"]], "Inner Copper", ConductorPurpose, "Inr"),
        new([["inner"], ["layer"]], "Inner Copper", ConductorPurpose, "Inr"),

        // A NUMBERED MID LAYER — "layer 2", "layer 3" — is copper, and without this row a six-layer
        // board imports as two copper layers and four unidentified drawing layers, which is not a
        // labelling nuisance: only conductors enter the stackup and the copper order, so four sixths
        // of the board silently leaves the part of the import the EM path reads.
        //
        // The row is LAST so every function row above it wins first (a "soldermask top" file says both
        // "mask" and, often, "layer"), and it matches only when a NUMBER follows the word, so a name
        // that merely contains "layer" is not read as copper. A set that numbers its mid layers this
        // way names its outer ones "top layer" / "bottom layer" — both matched above — so "layer 2" is
        // the second layer of the whole stack and hence the FIRST inner one, which is the -1.
        new([["layer"]], "Inner Copper", ConductorPurpose, "Inr", IndexAfter: "layer", IndexOffset: -1, MinIndex: 2),
    ];

    private static GerberLayerIdentity? Heuristic(string filePath)
    {
        string extension = Path.GetExtension(filePath).TrimStart('.');
        string signature = Signature(Path.GetFileNameWithoutExtension(filePath) + " " + extension);

        foreach (var pattern in Patterns)
        {
            if (!pattern.WordGroups.All(g => g.Any(w => ContainsWord(signature, w)))) continue;

            int? index = pattern.Side == "Inr" ? NumberAfter(signature, pattern.IndexAfter ?? "inner") : null;
            // A row that reads its index from the name matches ONLY when the name states one, at or
            // above the row's own floor.
            if (pattern.IndexAfter is not null)
            {
                if (index is not { } stated || stated < pattern.MinIndex) continue;
                index = stated + pattern.IndexOffset;
            }

            string name = index is { } n && n >= 1 ? $"Inner {n}" : pattern.LayerName;
            return new GerberLayerIdentity(
                filePath, extension, GerberLayerRung.Heuristic, name, null,
                pattern.Purpose, pattern.Side, null, null);
        }

        return ExtensionFamily(filePath, extension);
    }

    /// <summary>
    /// The conventional extension family, read STRUCTURALLY rather than as a lookup of names: an
    /// extension of the shape <c>g&lt;side&gt;&lt;function&gt;</c> spells out its own meaning —
    /// <c>t</c>/<c>b</c> for the side the artwork is on, then <c>l</c> for the copper layer, <c>s</c>
    /// for the solder mask, <c>o</c> for the silkscreen overlay and <c>p</c> for the paste stencil.
    /// <c>g</c> followed by digits is inner copper by number, and <c>gko</c>/<c>gm&lt;n&gt;</c> are the
    /// keep-out and mechanical drawings.
    ///
    /// <para>This is decomposition, not a vendor table: no tool or product is named, and the rule
    /// generalizes to extensions nobody has seen rather than enumerating ones somebody has.</para>
    /// </summary>
    private static GerberLayerIdentity? ExtensionFamily(string filePath, string extension)
    {
        string ext = extension.ToLowerInvariant();
        if (ext.Length < 2 || ext[0] != 'g') return null;

        GerberLayerIdentity Make(string name, string purpose, string? side) =>
            new(filePath, extension, GerberLayerRung.Heuristic, name, null, purpose, side, null, null);

        if (ext == "gko") return Make("Outline", UnidentifiedPurpose, null);
        if (ext.StartsWith("gm", StringComparison.Ordinal) && ext[2..].All(char.IsAsciiDigit))
            return Make("Mechanical", UnidentifiedPurpose, null);

        // `g<n>` IS AN INNER COPPER LAYER, NUMBERED FROM 1 — never the top layer, whatever the digit.
        //
        // Two conventions for this extension are in circulation and they disagree by one: some sets
        // number the whole copper stack (`g1` = top) and some number only the mid layers (`g1` = the
        // first inner). The digit alone cannot separate them, so the choice is made on which mistake
        // is recoverable. Naming `g1` "Top Copper" is not: a set carrying `.g1` carries `.gtl` too —
        // `.gtl` is the one unambiguous spelling of top copper and every such set uses it — so the two
        // files collide on one layer name AND both rank as "Top", which puts the copper stack order
        // wrong (top, top, inner, bottom) in a way R-L4g-10 says must never happen silently. Reading
        // every `g<n>` as inner cannot collide with either outer layer, and orders `.g1`, `.g2`, `.g3`
        // correctly among themselves whichever convention wrote them.
        //
        // What it can still get wrong is the NUMBER on a set using the other convention (`.g2` shown
        // as "Inner 2" where the file meant the first inner layer). That is a label, it is reported as
        // a guess like every other rung-3 answer, and it is what a declared `%TF.FileFunction` or a
        // job file settles exactly — neither of which this rung ever runs against.
        if (ext.Length >= 2 && ext[1..].All(char.IsAsciiDigit) && int.TryParse(ext[1..], out int level) && level >= 1)
            return Make($"Inner {level}", ConductorPurpose, "Inr");

        if (ext.Length != 3) return null;
        string? side = ext[1] switch { 't' => "Top", 'b' => "Bot", _ => null };
        if (side is null) return null;
        bool top = side == "Top";

        return ext[2] switch
        {
            'l' => Make(top ? "Top Copper" : "Bottom Copper", ConductorPurpose, side),
            's' => Make(top ? "Soldermask Top" : "Soldermask Bottom", UnidentifiedPurpose, side),
            'o' => Make(top ? "Silk Top" : "Silk Bottom", UnidentifiedPurpose, side),
            'p' => Make(top ? "Paste Top" : "Paste Bottom", UnidentifiedPurpose, side),
            _ => null,
        };
    }

    /// <summary>Lower-cases and turns every non-alphanumeric run into a single space, so a name can be
    /// searched for WORDS rather than substrings — without this "topology" contains "top".</summary>
    private static string Signature(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 2);
        sb.Append(' ');
        bool lastWasSpace = true;
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) { sb.Append(c); lastWasSpace = false; }
            else if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
        }
        if (!lastWasSpace) sb.Append(' ');
        return sb.ToString();
    }

    private static bool ContainsWord(string signature, string word) =>
        signature.Contains(' ' + word + ' ', StringComparison.Ordinal) ||
        signature.Contains(word, StringComparison.Ordinal) && word.Length >= 4;

    /// <summary>The number that follows <paramref name="word"/> in a name signature — the 2 of
    /// "inner 2" or of "layer 2". A LETTER before the digits means the word was part of something else
    /// and there is no number to read.</summary>
    private static int? NumberAfter(string signature, string word)
    {
        int at = signature.IndexOf(word, StringComparison.Ordinal);
        if (at < 0) return null;
        int i = at + word.Length;
        while (i < signature.Length && !char.IsAsciiDigit(signature[i]))
        {
            if (char.IsAsciiLetter(signature[i])) return null;
            i++;
        }
        int start = i;
        while (i < signature.Length && char.IsAsciiDigit(signature[i])) i++;
        return i > start && int.TryParse(signature[start..i], out int n) && n >= 1 ? n : null;
    }

    // ── Destination-technology lookups ────────────────────────────────────────

    /// <summary>Rung 2's own test: a destination layer whose <c>InterchangeMapping.GerberSuffix</c> IS
    /// this file's extension.</summary>
    public static LayerDef? SuffixOwner(Technology? tech, string extension) =>
        extension.Length == 0 ? null : tech?.Layers.FirstOrDefault(l =>
            string.Equals(l.Interchange?.GerberSuffix, extension, StringComparison.OrdinalIgnoreCase));

    private static LayerDef? FunctionOwner(Technology? tech, string? fileFunction) =>
        fileFunction is not { Length: > 0 } ? null : tech?.Layers.FirstOrDefault(l =>
            string.Equals(l.Interchange?.GerberFileFunction, fileFunction, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a DESTINATION layer is for, when the layer table itself says only "drawing" —
    /// which every shipped starter technology does. The stackup is the authority: a layer named by a
    /// <see cref="StackupKind.Conductor"/> entry is copper and a layer named by a
    /// <see cref="StackupKind.Via"/> entry is a drill layer, whatever the layer table calls it.</summary>
    private static string PurposeOf(Technology? tech, LayerDef layer)
    {
        if (string.Equals(layer.Purpose, ConductorPurpose, StringComparison.Ordinal) ||
            string.Equals(layer.Purpose, DrillPurpose, StringComparison.Ordinal))
            return layer.Purpose!;

        if (ParseFileFunction(layer.Interchange?.GerberFileFunction) is { } fn)
        {
            if (fn.Kind.Equals("Copper", StringComparison.OrdinalIgnoreCase)) return ConductorPurpose;
            if (fn.Kind.Equals("Plated", StringComparison.OrdinalIgnoreCase) ||
                fn.Kind.Equals("NonPlated", StringComparison.OrdinalIgnoreCase)) return DrillPurpose;
        }

        if (tech is not null)
            foreach (var entry in tech.Stackup.Layers)
            {
                if (!entry.DrawingLayers.Contains(layer.Key)) continue;
                if (entry.Kind == StackupKind.Conductor) return ConductorPurpose;
                if (entry.Kind == StackupKind.Via) return DrillPurpose;
            }

        return UnidentifiedPurpose;
    }

    /// <summary>A rung-2 match's position in the destination technology's own conductor stack, which is
    /// a DECLARATION (the technology's stackup order) and not a guess — so a set identified entirely by
    /// suffix needs no ordering heuristic at all.</summary>
    private static int? CopperIndexOf(Technology? tech, LayerDef layer)
    {
        if (tech is null) return null;
        int rank = 0;
        foreach (var entry in tech.Stackup.Layers)
        {
            if (entry.Kind != StackupKind.Conductor) continue;
            rank++;
            if (entry.DrawingLayers.Contains(layer.Key)) return rank;
        }
        return null;
    }

    private static LayerDef? NameOwner(Technology? tech, string name) =>
        tech?.Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The side a technology layer's own NAME implies, for a rung-2 identification that has no
    /// file function to read it from — enough to order copper when the set declares no order at all.</summary>
    private static string? SideOfName(string name)
    {
        string signature = Signature(name);
        if (ContainsWord(signature, "top") || ContainsWord(signature, "front")) return "Top";
        if (ContainsWord(signature, "bottom") || ContainsWord(signature, "back")) return "Bot";
        if (ContainsWord(signature, "inner")) return "Inr";
        return null;
    }

    // ── Reporting ─────────────────────────────────────────────────────────────

    /// <summary>How a rung is named in the import summary. R-L4g-5's own requirement: report, per file,
    /// WHICH rung identified it, and flag rung 3 specifically.</summary>
    public static string Describe(GerberLayerRung rung) => rung switch
    {
        GerberLayerRung.JobFile => "declared by the job file",
        GerberLayerRung.FileFunction => "declared by the file's own %TF.FileFunction",
        GerberLayerRung.TechnologySuffix => "matched to a technology layer by its Gerber suffix",
        GerberLayerRung.Heuristic => "GUESSED from the file name",
        _ => "not identified — mapped by hand",
    };
}
