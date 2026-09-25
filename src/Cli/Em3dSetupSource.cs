using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;

namespace CircuitRF.Cli;

/// <summary>
/// A <c>.cem</c> read, resolved and — for a 3D setup — generated, the way every verb that looks at a
/// 3D setup reads one (brief-em3d-5). <c>render</c> and <c>explain</c> both come through here, so
/// the picture and the report are of the same problem: the same two walk-ups
/// (<see cref="EmSetupResolver"/>), the same generator, the same stem-paired <c>.wBond</c>.
/// </summary>
internal sealed record Em3dSetupSource(
    string                 Path,
    EmSetup                Setup,
    EmSetupResolution      Resolution,
    Em3dGenerationResult?  Generated,
    string?                Refusal)
{
    /// <summary>Reads <paramref name="path"/>. Throws what the <c>.cem</c> reader throws on a file
    /// that will not parse; every other failure is <see cref="Refusal"/>.</summary>
    public static Em3dSetupSource Load(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        var setup = EmSetupPersistence.LoadFromFile(full);
        var resolution = EmSetupResolver.Resolve(full, setup.LayoutRef, DocumentKinds.AncestorCws(full),
                                                 new TechnologyCache());
        return From(full, setup, resolution);
    }

    /// <summary>A setup already read and resolved — <c>explain</c>'s case, which has done both for its
    /// walks — generated exactly as <see cref="Load"/> generates it.</summary>
    public static Em3dSetupSource From(string full, EmSetup setup, EmSetupResolution resolution)
    {
        if (!setup.Is3D) return new Em3dSetupSource(full, setup, resolution, null, null);

        if (resolution.Source is not { } source)
            return new Em3dSetupSource(full, setup, resolution, null,
                resolution.Diagnostics.Count > 0 ? string.Join(" ", resolution.Diagnostics)
                                                 : "its layout reference resolves to nothing.");
        if (source.Technology is not { } tech)
            return new Em3dSetupSource(full, setup, resolution, null,
                EmDiagnostics.NoTechnology(setup.LayoutRef).Render());

        var generated = Em3dGenerator.Generate(setup, source, tech);
        return new Em3dSetupSource(full, setup, resolution, generated,
            generated.Problem is null ? generated.Refusal ?? "the 3D problem could not be built." : null);
    }
}
