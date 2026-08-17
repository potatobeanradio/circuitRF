using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The parameterised wire transforms (wbond.md §6.4) — mirror, duplicate-with-pitch, bend, extend.
///
/// <para>One dialog with four modes rather than four dialogs: they share a subject (the current
/// selection), a unit convention, and an Apply, and four windows would be four places for those to
/// drift. The transforms that need no parameters — straighten, re-apply profile, reverse — are
/// toolbar buttons instead, because a dialog whose only content is a confirm button is friction.</para>
///
/// <para>Distances are typed in the editor's OWN display unit (§6.5), which is independent of the
/// layout's — the readout beside each field states which, so a number is never ambiguous.</para>
/// </summary>
public partial class WBondTransformDialog : Window
{
    private readonly WBondViewModel? _vm;
    private readonly WBondUnit _unit;

    // Parameterless ctor satisfies the Avalonia XAML resource loader.
    public WBondTransformDialog() : this(null, WBondUnit.Mil) { }

    public WBondTransformDialog(WBondViewModel? vm, WBondUnit unit)
    {
        _vm = vm;
        _unit = unit;

        InitializeComponent();

        string suffix = unit.ToString().ToLowerInvariant();
        PitchUnitX.Text = suffix;
        PitchUnitY.Text = suffix;
        BendUnitX.Text = suffix;
        BendUnitY.Text = suffix;
        BendUnitZ.Text = suffix;

        int wires = _vm?.Selection.TouchedWires().Count ?? 0;
        // The count alone, matching the Group Wires As dialog (owner, 2026-08-16). "Nothing selected"
        // keeps its sentence: there the point IS that there is nothing, not how many.
        SelectionText.Text = wires switch
        {
            0 => "Nothing selected.",
            1 => "1 wire",
            _ => wires.ToString(CultureInfo.InvariantCulture) + " wires",
        };
    }

    /// <summary>Shows the dialog; returns how many wires the applied transform touched (0 on cancel).</summary>
    public static async Task<int> ShowAsync(Window? owner, WBondViewModel vm, WBondUnit unit)
    {
        var dialog = new WBondTransformDialog(vm, unit);
        return owner is null ? 0 : await dialog.ShowDialog<int>(owner);
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        if (MirrorPanel is null) return;   // fires once during construction, before the tree is built

        MirrorPanel.IsVisible = MirrorMode.IsChecked == true;
        DuplicatePanel.IsVisible = DuplicateMode.IsChecked == true;
        BendPanel.IsVisible = BendMode.IsChecked == true;
        ExtendPanel.IsVisible = ExtendMode.IsChecked == true;
        ErrorText.IsVisible = false;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(0);

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) { Close(0); return; }

        try
        {
            int touched =
                MirrorMode.IsChecked == true ? ApplyMirror()
                : DuplicateMode.IsChecked == true ? ApplyDuplicate()
                : BendMode.IsChecked == true ? ApplyBend()
                : ApplyExtend();

            Close(touched);
        }
        catch (FormatException)
        {
            ErrorText.Text = "Enter a number in each distance field.";
            ErrorText.IsVisible = true;
        }
    }

    private int ApplyMirror()
    {
        char axis = MirrorX.IsChecked == true ? 'x' : MirrorY.IsChecked == true ? 'y' : 'z';

        // Mirrored about the selection's own centre, which is what makes the operation feel local:
        // mirroring about the origin would fling a selection across the package.
        var centre = SelectionCentre();
        long about = axis switch { 'x' => centre.X, 'y' => centre.Y, _ => centre.Z };

        return _vm!.MirrorSelection(axis, about, ReverseTraversalCheck.IsChecked == true);
    }

    private int ApplyDuplicate()
    {
        int first = _vm!.Selection.TouchedWires().FirstOrDefault(-1);
        if (first < 0) return 0;

        return _vm.DuplicateWithPitch(first, ParseNm(PitchXBox.Text), ParseNm(PitchYBox.Text),
                                      (int)(CountUpDown.Value ?? 1));
    }

    private int ApplyBend() =>
        _vm!.BendSelection(ParseNm(BendXBox.Text), ParseNm(BendYBox.Text), ParseNm(BendZBox.Text));

    private int ApplyExtend() =>
        _vm!.ExtendSelection((double)(ExtendFactorUpDown.Value ?? 1m), ExtendFromOutput.IsChecked == true);

    private Point3 SelectionCentre()
    {
        var wires = _vm!.Design.AllWires().ToList();
        long minX = long.MaxValue, maxX = long.MinValue;
        long minY = long.MaxValue, maxY = long.MinValue;
        long minZ = long.MaxValue, maxZ = long.MinValue;
        bool any = false;

        foreach (int index in _vm.Selection.TouchedWires())
        {
            if (index < 0 || index >= wires.Count) continue;
            foreach (var p in wires[index].Points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
                any = true;
            }
        }

        return any ? new Point3((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2) : default;
    }

    private long ParseNm(string? text) =>
        WBondUnits.ToNm(double.Parse((text ?? "").Trim(), CultureInfo.InvariantCulture), _unit);
}
