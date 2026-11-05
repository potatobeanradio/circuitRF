using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// The frequency plan editor: one card per <see cref="ViewModels.FrequencySpecViewModel"/> segment (Step / Points /
/// Single, Lin / Log, Start / Stop / count or step) and a + Segment button. The rows and what + Segment does belong to
/// the host — the EM Setup panel, Export to Library (brief vendorlib-11 R-vl11-4a) and the S-parameter analysis — so the control owns markup only.
/// </summary>
public partial class FrequencyPlanEditor : UserControl
{
    public static readonly StyledProperty<IEnumerable?> SegmentsProperty =
        AvaloniaProperty.Register<FrequencyPlanEditor, IEnumerable?>(nameof(Segments));

    public static readonly StyledProperty<ICommand?> AddSegmentCommandProperty =
        AvaloniaProperty.Register<FrequencyPlanEditor, ICommand?>(nameof(AddSegmentCommand));

    /// <summary>The plan's rows.</summary>
    public IEnumerable? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>What + Segment runs.</summary>
    public ICommand? AddSegmentCommand
    {
        get => GetValue(AddSegmentCommandProperty);
        set => SetValue(AddSegmentCommandProperty, value);
    }

    public FrequencyPlanEditor() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty) Rows.ItemsSource = Segments;
        else if (change.Property == AddSegmentCommandProperty) AddButton.Command = AddSegmentCommand;
    }
}
