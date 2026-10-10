using CircuitRF.Design.Imaging;

namespace CircuitRF.Cli;

/// <summary>
/// A picture as <c>check</c>, <c>find</c> and <c>explain</c> see it (brief-img-2 R-im2-6): decoded through
/// <see cref="ImageSource"/> and classified through <see cref="ImageKind"/>, the functions the dialog reads with — and
/// nothing beyond them. None of the three reads past IM-2: tracing and recognition are Create from Image's.
/// </summary>
internal static class PictureKinds
{
    public sealed record Reading(ImageSource Source, ImageKindResult Kind);

    /// <summary>The picture at <paramref name="path"/> and its kind, or the decoder's own refusal.</summary>
    public static Reading? Read(string path, out string? refusal)
    {
        var read = ImageSource.FromFile(path);
        refusal = read.Refusal;
        return read.Source is { } source ? new Reading(source, ImageKind.Classify(source.Raster)) : null;
    }
}
