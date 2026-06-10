namespace CircuitRF.Ui.ViewModels.ProjectTree;

/// <summary>
/// Callbacks provided by WorkspaceViewModel to ProjectTreeNodeViewModel commands.
/// Injected at VM construction via ProjectTreeTool.SetActions.
/// Each method maps to one user gesture (double-click or context-menu item).
/// </summary>
public interface ITreeActions
{
    /// <summary>Double-click open/activate for any node kind.</summary>
    void OpenNode(ProjectTreeNodeViewModel node);

    /// <summary>Make Primary — write .ccell, Refresh tree.</summary>
    void MakePrimary(ProjectTreeNodeViewModel node);

    /// <summary>Reveal in OS file manager (Finder / Explorer / xdg-open).</summary>
    void Reveal(ProjectTreeNodeViewModel node);

    /// <summary>New Cell on workspace/library node — prompts for name, creates folder, Refresh.</summary>
    Task NewCellAsync(ProjectTreeNodeViewModel parentNode);

    /// <summary>New Cell in the workspace root — no parent node; used by File menu and tree-header button.</summary>
    Task NewCellInWorkspaceAsync();

    /// <summary>New Symbol on cell node — prompts for name, creates .csym, opens editor, Refresh.</summary>
    Task NewSymbolAsync(ProjectTreeNodeViewModel cellNode);

    /// <summary>New Schematic on cell node — prompts for name, creates .csch, opens tab, Refresh.</summary>
    Task NewSchematicAsync(ProjectTreeNodeViewModel cellNode);
}
