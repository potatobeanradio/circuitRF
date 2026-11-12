// ================================================================
//  DataDisplayTemplate.cs  —  the `.cddt` Data Display template
//
//  A template is a `.cdd` with no data of its own: the same JSON, read
//  by the same reader, in which every trace follows the source the
//  display that opens it has selected. Opening one makes a new, unsaved
//  display; the template file itself is never the document that is
//  edited. Framework-free, so a headless caller can make or read one.
// ================================================================

namespace CircuitRF.Render.DataDisplay;

public static class DataDisplayTemplate
{
    /// <summary>The file extension of a Data Display template.</summary>
    public const string Extension = ".cddt";

    /// <summary>
    /// Turns a display's saved form into a template, in place: every trace reads the display's selected source
    /// (<see cref="DataSourceRef.Selected"/>) instead of a named file, a "plot versus" X reads the same source as its
    /// Y, and the display's own source list, its selection and its window geometry are dropped — none of them means
    /// anything to a display made from the template in another workspace, on another run.
    /// </summary>
    /// <remarks>
    /// Everything else — the tabs, the plots and where they are, every trace's quantity, transform and appearance,
    /// the markers, the axes and limits — is kept as authored: that is what a template is for.
    /// </remarks>
    public static DataDisplayConfig MakeTemplate(DataDisplayConfig config)
    {
        config.SelectedDataSource = null;
        config.SourceAliases.Clear();
        config.WindowLeft = config.WindowTop = config.WindowWidth = config.WindowHeight = 0;

        foreach (var tab in config.Tabs)
            foreach (var pc in tab.Plots) Detach(pc);
        foreach (var pc in config.Plots) Detach(pc);
        return config;
    }

    private static void Detach(PlotContainerConfig pc)
    {
        foreach (var tc in pc.Traces)
        {
            // An empty source is a trace with no file of its own (owner data, a summary column) — left as it is.
            if (!string.IsNullOrEmpty(tc.SourcePath)) tc.SourcePath = DataSourceRef.Selected;
            tc.XSourcePath = null;
        }
    }
}
