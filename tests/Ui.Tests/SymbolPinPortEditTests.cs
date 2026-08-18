using Avalonia.Input;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner, 2026-08-17: "If user double clicks on a placed Pin in the Symbol editor, a small dialog
/// should pop up allowing the user to change that Pin's Port number" — and, on the entry itself,
/// "make sure the text is properly validated."
///
/// <para>The dialog is a <c>Window</c> and cannot be constructed headlessly, so what is gated here is
/// everything either side of it: the double-click that RAISES the request with the right pin and the
/// right (1-based) number, the validation rules the OK button is bound to, and the commit that applies
/// the answer. Those are the three places a defect could actually live.</para>
/// </summary>
public sealed class SymbolPinPortEditTests
{
    private static SymbolEditorViewModel MakeVm(out EditableSymbol sym)
    {
        sym = new EditableSymbol { UserEditable = true };
        sym.Primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                             -100, 0, 100, 0));
        sym.Pins.Add(new SymbolPin(-100, 0, 0));   // port 1, sitting ON the line's left end
        sym.Pins.Add(new SymbolPin(100, 0, 1));    // port 2
        return new SymbolEditorViewModel(sym);
    }

    // ── The double-click ──────────────────────────────────────────────────────

    [Fact]
    public void DoubleClickingAPin_RaisesTheRequest_WithThatPinAndItsOneBasedPortNumber()
    {
        var vm = MakeVm(out _);
        SymbolEditorViewModel.PinPortEditRequest? seen = null;
        vm.PinPortEditRequested += r => seen = r;

        vm.OnPointerPressed(100, 0, KeyModifiers.None, clickCount: 2);

        Assert.NotNull(seen);
        Assert.Equal(1, seen!.Value.PinIndex);
        Assert.Equal(2, seen.Value.PortNumber);   // 1-BASED — PortIndex 1 is port 2
    }

    /// <summary>
    /// A real double-click delivers TWO presses — ClickCount 1, then ClickCount 2 — and only the second
    /// may raise. Gated because the alternative explanation for the owner's "I am forced to press cancel
    /// twice" was two dialogs stacked on top of each other, and a fix aimed at pointer capture would do
    /// nothing about that. It raises once.
    /// </summary>
    [Fact]
    public void ARealDoubleClick_RaisesTheRequestExactlyOnce()
    {
        var vm = MakeVm(out _);
        int raises = 0;
        vm.PinPortEditRequested += _ => raises++;

        vm.OnPointerPressed(100, 0, KeyModifiers.None, clickCount: 1);
        vm.OnPointerReleased(100, 0);
        vm.OnPointerPressed(100, 0, KeyModifiers.None, clickCount: 2);

        Assert.Equal(1, raises);
    }

    /// <summary>The press that opens a modal leaves no drag armed behind it — the matching release is
    /// delivered to the dialog, so nothing else would ever disarm one.</summary>
    [Fact]
    public void TheDoubleClickThatOpensTheDialog_LeavesNoPinDragArmed()
    {
        var vm = MakeVm(out var sym);
        vm.OnPointerPressed(100, 0, KeyModifiers.None, clickCount: 1);
        vm.OnPointerPressed(100, 0, KeyModifiers.None, clickCount: 2);

        // A pointer move that would otherwise be a drag must not move the pin.
        double before = sym.Pins[1].LocalX;
        vm.OnPointerMoved(700, 0, leftDown: true);

        Assert.Equal(before, sym.Pins[1].LocalX);
        Assert.Equal((0.0, 0.0), vm.Overlay.PinLiveDragOffset);
    }

    [Fact]
    public void DoubleClickingAPin_SelectsIt_SoTheInspectorAndTheDialogAgree()
    {
        var vm = MakeVm(out _);
        vm.OnPointerPressed(100, 0, KeyModifiers.None, clickCount: 2);

        Assert.Equal(1, vm.Overlay.SelectedPinIndex);
        Assert.Empty(vm.Overlay.SelectedIndices);   // the line underneath is NOT also selected
    }

    /// <summary>A pin sits on top of the artwork it is attached to. Hit-testing primitives first —
    /// which is what the pre-existing text-primitive double-click branch did — would let the artwork
    /// win every double-click aimed at a pin.</summary>
    [Fact]
    public void DoubleClickingAPin_BeatsThePrimitiveUnderneath()
    {
        var vm = MakeVm(out _);
        bool textEditAsked = false;
        vm.TextEditRequested += _ => textEditAsked = true;
        SymbolEditorViewModel.PinPortEditRequest? seen = null;
        vm.PinPortEditRequested += r => seen = r;

        vm.OnPointerPressed(-100, 0, KeyModifiers.None, clickCount: 2);   // pin 0 lies on the line

        Assert.NotNull(seen);
        Assert.Equal(0, seen!.Value.PinIndex);
        Assert.False(textEditAsked);
    }

    [Fact]
    public void DoubleClickingEmptyCanvas_RaisesNothing()
    {
        var vm = MakeVm(out _);
        bool asked = false;
        vm.PinPortEditRequested += _ => asked = true;

        vm.OnPointerPressed(4000, 4000, KeyModifiers.None, clickCount: 2);

        Assert.False(asked);
    }

    [Fact]
    public void DoubleClickingAPin_OnALockedSymbol_RaisesNothing()
    {
        var vm = MakeVm(out _);
        vm.IsLocked = true;
        bool asked = false;
        vm.PinPortEditRequested += _ => asked = true;

        vm.OnPointerPressed(100, 0, KeyModifiers.None, clickCount: 2);

        Assert.False(asked);
    }

    [Fact]
    public void TheRequestCarriesTheCellsDeclaredPortCount_OrNullForAnOrphanSymbol()
    {
        var vm = MakeVm(out _);
        SymbolEditorViewModel.PinPortEditRequest? seen = null;
        vm.PinPortEditRequested += r => seen = r;

        vm.OnPointerPressed(100, 0, KeyModifiers.None, clickCount: 2);
        Assert.Null(seen!.Value.PortCount);   // no .ccell behind this symbol

        vm.SetExternalPortCount(3);
        vm.OnPointerPressed(100, 0, KeyModifiers.None, clickCount: 2);
        Assert.Equal(3, seen!.Value.PortCount);
    }

    // ── The commit ────────────────────────────────────────────────────────────

    [Fact]
    public void SetPinPortNumber_WritesTheZeroBasedIndex_AndIsUndoable()
    {
        var vm = MakeVm(out var sym);

        vm.SetPinPortNumber(1, 5);
        Assert.Equal(4, sym.Pins[1].PortIndex);   // 1-based in, 0-based stored

        vm.UndoRedo.Undo();
        Assert.Equal(1, sym.Pins[1].PortIndex);
    }

    [Fact]
    public void SetPinPortNumber_RejectsOutOfRangeInputRatherThanClamping()
    {
        var vm = MakeVm(out var sym);

        vm.SetPinPortNumber(1, 0);    // 0 is not a port number
        vm.SetPinPortNumber(1, -3);
        vm.SetPinPortNumber(9, 2);    // no such pin
        vm.SetPinPortNumber(-1, 2);

        Assert.Equal(1, sym.Pins[1].PortIndex);   // untouched — never clamped to port 1
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void SetPinPortNumber_OnALockedSymbol_DoesNothing()
    {
        var vm = MakeVm(out var sym);
        vm.IsLocked = true;

        vm.SetPinPortNumber(1, 7);

        Assert.Equal(1, sym.Pins[1].PortIndex);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void SetPinPortNumber_ToTheSameNumber_IsNotAnUndoEntry()
    {
        var vm = MakeVm(out _);
        vm.SetPinPortNumber(1, 2);   // already port 2
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── The text validation ───────────────────────────────────────────────────

    [Theory]
    [InlineData("1", 1)]
    [InlineData("42", 42)]
    [InlineData("  7  ", 7)]      // surrounding whitespace is not the user's mistake
    [InlineData("007", 7)]
    [InlineData("9999", 9999)]    // the cap itself is allowed
    public void Validate_AcceptsAWholeNumber(string text, int expected)
    {
        var r = SymbolPinPortInput.Validate(text);
        Assert.True(r.IsValid);
        Assert.Equal(expected, r.PortNumber);
        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("2a")]
    [InlineData("2.0")]           // a port number is an ordinal, not a measurement
    [InlineData("2.7")]
    [InlineData("+2")]
    [InlineData("-2")]
    [InlineData("0")]
    [InlineData("1,000")]
    [InlineData("1e3")]
    [InlineData("10000")]         // past the cap
    [InlineData("99999999999999999999")]   // would overflow int
    public void Validate_RejectsAnythingElse_AndSaysWhy(string? text)
    {
        var r = SymbolPinPortInput.Validate(text);
        Assert.False(r.IsValid);
        Assert.Equal(0, r.PortNumber);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    /// <summary>An overflowing number and a misspelled one need different corrections, so they must
    /// not share a message. Reported as too large rather than as "not a number".</summary>
    [Fact]
    public void Validate_ReportsAnOverlargeNumberAsTooLarge_NotAsNotANumber()
    {
        var r = SymbolPinPortInput.Validate("99999999999999999999");
        Assert.Contains("or less", r.Error);
    }

    [Fact]
    public void Validate_NotesAPortPastTheCellsDeclaredCount_ButStillAcceptsIt()
    {
        var r = SymbolPinPortInput.Validate("5", declaredPortCount: 2);
        Assert.True(r.IsValid);          // authoring a symbol before its .ccell agrees is ordinary
        Assert.Equal(5, r.PortNumber);
        Assert.Null(r.Error);
        Assert.False(string.IsNullOrWhiteSpace(r.Note));

        Assert.Null(SymbolPinPortInput.Validate("2", declaredPortCount: 2).Note);
        Assert.Null(SymbolPinPortInput.Validate("5", declaredPortCount: null).Note);
    }
}
