using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
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

        // The scroll handler above is TUNNELLING FROM THIS CONTROL, so it only ever sees a keystroke
        // that is already routing through this view — which means something inside the view has to
        // hold focus for Page Up/Down to work at all. On first open nothing does: the tab is
        // activated before the view is bound, so focus is still wherever it was and the keystroke
        // routes somewhere else entirely. The three other document views already take focus on
        // activation through this same hook; this one never subscribed, which is the whole bug.
        DataContextChanged += OnDataContextChanged;
    }

    private TechDocument? _subscribedDoc;

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedDoc is not null) _subscribedDoc.ActivationFocusRequested -= OnActivationFocusRequested;
        _subscribedDoc = DataContext as TechDocument;
        if (_subscribedDoc is null) return;

        _subscribedDoc.ActivationFocusRequested += OnActivationFocusRequested;
        // Activated BEFORE the view bound — the first-open case — so the request is sitting pending.
        if (_subscribedDoc.ConsumeActivationFocus()) FocusForScrollingDeferred();
    }

    private void OnActivationFocusRequested()
    {
        _subscribedDoc?.ConsumeActivationFocus();
        FocusForScrollingDeferred();
    }

    /// <summary>
    /// Takes keyboard focus for the editor as a whole, so Page Up/Down reach
    /// <see cref="OnScrollKeyDown"/>.
    ///
    /// <para><b>The view itself, not the visible tab's list</b>, and not a field in a row. Focusing
    /// the list would make the FIRST thing the user sees a control with a selection, in lists whose
    /// rows are deliberately flattened so selection is invisible; focusing a row's text box would put
    /// a caret in an editable process value nobody asked to edit. Focusing the view lands on
    /// <see cref="TargetScrollViewer"/>'s own documented fallback — the visible tab's row list —
    /// which already resolves correctly for whichever tab is showing, including after a tab change.</para>
    ///
    /// <para>Deferred to Background priority for the same reason every other view here defers it: on
    /// first open the visual tree is still being realized and a synchronous Focus() lands on a
    /// control that has not been attached yet. <c>IsTabStop="False"</c> keeps this out of the Tab
    /// order — it is a programmatic focus target, never a stop the user cycles through.</para>
    /// </summary>
    /// <summary>
    /// Undocking is the same dead keyboard by a different route.
    ///
    /// <para>Floating the editor builds a NEW window around this view, and a new window's activation
    /// is not the dock's activation — no <c>IActivatableDocument</c> request fires, so the hook above
    /// never runs, nothing inside the view holds focus, and Page Up/Down are dead again until
    /// something is clicked. Attaching to a visual tree is the one event both routes share.</para>
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusForScrollingDeferred(onlyIfUnclaimed: true);
    }

    /// <param name="onlyIfUnclaimed">Take focus only when nothing else already holds it. The
    /// activation hook may pass false because an explicit activation IS the claim; an attach may
    /// not, because a view can be re-attached by an ordinary dock rearrangement while the user is
    /// typing somewhere else entirely, and yanking the caret out of another panel would be a worse
    /// bug than the one being fixed.</param>
    private void FocusForScrollingDeferred(bool onlyIfUnclaimed = false) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Evaluated inside the posted action, not before it: on the undock path the view is
            // still moving between windows when the attach fires, so the top level asked any earlier
            // is the one being left rather than the one being entered.
            if (onlyIfUnclaimed && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is { } held
                && held is not TopLevel)
                return;

            Focus();
        }, DispatcherPriority.Background);

    // ── Page Up / Page Down / Home / End over the row lists ────────────────────

    private void OnScrollKeyDown(object? sender, KeyEventArgs e)
    {
        // An open dropdown owns all four keys — it is navigating its own items, and the list behind
        // it is not what the user is looking at.
        if (e.Source is ComboBox { IsDropDownOpen: true }) return;

        var action = PanelScrollKeys.ActionFor(e.Key, e.Source is TextBox);
        if (action is null) return;

        var scroll = TargetScrollViewer(e.Source);
        if (scroll is null) return;

        PanelScrollKeys.Apply(action.Value, scroll);
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
