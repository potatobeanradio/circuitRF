// Teaches the kit importer to recognise the two process files C0 reads.
//
// These live on the UI side rather than among the built-in recognisers because the readers they
// speak for do: circuitRF's technology model is a UI-project type. That is exactly the extension the
// registry was built for — circuitRF ships the generic recognisers and a host adds whatever else it
// can read.
//
// Recognising them does NOT make an ordinary kit import build a technology. It makes the import
// REPORT that the kit carries process data and say how to get at it, which is the difference between
// a user finding File ▸ Import ▸ Technology and never knowing it applies to them.

using CircuitRF.Core.Pdk;

namespace CircuitRF.Ui.Layout.TechImport;

/// <summary>Registers the process-technology recognisers with the kit importer. Idempotent.</summary>
public static class ProcessTechnologyRecognizers
{
    private static bool _registered;

    public static void RegisterOnce()
    {
        if (_registered) return;
        _registered = true;

        PdkFormatRegistry.Register(new StackDescriptionRecognizer());
        PdkFormatRegistry.Register(new LayerTableRecognizer());
    }
}

/// <summary>
/// An interconnect technology file — the process's own stack description.
///
/// <para>Recognised by its GRAMMAR (a technology declaration plus a conductor statement), never by
/// extension: kits spell this file <c>.itf</c>, <c>.dat</c> and <c>.txt</c>, sometimes several ways
/// in one delivery, and the extensions are claimed by unrelated formats elsewhere.</para>
/// </summary>
internal sealed class StackDescriptionRecognizer : IPdkFormatRecognizer
{
    // Above the extension recogniser, which claims .dat for model data.
    public int Priority => 25;

    public PdkAsset? Recognize(string path, Func<string> peek)
    {
        string text = peek();
        if (text.Length == 0 || !ProcessStackReader.LooksLikeStackFile(text)) return null;

        return new PdkAsset(path, PdkAssetKind.LayerTechnology, PdkAssetSupport.Supported,
                            "interconnect technology file",
                            "The process stack: layer thicknesses, permittivities, sheet resistances " +
                            "and via geometry. Build a technology from it with File ▸ Import ▸ " +
                            "Technology.");
    }
}

/// <summary>
/// A layer-properties table — the process's own layer numbers, names and colours.
///
/// <para>Classified on a PARTIAL read (the importer's peek), so the check is the weaker head-level
/// one; the strict predicate runs when the file is actually read.</para>
/// </summary>
internal sealed class LayerTableRecognizer : IPdkFormatRecognizer
{
    public int Priority => 25;

    public PdkAsset? Recognize(string path, Func<string> peek)
    {
        string text = peek();
        if (text.Length == 0 || !LayerPropertiesReader.HeadLooksLikeLayerProperties(text)) return null;

        return new PdkAsset(path, PdkAssetKind.LayerTechnology, PdkAssetSupport.Supported,
                            "layer properties table",
                            "Layer numbers, names, purposes and display colours. Build a technology " +
                            "from it with File ▸ Import ▸ Technology.");
    }
}
