// What a result made from a picture records about where it came from — brief-img-2-image-source-and-kind.md R-im2-5
// (D4). Plain data, persisted as the `ImageSource` block of a .csch (beside AS-6's `ArtworkSource`) and of a traced
// .clay, exactly as written here.
//
// Its PRESENCE is what marks a result as this command's to replace (AS D4's rule): a re-run rewrites a document
// carrying one and refuses one that does not. Nothing in a run reads it.

using System.Globalization;

namespace CircuitRF.Design.Imaging;

/// <summary>A picture-made result's provenance.</summary>
public sealed class ImageProvenance
{
    /// <summary>The kept copy (<see cref="ImageKeep"/>), relative to the document, forward slashes.</summary>
    public string Picture { get; set; } = "";

    /// <summary>The original file NAME — never its path (D4).</summary>
    public string OriginalName { get; set; } = "";

    /// <summary>The SHA-256 of the original bytes, lower-case hex — what a re-run compares.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>The picture's size in pixels as read (after any reduction).</summary>
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    /// <summary>The linear factor the picture was reduced by to fit 50 MP; absent when it was not reduced.</summary>
    public double? ReductionFactor { get; set; }

    /// <summary><c>schematic</c> or <c>layout</c> — the kind the result was read as.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The kind was chosen by the user rather than read (R-im2-3); absent when it was read.</summary>
    public bool? KindForced { get; set; }

    /// <summary>The scale, metres per pixel, written as text with its unit (IM-3); absent for a schematic picture.</summary>
    public string? Scale { get; set; }

    /// <summary>Where the scale came from (D6's evidence, in words); absent with <see cref="Scale"/>.</summary>
    public string? ScaleEvidence { get; set; }

    /// <summary>A layout picture's colour → layer mapping (<c>#rrggbb</c> → layer name or <c>ignore</c>), IM-3.</summary>
    public SortedDictionary<string, string>? LayerMap { get; set; }

    /// <summary>The reading options as name → text — what a re-run needs to say the same thing.</summary>
    public SortedDictionary<string, string> Options { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The circuitRF version that wrote it (the <c>VERSION</c> file, through the assembly).</summary>
    public string Version { get; set; } = "";

    /// <summary>When, UTC, to the second — <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    public string CreatedUtc { get; set; } = "";

    /// <summary>The block for a result read from <paramref name="source"/> as <paramref name="kind"/>, its picture kept at
    /// <paramref name="pictureRef"/>. Scale, layer map and options are the later phases' to fill.</summary>
    public static ImageProvenance For(ImageSource source, ImageKindResult kind, string pictureRef, DateTime utcNow)
    {
        if (kind.Kind == DrawingKind.None)
            throw new ArgumentException("A result is read as a schematic or a layout, never as none.", nameof(kind));
        return new ImageProvenance
        {
            Picture = pictureRef,
            OriginalName = source.OriginalName,
            Sha256 = source.Sha256,
            PixelWidth = source.Raster.Width,
            PixelHeight = source.Raster.Height,
            ReductionFactor = source.Raster.ReductionFactor < 1.0 ? source.Raster.ReductionFactor : null,
            Kind = kind.Name,
            KindForced = kind.Forced ? true : null,
            // AS-6's reading of the version, so the two provenance blocks never disagree.
            Version = Layout.Recognition.RecognitionProvenance.Version,
            CreatedUtc = utcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
    }

}
