using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// A value that reads as text until you click it, then becomes a text box — harmonicaRF's readout
/// strip editor, in the shape a densely-packed properties pane wants.
/// </summary>
/// <remarks>
/// <b>Why this exists rather than a <see cref="TextBox"/> with a thin border.</b> The owner's ask
/// (2026-08-19) was for the Match Designer's specification pane to use "an inline text editor —
/// harmonicaRF has already implemented this for a UI panel, please reuse it, it selects units within
/// the text properly". The part that is genuinely reusable is
/// <see cref="InlineEdit.ValueSelectionLength"/> and <see cref="InlineEdit.MeasureWidth"/>, and this
/// control shares both with the strip. What is NOT shared is the hosting: the strip floats its box in
/// a <c>Canvas</c> overlay because its columns are width-shared and an in-place box would shove every
/// column sideways; a Designer row is a fixed grid cell, so the box swaps in place and nothing moves.
///
/// <para><b>Double-click opens it</b> — the same gesture the readout strip uses, and the one the
/// owner asked for. A single click opens an editor the user only meant to click past.</para>
///
/// <para><b>The three-key contract is the same everywhere in this application</b>: Return commits and
/// sets <c>e.Handled</c> (or the hosting window's default button takes it), LostFocus commits, Escape
/// reverts. Getting any one of the three wrong is an edit the user loses by clicking away.</para>
///
/// <para><b>The unit is part of the text and is not selected.</b> <see cref="Text"/> carries the whole
/// "50 Ω" / "1.5 nH" string; opening the editor pre-selects only "50" / "1.5", so typing replaces the
/// number and keeps the unit. That is the behaviour the schematic editor's own inline edit has, and
/// what the owner meant by "selects units within the text properly".</para>
/// </remarks>
public sealed class InlineEditText : Panel
{
    /// <summary>The displayed text, unit included. Two-way: a commit writes the typed string back.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<InlineEditText, string?>(
            nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Point size for both the resting text and the open editor.</summary>
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<InlineEditText, double>(nameof(FontSize), 11.0);

    /// <summary>Resting text colour. The open editor keeps the theme's own box colours.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<InlineEditText, IBrush?>(nameof(Foreground));

    /// <summary>Shown, dimmed, when <see cref="Text"/> is empty — never committed.</summary>
    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<InlineEditText, string?>(nameof(Watermark));

    /// <summary>
    /// Which edge the value sits against — <see cref="HorizontalAlignment.Left"/> by default, and
    /// <see cref="HorizontalAlignment.Right"/> for a label-left/value-right properties row.
    /// </summary>
    /// <remarks>
    /// The RESTING text and the OPEN editor honour it together, which is the whole point: a value
    /// that is right-aligned until you double-click it and then jumps to the left edge of its column
    /// reads as a different control appearing rather than the same one opening.
    /// </remarks>
    public static readonly StyledProperty<HorizontalAlignment> HorizontalContentAlignmentProperty =
        AvaloniaProperty.Register<InlineEditText, HorizontalAlignment>(
            nameof(HorizontalContentAlignment), HorizontalAlignment.Left);

    /// <summary>True while an editor box is open on this control.</summary>
    public static readonly DirectProperty<InlineEditText, bool> IsEditingProperty =
        AvaloniaProperty.RegisterDirect<InlineEditText, bool>(nameof(IsEditing), o => o.IsEditing);

    static InlineEditText()
    {
        AffectsRender<InlineEditText>(TextProperty, ForegroundProperty);
        FocusableProperty.OverrideDefaultValue<InlineEditText>(true);
    }

    private readonly TextBlock _display;
    private TextBox? _editor;
    private string _pristine = "";
    private bool _isEditing;

    /// <inheritdoc cref="TextProperty"/>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <inheritdoc cref="FontSizeProperty"/>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <inheritdoc cref="WatermarkProperty"/>
    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <inheritdoc cref="HorizontalContentAlignmentProperty"/>
    public HorizontalAlignment HorizontalContentAlignment
    {
        get => GetValue(HorizontalContentAlignmentProperty);
        set => SetValue(HorizontalContentAlignmentProperty, value);
    }

    /// <inheritdoc cref="IsEditingProperty"/>
    public bool IsEditing
    {
        get => _isEditing;
        private set => SetAndRaise(IsEditingProperty, ref _isEditing, value);
    }

    /// <summary>Raised after a commit has written <see cref="Text"/>.</summary>
    public event EventHandler<string>? Committed;

    /// <summary>Builds the resting half. The editor box is constructed only when one is opened.</summary>
    public InlineEditText()
    {
        _display = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Children.Add(_display);
        SyncDisplay();

        // A Panel with no Background is not hit-testable in Avalonia, so a double-click on the empty
        // part of the slot (anywhere but the glyphs themselves) would reach whatever is behind it and
        // the editor would only open when the user hit the text exactly. Transparent is a background:
        // it paints nothing and still takes the pointer.
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        // DOUBLE-click opens it, the same gesture harmonicaRF's readout strip uses (owner,
        // 2026-08-19). A single click was wrong for two reasons: it opens an editor the user only
        // meant to click past, and it makes the control disagree with the one it was asked to reuse.
        DoubleTapped += (_, e) => { BeginEdit(); e.Handled = true; };
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FontSizeProperty
            || change.Property == ForegroundProperty || change.Property == WatermarkProperty
            || change.Property == HorizontalContentAlignmentProperty)
            SyncDisplay();
    }

    private void SyncDisplay()
    {
        string text = Text ?? "";
        bool empty = text.Length == 0;
        _display.Text = empty ? Watermark ?? "" : text;
        _display.Opacity = empty ? 0.5 : 1.0;
        _display.FontSize = FontSize;
        _display.HorizontalAlignment = HorizontalContentAlignment;
        if (Foreground is { } fg) _display.Foreground = fg;
    }

    /// <summary>Opens the editor, seeded with the current text and the VALUE half selected.</summary>
    public void BeginEdit()
    {
        if (_editor is not null) return;

        _pristine = Text ?? "";
        var typeface = new Typeface(_display.FontFamily, _display.FontStyle, _display.FontWeight);
        var box = new TextBox
        {
            Text = _pristine,
            FontSize = FontSize,
            Padding = new Thickness(2, 0),
            MinHeight = 0,
            MinWidth = 0,
            // Aligned to its own edge and explicitly WIDTH-SET, never stretched (owner-reported,
            // 2026-08-19:
            // "too wide when it appears — only slightly wider than the text that is present"). This
            // control usually sits in a `*` grid column, so a stretched box fills the column however
            // short the value is; the width is measured from the text and re-measured as it is
            // typed, which is what harmonicaRF's own strip editor does.
            HorizontalAlignment = HorizontalContentAlignment,
            Width = InlineEdit.MeasureWidth(_pristine, FontSize, typeface),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) =>
            box.Width = InlineEdit.MeasureWidth(box.Text ?? "", FontSize, typeface);

        _editor = box;
        Children.Add(box);
        _display.IsVisible = false;
        IsEditing = true;

        // A click ANYWHERE outside the box commits and closes it. LostFocus alone is not enough:
        // clicking a non-focusable area (a label, the pane's own background, a drawn canvas) moves
        // focus nowhere at all, so the box never loses it and the editor just stays open — which is
        // the bug the owner hit. Tunnelling from the top level catches the press before whatever is
        // under it decides whether to take focus. Same reasoning as the readout strip's own
        // strip-level tunnel handler, hosted here at the window instead of at a panel.
        var top = TopLevel.GetTopLevel(this);
        void OnOutsidePress(object? _, PointerPressedEventArgs e)
        {
            if (e.Source is Visual v && (ReferenceEquals(v, box) || box.IsVisualAncestorOf(v))) return;
            End(true);
        }
        top?.AddHandler(PointerPressedEvent, OnOutsidePress, RoutingStrategies.Tunnel);

        void End(bool commit)
        {
            if (_editor != box) return;
            _editor = null;
            top?.RemoveHandler(PointerPressedEvent, OnOutsidePress);
            Children.Remove(box);
            _display.IsVisible = true;
            IsEditing = false;

            string typed = box.Text ?? "";
            if (commit && typed != _pristine)
            {
                Text = typed;
                Committed?.Invoke(this, typed);
            }
            SyncDisplay();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) { End(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { End(false); e.Handled = true; }
        };
        box.LostFocus += (_, _) => End(true);

        box.Focus();
        box.SelectionStart = 0;
        box.SelectionEnd = InlineEdit.ValueSelectionLength(_pristine);
    }

    /// <summary>
    /// Commits whatever is currently typed, if an editor is open — what an Apply button needs, since
    /// a box that has not lost focus has not written anything back yet.
    /// </summary>
    public void CommitPending() => _editor?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));

}
