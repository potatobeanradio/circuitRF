using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Layout;

/// <summary>
/// Code-behind for the .ctech editor. Every editable cell across the three sections (layer
/// table, stackup, DRC rules) commits through one of two generic dispatchers keyed by the
/// control's <see cref="Control.Tag"/> and its DataContext's row-VM type — avoids one handler
/// method per field across three different row VMs.
/// </summary>
public partial class TechEditorView : UserControl
{
    public TechEditorView()
    {
        InitializeComponent();

        // TUNNELLING, not bubbling: a ListBox handles Page Up/Down and Home/End itself (moving the
        // selection), and these lists are flattened so that selection is invisible — the keystroke
        // would appear to do nothing while quietly changing what is selected. Getting there first is
        // what makes the key scroll the pane instead.
        AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel);
    }

    // ── Page Up / Page Down / Home / End over the row lists ────────────────────

    private void OnScrollKeyDown(object? sender, KeyEventArgs e)
    {
        // An open dropdown owns all four keys — it is navigating its own items, and the list behind
        // it is not what the user is looking at.
        if (e.Source is ComboBox { IsDropDownOpen: true }) return;

        var action = TechEditorScrollKeys.ActionFor(e.Key, e.Source is TextBox);
        if (action is null) return;

        var scroll = TargetScrollViewer(e.Source);
        if (scroll is null) return;

        switch (action)
        {
            case TechScrollAction.PageUp:   scroll.PageUp();      break;
            case TechScrollAction.PageDown: scroll.PageDown();    break;
            case TechScrollAction.Home:     scroll.ScrollToHome(); break;
            case TechScrollAction.End:      scroll.ScrollToEnd();  break;
        }
        e.Handled = true;
    }

    /// <summary>
    /// The scroller the keystroke belongs to: the one the focused control is INSIDE, if any — which
    /// is the row list when focus is in a row, and the Stackup tab's own drawing-layer picker when
    /// focus is in that — falling back to the visible tab's row list, which is where focus sits when
    /// the user has just typed in the filter box (that box is deliberately outside the list).
    /// </summary>
    private ScrollViewer? TargetScrollViewer(object? source)
    {
        if (source is Visual v && v.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } inner)
            return inner;

        var list = SectionTabs?.SelectedIndex switch
        {
            0 => LayersList,
            1 => StackupList,
            2 => DrcRulesList,
            3 => InterchangeList,
            _ => null,
        };
        return list?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => CommitField(sender);

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitField(sender);
            e.Handled = true;
        }
    }

    private static void CommitField(object? sender)
    {
        if (sender is not Control c) return;
        var tag = c.Tag as string;

        switch (c.DataContext)
        {
            case LayerRowViewModel lr:
                switch (tag)
                {
                    case "Name":        lr.CommitName();        break;
                    case "LayerNumber": lr.CommitLayerNumber(); break;
                    case "Datatype":    lr.CommitDatatype();    break;
                    case "FillOpacity": lr.CommitFillOpacity(); break;
                    case "ZOrder":      lr.CommitZOrder();      break;
                    case "Purpose":     lr.CommitPurpose();     break;
                    case "GdsiiLayer":         lr.CommitGdsiiLayer();         break;
                    case "GdsiiDatatype":      lr.CommitGdsiiDatatype();      break;
                    case "DxfLayerName":       lr.CommitDxfLayerName();       break;
                    case "GerberSuffix":       lr.CommitGerberSuffix();       break;
                    case "GerberFileFunction": lr.CommitGerberFileFunction(); break;
                }
                break;

            case StackupLayerRowViewModel sr:
                switch (tag)
                {
                    case "Name":      sr.CommitName();      break;
                    case "Thickness": sr.CommitThickness(); break;
                    case "Epsr":      sr.CommitEpsr();      break;
                    case "TanD":      sr.CommitTanD();      break;
                    case "Mur":       sr.CommitMur();       break;
                    case "Sigma":     sr.CommitSigmaSm();   break;
                    case "WallThickness": sr.CommitWallThickness(); break;
                }
                break;

            case DrcRuleRowViewModel dr:
                switch (tag)
                {
                    case "Name":     dr.CommitName();     break;
                    case "Value":    dr.CommitValue();    break;
                    case "RegionA":  dr.CommitRegionA();  break;
                    case "RegionB":  dr.CommitRegionB();  break;
                    case "Window":   dr.CommitWindow();   break;
                    case "MinRatio": dr.CommitMinRatio(); break;
                    case "MaxRatio": dr.CommitMaxRatio(); break;
                }
                break;
        }
    }

    private void OnComboSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not Control c) return;
        var tag = c.Tag as string;

        if (c.DataContext is DrcRuleRowViewModel dr)
        {
            switch (tag)
            {
                case "Kind":     dr.CommitKind();     break;
                case "Layer":    dr.CommitLayer();    break;
                case "Severity": dr.CommitSeverity(); break;
                case "NetScope": dr.CommitNetScope(); break;
            }
        }
    }
}
