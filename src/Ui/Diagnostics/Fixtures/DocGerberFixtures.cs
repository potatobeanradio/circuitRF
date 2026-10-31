using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// Gerber import figures (brief-gerber-import-target-technology R-gt-10): the Layer Mapping dialog with
/// its Technology row, built exactly as File ▸ Import ▸ Gerber builds it — a real workspace, a real file
/// set, the real import run up to its mapping callback, and the real dialog.
/// </summary>
public static class DocGerberFixtures
{
    public const string SixLayerId  = "pcb-6layer_FR-4_1.6mm_1oz";
    public const string FourLayerId = "pcb-4layer_FR-4_62mil_1oz";

    /// <summary>The dialog as it opens on a six-copper set in a workspace whose default is six-layer.</summary>
    public static FigureScene GerberImportTechnology() => Scene(openCombo: false);

    /// <summary>The same dialog with the Technology combo open.</summary>
    public static FigureScene GerberImportTechnologyChoices() => Scene(openCombo: true);

    private static FigureScene Scene(bool openCombo)
    {
        string root = Directory.CreateTempSubdirectory("crf-doc-gerber-").FullName;
        try
        {
            var dialog = BuildDialog(root, out _);
            var content = (Control)dialog.Content!;
            dialog.Content = null;

            // The window's content carries the dialog's 20/16 px margin, and the generator sizes what
            // it is handed to the figure's full width — so lifted out bare, the dialog would be laid
            // out 40 px wider than it is and clipped at the right, Continue button included. As
            // padding on a host the margin stays INSIDE the figure, where the window keeps it.
            var margin = content.Margin;
            content.Margin = default;
            var body = new Border { Padding = margin, Child = content };

            return new FigureScene(body)
            {
                Popups = openCombo ? OpenTechnologyCombo : null,
                Cleanup = () => { try { Directory.Delete(root, recursive: true); } catch (IOException) { } },
            };
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// The dialog File ▸ Import ▸ Gerber shows for <see cref="WriteSixCopperSet"/> in a workspace under
    /// <paramref name="root"/> that holds the six- and four-layer technologies, the six-layer one its
    /// default. <paramref name="request"/> is the import's own request, captured at its mapping callback.
    /// </summary>
    public static LayerMappingDialog BuildDialog(string root, out ImportMappingRequest request)
    {
        var ws = WorkspaceCreate.Create(root, "Board", SixLayerId);
        string wsDir = Path.GetDirectoryName(ws.CwsPath)!;
        WorkspaceCreate.InstallTechnology(wsDir, FourLayerId);
        string defaultPath = Path.Combine(wsDir, "tech", SixLayerId + ".ctech");
        var workspaceTech = TechPersistence.LoadFromFile(defaultPath);

        string setDir = Path.Combine(root, "board-gerbers");
        var files = WriteSixCopperSet(setDir);

        ImportMappingRequest? captured = null;
        GerberImport.Import(files, wsDir, "board", workspaceTech, LayoutUnits.DefaultDbuPerMicron,
            resolveMapping: r => { captured = r; return null; });
        request = captured ?? throw new InvalidOperationException(
            "The Gerber import never reached its mapping callback, so there is no dialog to photograph.");

        // The SHIPPED catalog only: TechnologyCatalog.All also lists the technologies installed on the
        // machine running DocGen, and a figure must not depend on whose machine that is.
        var catalog = ShippedTechnologies.All
            .Select(e => new TechnologyCatalogEntry(
                e.Id, ShippedTechnologies.Load(e).Name, TechnologyOrigin.Shipped, e.ResourceName, null))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        var choices = ImportTechnologyChoices.Build(wsDir, request.CopperCount, catalog);
        int selected = ImportTechnologyChoices.DefaultIndex(choices, defaultPath);

        return new LayerMappingDialog("Import Gerber — Layer Mapping", workspaceTech, request, choices, selected, wsDir);
    }

    /// <summary>
    /// Opens the Technology combo and describes the drop-down's CONTENT — the popup's child, which is
    /// what lives in the popup's own top level. The <c>Popup</c> control itself stays in the window's
    /// tree and draws nothing, so describing it (as <c>DocFixtures.OpenDropDown</c> does) fails the
    /// catalog's "the popup drew" check with the list open.
    /// </summary>
    private static IReadOnlyList<PopupCapture> OpenTechnologyCombo(Control root)
    {
        var combo = root.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "TechnologyCombo");
        combo.IsDropDownOpen = true;
        UiArtworkGenerator.Pump();

        var popup = combo.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().FirstOrDefault();
        if (popup?.Child is not { } list) return [];
        var at = combo.TranslatePoint(new Point(0, combo.Bounds.Height), root) ?? default;
        return DocFixtures.DescribePopup(list, root, at);
    }

    /// <summary>
    /// A synthetic six-copper Gerber set with X2 attributes, fourteen files: six copper, two solder
    /// mask, two paste, two legend, the outline and one Excellon drill file. Every copper layer carries
    /// a pad at the hole and a short trace, so the drill pairs into a via through the whole stack.
    /// </summary>
    public static IReadOnlyList<string> WriteSixCopperSet(string dir)
    {
        Directory.CreateDirectory(dir);
        var files = new List<string>();

        void Art(string name, string function, bool trace)
        {
            string body =
                "%FSLAX46Y46*%\n%MOMM*%\n" +
                $"%TF.FileFunction,{function}*%\n" +
                "%ADD10C,0.600000*%\n%ADD11C,0.200000*%\n" +
                "D10*\nX1000000Y1000000D03*\n" +
                (trace ? "D11*\nX1000000Y1000000D02*\nX4000000Y1000000D01*\n" : "") +
                "M02*\n";
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, body);
            files.Add(path);
        }

        Art("board.gtl", "Copper,L1,Top,Signal", trace: true);
        for (int n = 2; n <= 5; n++)
            Art($"board.g{n - 1}", $"Copper,L{n},Inr,Signal", trace: true);
        Art("board.gbl", "Copper,L6,Bot,Signal", trace: true);
        Art("board.gts", "Soldermask,Top", trace: false);
        Art("board.gbs", "Soldermask,Bot", trace: false);
        Art("board.gtp", "Paste,Top", trace: false);
        Art("board.gbp", "Paste,Bot", trace: false);
        Art("board.gto", "Legend,Top", trace: false);
        Art("board.gbo", "Legend,Bot", trace: false);
        Art("board.gko", "Profile,NP", trace: true);

        string drill = Path.Combine(dir, "board.drl");
        File.WriteAllText(drill, "M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\nX1.000000Y1.000000\nM30\n");
        files.Add(drill);
        return files;
    }
}
