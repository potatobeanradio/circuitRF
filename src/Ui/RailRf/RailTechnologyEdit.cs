// R-rail22-2a — what the Edit Technology button says when it cannot do the thing it offers.

using System;
using System.IO;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// The three states in which railRF's <b>Edit Technology</b> button is visible and cannot open
/// anything, and the sentence each one says.
/// </summary>
/// <remarks>
/// <b>A control that is live and silent is indistinguishable from a control that is broken</b> —
/// the rule <c>CanPickSelectedNet</c> already states in its own remarks, and the reason this file
/// exists. The button's handler had two bare <c>return</c>s in it, and the second is reachable in
/// ordinary use: railRF is an UNOWNED window that outlives the workspace behind it, so
/// <c>WorkspaceLocator.Any()</c> comes back null the moment that workspace is closed, and the
/// button is then visible, enabled, and does nothing at all.
///
/// <para><b>Framework-free, so the sentences are gated without an application host</b> — the same
/// decision <see cref="RailImportOptions"/>' own remarks make. The handler in
/// <c>RailRfWindow.axaml.cs</c> is then the four lines that choose between them.</para>
/// </remarks>
public static class RailTechnologyEdit
{
    /// <summary>
    /// True where <paramref name="techPath"/> is under the operating system's temporary directory —
    /// which is where the import's throwaway path mints its <c>.ctech</c>, and no workspace owns it.
    /// </summary>
    /// <remarks>
    /// The second route to the same silent button, and the one the user chose rather than fell into:
    /// unticking <i>keep the artwork in this workspace</i> puts the whole cell in a temp directory,
    /// so there is no workspace for <c>OpenTechnologyDocument</c> to put a document in. R-rail22-2b
    /// puts that consequence on the dialog; this is what happens to whoever did not read it.
    /// </remarks>
    public static bool IsTemporary(string? techPath)
    {
        if (techPath is not { Length: > 0 }) return false;
        try
        {
            string full = Path.GetFullPath(techPath);
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            return full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)   { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException)  { return false; }
    }

    /// <summary>The board resolved no <c>.ctech</c> at all. The button is hidden in this state, so
    /// this is the sentence for the case that should not arise rather than for one that does.</summary>
    public const string NoTechnologyRefusal =
        "This board resolved no .ctech file, so there is no technology document to open. Import the "
      + "board again — the Gerber import mints a technology from the set's own layers and points the "
      + "artwork at it.";

    /// <summary>The <c>.ctech</c> is in a temp directory, so no workspace owns it.</summary>
    public static string TemporaryRefusal(string techPath) =>
        $"“{Path.GetFileName(techPath)}” was imported to a temporary location and is not "
      + "part of any workspace, so there is no document to open it as — the stackup is still being "
      + "used to price this board, it just cannot be edited. Import the board again with “Keep "
      + "the artwork in this workspace as a cell” ticked, which is the default.";

    /// <summary>There is a technology and it is in a workspace, and no workspace window is open to
    /// show it in.</summary>
    public static string NoWorkspaceRefusal(string techPath) =>
        $"There is no workspace open to edit “{Path.GetFileName(techPath)}” in. railRF is "
      + "its own window and outlives the workspace behind it, and the technology editor is the "
      + "workspace's own document rather than a second editor railRF carries. Open the workspace "
      + "holding this board and press this again.";
}
