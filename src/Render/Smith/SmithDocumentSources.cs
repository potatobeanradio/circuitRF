// ================================================================
//  SmithDocumentSources.cs  —  where a `.csmith`'s overlay data is
//
//  brief-smith-12-overlays-via-the-inspector.md R-smith12-3.
//
//  A Smith Chart document is a real document with or without a
//  workspace: Tools ▸ Smith Chart opens a scratch one and Save As is
//  what gives it a home. So an overlay's reference has to resolve two
//  ways — through whatever data-source library the host has open, and,
//  when there is none or it does not know the file, as a path relative
//  to the DOCUMENT. That is brief 8's own convention (R-smith8-2) and
//  the one that survives an archived or moved workspace: the pair moves
//  together and the reference still resolves.
//
//  It is an IPlotDataSources and not a new seam, which is what lets the
//  window and `circuitrf smith` resolve one document's overlays through
//  one loader.
// ================================================================

using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.Smith;

/// <summary>
/// The overlay sources of one <c>.csmith</c>: an inner library first, then files beside the
/// document.
/// </summary>
/// <param name="documentDirectory">What a relative reference resolves against. Null is a scratch
/// document that has never been saved, whose relative references cannot resolve — and saying so is
/// the honest answer, not an exception.</param>
/// <param name="inner">The host's own sources, consulted first. Null outside a workspace.</param>
/// <param name="alsoSearch">Extra directories a relative reference is looked for in, in order —
/// the workspace's <c>results/</c>, for a caller that has one and no library.</param>
public sealed class SmithDocumentSources(
    Func<string?> documentDirectory,
    IPlotDataSources? inner = null,
    IReadOnlyList<string>? alsoSearch = null) : IPlotDataSources
{
    private readonly Dictionary<string, (DataSet? Data, SNP? Snp, string? Error)> _loaded
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A fixed directory, for a caller with no live document.</summary>
    public SmithDocumentSources(string? documentDirectory, IPlotDataSources? inner = null,
                                IReadOnlyList<string>? alsoSearch = null)
        : this(() => documentDirectory, inner, alsoSearch) { }

    /// <summary>
    /// Why <paramref name="sourceRef"/> could not be read, or null when it could.
    /// </summary>
    /// <remarks>
    /// <b>A reference that does not resolve costs the user a comparison and nothing else.</b> That
    /// is the opposite of an S1P ELEMENT, whose missing file is a refusal because the cascade cannot
    /// be walked without it — so this is a sentence a caller reports beside a chart that drew
    /// everything else.
    /// </remarks>
    public string? WhyUnresolved(string? sourceRef)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
            return "This overlay names no source — an overlay IS a reference to data somewhere, so "
                 + "there is nothing for it to draw until it has one.";

        if (ResolveAbs(sourceRef) is not { } abs)
            return $"'{sourceRef}' does not resolve to a file — the path is relative to the "
                 + $"document, so a document and its overlays move together. Looked in "
                 + $"{string.Join(", ", Searched().DefaultIfEmpty("nowhere: this document has not been saved"))}.";

        if (inner is not null && inner.Contains(abs)) return null;

        Load(abs);
        return _loaded[abs].Error is { } why ? $"'{sourceRef}' could not be read: {why}" : null;
    }

    private IEnumerable<string> Searched()
    {
        if (documentDirectory() is { Length: > 0 } dir) yield return dir;
        foreach (string d in alsoSearch ?? []) yield return d;
    }

    // ── IPlotDataSources ─────────────────────────────────────────────────────

    public string? ResolveAbs(string? sourceRef)
    {
        // THE HOST'S ANSWER WINS when it has one that is actually there. A workspace that already
        // holds the run is where a cube reference belongs, and going to disk first would resolve a
        // bare "<name>.npy" beside the document rather than in results/.
        if (inner?.ResolveAbs(sourceRef) is { } fromInner
            && (inner.Contains(fromInner) || File.Exists(fromInner)))
            return fromInner;

        if (string.IsNullOrWhiteSpace(sourceRef)) return null;
        if (Path.IsPathRooted(sourceRef))
            return File.Exists(sourceRef) ? Path.GetFullPath(sourceRef) : null;

        foreach (string dir in Searched())
        {
            string candidate = CircuitRF.Core.RefPath.Resolve(dir, sourceRef);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public bool Contains(string absPath)
        => (inner?.Contains(absPath) ?? false) || File.Exists(absPath);

    public SNP? NetworkFor(string absPath)
        => inner?.NetworkFor(absPath) ?? Load(absPath).Snp;

    public DataSet? DataFor(string absPath)
        => inner?.DataFor(absPath) ?? Load(absPath).Data;

    public string? AliasFor(string absPath) => inner?.AliasFor(absPath);

    public string? DisplayNameFor(string absPath)
        => inner?.DisplayNameFor(absPath) ?? Path.GetFileName(absPath);

    public bool HasMultipleSources => inner?.HasMultipleSources ?? false;

    public DataSet? SelectedData => inner?.SelectedData;

    /// <summary>
    /// Reads one file beside the document, once.
    /// </summary>
    /// <remarks>
    /// <b>Through <see cref="PlotSourceFile"/>, which is the loader the application's library and
    /// <c>circuitrf render</c> both read through.</b> A third one here would be a file the window
    /// and a build machine could disagree about — and the cache is not an optimization: an overlay
    /// and an S1P element in the same document routinely name the same Touchstone, and two reads of
    /// it would be two SNP objects a renormalization could drift between.
    /// </remarks>
    private (DataSet? Data, SNP? Snp, string? Error) Load(string absPath)
    {
        if (_loaded.TryGetValue(absPath, out var hit)) return hit;
        var read = File.Exists(absPath)
            ? PlotSourceFile.Load(absPath)
            : (null, null, $"looked for it at '{absPath}'");
        _loaded[absPath] = read;
        return read;
    }
}
