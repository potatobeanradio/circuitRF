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
using Avalonia.Threading;

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
        // A REPLACED item list clears the ListBox's selection without anything telling this control,
        // so the popup opened next with nothing highlighted while the face still showed the choice.
        // The Match Designer's Order selector is bound to a collection that is genuinely rebuilt when
        // the permitted parities change (MatchDesignerViewModel.RefreshOrderChoices), which is what
        // made it visible.
        if (change.Property == ItemsSourceProperty)       SyncListBoxSelection();
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
        if (_popup == null) return;
        if (_popup.IsOpen) { _popup.IsOpen = false; return; }

        // Opened, not toggled-into: whatever the list last did to its own selection, the face is the
        // truth, so the highlight is put back before anyone sees the popup.
        SyncListBoxSelection();
        _popup.IsOpen = true;
    }

    /// <summary>
    /// A picked item: <b>close the popup, then publish the choice on the next dispatcher turn</b>.
    /// </summary>
    /// <remarks>
    /// <b>Order and timing are both load-bearing, and this used to do neither.</b> It published
    /// <see cref="SelectedItem"/> first and closed the popup afterwards, so an application that reacts
    /// to a selection did all of its work — rebuilding view-models, replacing the very
    /// <see cref="ItemsSource"/> this open popup's ListBox is bound to, re-laying out the window —
    /// INSIDE the ListBox's own <c>SelectionChanged</c>, with the popup still open. That is a
    /// re-entrant mutation of a live popup: clearing the bound collection makes the ListBox raise
    /// <c>SelectionChanged</c> again from inside this handler, and the <c>PopupRoot</c> being torn
    /// down a moment later still has that layout pass owing.
    ///
    /// <para>The owner hit the end of it as a hard crash — an unhandled
    /// <c>NullReferenceException</c> inside <c>Popup.RootTemplateApplied</c>, reached from this very
    /// method's <c>IsOpen = true</c> on a LATER click (2026-08-20, changing an input control in the
    /// Match Designer's Specification panel). The Designer is where it surfaced because its Order
    /// selector is the one <see cref="IconSelectButton"/> in the application whose item list is
    /// rebuilt by the act of choosing from it.</para>
    ///
    /// <para>Closing first and posting the publish costs nothing anyone can perceive — the popup is
    /// already gone by the time the application sees the choice — and it means no consumer of this
    /// control can run while its popup is open, whatever that consumer does.</para>
    /// </remarks>
    private void OnListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressListBoxCallback || _listBox == null) return;
        if (_listBox.SelectedItem is not { } item) return;

        if (_popup != null) _popup.IsOpen = false;

        if (Equals(SelectedItem, item)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!Equals(SelectedItem, item)) SelectedItem = item;
        });
    }
}
