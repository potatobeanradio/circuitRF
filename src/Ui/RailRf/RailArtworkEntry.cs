// R-rail22-1b — which of GerberImportEntry's two doors the artwork goes through.

using System.IO;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// Sends an artwork path to the right <see cref="GerberImportEntry"/> entry point.
/// </summary>
/// <remarks>
/// <b>NOTHING HERE IMPORTS ANYTHING, and it is not a second import path</b> — the Import file's own
/// header forbids one, and this holds no file list, no classification and no decision about layers.
/// It is the one <c>if</c> that R-rail22-1a created: the dialog can now name a FOLDER up front, and
/// <see cref="GerberImportEntry.Run"/> is the wrong door for one — it surveys the folder holding the
/// chosen FILE, so a folder handed to it would be surveyed against its PARENT and the user would be
/// asked about the enclosing folder of the folder they just chose.
///
/// <para><see cref="GerberImportEntry.RunFolder"/> is the door that already exists for a folder
/// chosen outright, and it takes no <c>pickFolder</c> because there is nothing left to ask — which is
/// R-rail22-1b stated as a property of the call rather than as a promise: the later prompt is not
/// removed, it simply cannot fire on this route.</para>
/// </remarks>
public static class RailArtworkEntry
{
    /// <summary>True where the artwork the user named is a FOLDER — a Gerber set, chosen whole.</summary>
    public static bool IsFolder(string artworkPath) =>
        artworkPath is { Length: > 0 } && Directory.Exists(artworkPath);

    /// <summary>
    /// Imports <paramref name="artworkPath"/> through whichever entry point it is.
    /// </summary>
    /// <param name="promptForScope">R-L4h-3's this-file-or-its-folder question. <b>A folder named up
    /// front never reaches it.</b></param>
    /// <param name="pickFolder">R-L4h-5's folder picker, reached only from that question's third
    /// answer. Still here, still reachable, and never invoked on the folder route.</param>
    public static GerberImport.ImportResult Import(
        string artworkPath,
        string parentDir,
        int dbuPerMicron,
        GerberImportEntry.PromptForScope promptForScope,
        GerberImportEntry.PickFolder pickFolder,
        GerberImport.ResolveDrillFormat? resolveDrillFormat = null)
        => IsFolder(artworkPath)
            ? GerberImportEntry.RunFolder(
                artworkPath, parentDir, null, dbuPerMicron, resolveDrillFormat: resolveDrillFormat)
            : GerberImportEntry.Run(
                artworkPath, parentDir, null, dbuPerMicron, promptForScope, pickFolder,
                resolveDrillFormat: resolveDrillFormat);
}
