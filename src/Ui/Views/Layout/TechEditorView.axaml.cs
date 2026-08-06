using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    public TechEditorView() => InitializeComponent();

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
