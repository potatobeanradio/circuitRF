// ================================================================
//  IconSelectButton.cs
//  Lightweight toolbar button with a click-to-open vertical option popup.
//  Looks and behaves like a circuitRF toolbar button (via PART_Button +
//  the seg-btn class).  Not a ComboBox.
// ================================================================

using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;

namespace CircuitRF.Ui.DataDisplay.Controls;

public class IconSelectButton : TemplatedControl
{
    // ---- Styled properties -------------------------------------------------

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<IconSelectButton, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<IconSelectButton, object?>(nameof(SelectedItem),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<IconSelectButton, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<bool> HighlightProperty =
        AvaloniaProperty.Register<IconSelectButton, bool>(nameof(Highlight));

    public static readonly StyledProperty<bool> HighlightSelectedProperty =
        AvaloniaProperty.Register<IconSelectButton, bool>(nameof(HighlightSelected), defaultValue: true);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public bool Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public bool HighlightSelected
    {
        get => GetValue(HighlightSelectedProperty);
        set => SetValue(HighlightSelectedProperty, value);
    }

    // ---- Template parts ----------------------------------------------------

    private Button?  _button;
    private Popup?   _popup;
    private ListBox? _listBox;
    private bool     _suppressListBoxCallback;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // Detach old handlers
        if (_button  != null) _button.Click                       -= OnButtonClick;
        if (_listBox != null) _listBox.SelectionChanged           -= OnListBoxSelectionChanged;

        _button  = e.NameScope.Find<Button>("PART_Button");
        _popup   = e.NameScope.Find<Popup>("PART_Popup");
        _listBox = e.NameScope.Find<ListBox>("PART_ListBox");

        if (_button  != null) _button.Click             += OnButtonClick;
        if (_listBox != null) _listBox.SelectionChanged += OnListBoxSelectionChanged;

        ApplyHighlight();
        ApplyHighlightSelected();
        SyncListBoxSelection();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HighlightProperty)         ApplyHighlight();
        if (change.Property == HighlightSelectedProperty) { ApplyHighlight(); ApplyHighlightSelected(); }
        if (change.Property == SelectedItemProperty)      SyncListBoxSelection();
    }

    // ---- Highlight ----------------------------------------------------------

    private void ApplyHighlight()
    {
        if (_button == null) return;
        if (Highlight && HighlightSelected)
            _button.Classes.Add("active");
        else
            _button.Classes.Remove("active");
    }

    private void ApplyHighlightSelected()
    {
        if (_listBox == null) return;
        if (HighlightSelected)
            _listBox.Classes.Remove("flat-select");
        else
            _listBox.Classes.Add("flat-select");
    }

    // ---- Selection sync ----------------------------------------------------

    private void SyncListBoxSelection()
    {
        if (_listBox == null) return;
        _suppressListBoxCallback = true;
        _listBox.SelectedItem = SelectedItem;
        _suppressListBoxCallback = false;
    }

    // ---- Event handlers ----------------------------------------------------

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_popup != null)
            _popup.IsOpen = !_popup.IsOpen;
    }

    private void OnListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressListBoxCallback || _listBox == null) return;
        if (_listBox.SelectedItem is { } item)
        {
            SelectedItem = item;
            if (_popup != null) _popup.IsOpen = false;
        }
    }
}
