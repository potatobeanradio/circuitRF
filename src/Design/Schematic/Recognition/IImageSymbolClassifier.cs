// The seam between the schematic-picture reader and whatever names a symbol — brief-img-8-schematic-symbols.md R-im8-6
// (overview D1).
//
// SchematicSymbols.Read calls this and nothing else to learn what a region is: the template matcher
// (TemplateSymbolClassifier) is the one implementation, and a later classifier is added by implementing the interface.
// What the reader does with the answer — cutting out a multi-pin device, calling a two-pin region nothing matched `?`,
// leaving a one-pin one open (D9) — is the same whichever classifier gave it, and nothing downstream can tell which did.

using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>How a template lies on the picture, in the schematic's own terms: mirrored in x, then turned
/// (<see cref="SchematicGeometry.LocalToWorld"/>), so an instance placed with it draws over the picture.</summary>
public readonly record struct SymbolOrientation(SymbolRotation Rotation, bool MirrorX)
{
    /// <summary>The eight, in the order a tie is settled: unmirrored before mirrored, then by rotation.</summary>
    public static IReadOnlyList<SymbolOrientation> All { get; } =
    [
        .. new[] { false, true }.SelectMany(m => new[] { SymbolRotation.R0, SymbolRotation.R90, SymbolRotation.R180, SymbolRotation.R270 }
                                                .Select(r => new SymbolOrientation(r, m))),
    ];

    /// <summary>A template point (y down) turned into this orientation.</summary>
    public (double X, double Y) Apply(double x, double y)
    {
        double mx = MirrorX ? -x : x;
        return Rotation switch
        {
            SymbolRotation.R90 => (-y, mx),
            SymbolRotation.R180 => (-mx, -y),
            SymbolRotation.R270 => (y, -mx),
            _ => (mx, y),
        };
    }

    public override string ToString() => ((int)Rotation).ToString(System.Globalization.CultureInfo.InvariantCulture) + "°" + (MirrorX ? " mirrored" : "");
}

/// <summary>One template laid on one region.</summary>
/// <param name="PinAttachments">For each of the template's pins, in order, the index of the region attachment it lies
/// on.</param>
/// <param name="Score">The classifier's distance — for the template matcher the modified-Hausdorff distance, in units of
/// the stroke width. Lower is better.</param>
public sealed record SymbolCandidate(SymbolTemplate Template, SymbolOrientation Orientation, IReadOnlyList<int> PinAttachments,
                                     double Score)
{
    public ImageSymbolKind Kind => Template.Kind;
}

/// <summary>A template that matched best but failed its kind's structure check (R-im8-4), so its runner-up was taken.</summary>
public sealed record SymbolDemotion(SymbolCandidate Candidate, StructureCheck Failed);

/// <summary>A classifier's answer for one region.</summary>
/// <param name="Best">The accepted reading, or null when nothing is accepted.</param>
/// <param name="RunnerUp">The best reading of a different kind, whether or not <paramref name="Best"/> is null.</param>
/// <param name="Demotions">Better-scoring readings set aside by a structure check, best first.</param>
public sealed record SymbolClassification(SymbolCandidate? Best, SymbolCandidate? RunnerUp, IReadOnlyList<SymbolDemotion> Demotions,
                                          PartConfidence Confidence)
{
    public static SymbolClassification None { get; } = new(null, null, [], PartConfidence.Low);
}

/// <summary>Names a symbol region. Pure: the same region gives the same answer.</summary>
public interface IImageSymbolClassifier
{
    /// <param name="strokeWidth">The picture's stroke width w, pixels.</param>
    SymbolClassification Classify(SymbolRegion region, double strokeWidth);
}
