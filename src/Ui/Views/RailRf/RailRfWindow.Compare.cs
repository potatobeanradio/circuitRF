// The Compare… button (§2.5, brief 16; owner, 2026-09-19).
//
// ── FOUR STEPS, AND THIS FILE OWNS NONE OF THEM ─────────────────────────────────────────────────
//
//  1. ASK for the reference `.crail`. A comparison is against a design that is known to work, and
//     railRF has no way to guess which one — this is the whole of the button's ellipsis.
//  2. RESOLVE AND SOLVE it, through a second `RailRfViewModel` over that document. See
//     `RailRfViewModel.Compare.cs`'s header for why the reference side is a view model and not a
//     second resolve-and-solve path.
//  3. PAIR the two, `RailComparison.Match` — by refdes and pin, never by index.
//  4. BUILD the report, `RailComparisonReport.Build`, and show it.
//
// Every one of those lives in `src/Design`. What is here is the dialog, the progress and the
// refusals.
//
// ── BOTH SIDES RUN AT THE SAME MODEL KIND ───────────────────────────────────────────────────────
//
// Whatever is on screen. A fast reading of one board held against a meshed reading of another
// measures the two extractors and reports the difference as a property of the boards — which is
// the one mistake a comparison must not make, and it would look entirely normal.
//
// ── AND THE RAIL IS THE ONE BEING LOOKED AT ─────────────────────────────────────────────────────
//
// §6's scope: `+1V8` against `+1V8`. There is no cross-rail comparison, because comparing two
// different rails is not a question this answers — `RailComparison.Match` refuses by name and says
// which rails each design declares, which is a better sentence than anything this file could
// compose.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow
{
    private void WireCompareButton() =>
        CompareButton.Click += (_, _) => OnCompareClicked();

    /// <summary>
    /// <b>R-rail23-3b — Compare now has two sides to offer, so it asks which.</b>
    /// </summary>
    /// <remarks>
    /// <b>With no baseline it goes straight to the file picker</b>, exactly as it always did: an
    /// extra menu with one row on it is a click spent saying what the ellipsis already said. The
    /// menu appears the moment there is a previous run to point at, which is the second run of any
    /// session.
    ///
    /// <para>The pin lives here too (R-rail23-3d). It is a statement about which result is worth
    /// measuring against, so it belongs beside the thing that measures against it rather than on a
    /// toolbar of its own — and it is the only control in this feature that is not a comparison.</para>
    /// </remarks>
    private async void OnCompareClicked()
    {
        if (Vm is not { HasBaseline: true } vm)
        {
            await CompareAsync();
            return;
        }

        var previous = new MenuItem { Header = vm.BaselineText };
        previous.Click += (_, _) => ComparePrevious();

        var other = new MenuItem { Header = "Another .crail…" };
        other.Click += async (_, _) => await CompareAsync();

        var pin = new MenuItem
        {
            Header = vm.IsBaselinePinned ? "Unpin this result" : "Pin this result",
            IsEnabled = vm.CanPinBaseline,
        };
        ToolTip.SetTip(pin,
            "Hold the run on screen as the thing to compare against, across further runs — so "
          + "\"unmount, run, unmount another, run\" measures each cut against the fitted board "
          + "rather than against the previous cut.");
        pin.Click += (_, _) => vm.PinBaselineCommand.Execute(null);

        var flyout = new MenuFlyout();
        flyout.Items.Add(previous);
        flyout.Items.Add(other);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(pin);
        flyout.ShowAt(CompareButton);
    }

    /// <summary>
    /// The comparison the designer's own loop asks for: this run against the last one.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is re-solved and nothing is read from disk.</b> Both sides are answers this window
    /// already produced, so this is the one comparison that costs no time at all — which is the
    /// whole reason the loop <i>change one thing, re-run, compare</i> is worth having.
    /// </remarks>
    private void ComparePrevious()
    {
        if (Vm is not { } vm) return;

        if (vm.CompareAgainstBaseline() is not { } report)
        {
            vm.Refusal = new RailRefusal(
                "There is no previous run of this rail to compare against. Run it, change "
              + "something — unmount a part, retype a mounting loop — and run it again; "
              + "the run before the change is what this compares with.",
                RailRefusalControl.None);
            return;
        }

        var dialog = new RailCompareDialog(
            report,
            vm.BoardLayout?.Model,
            vm.Board?.Technology,
            vm.BoardOverlayLayer.Scene,
            vm.ExportProvenance()?.Lines ?? []);

        _ = dialog.ShowDialog(this);
    }

    private async Task CompareAsync()
    {
        if (Vm is not { } vm || StorageProvider is not { } sp) return;

        if (vm.CurrentSide() is not { } target || vm.SelectedRail is not { } rail)
        {
            vm.Refusal = new RailRefusal(
                "Nothing has been solved on this design yet, so there is nothing to compare. Run "
              + "the rail first — a comparison holds one answer against another.",
                RailRefusalControl.None);
            return;
        }

        var picked = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Compare against — the design that is known to work",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("railRF document") { Patterns = ["*" + RailDocumentIo.Extension] },
            ],
        });
        if (picked.Count == 0) return;

        string path = picked[0].Path.LocalPath;

        // Comparing a document with itself answers nothing and reads as a working comparison in
        // which nothing has changed, which is the most misleading of all the possible answers.
        if (vm.DocumentPath is { Length: > 0 } mine &&
            string.Equals(Path.GetFullPath(mine), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            vm.Refusal = new RailRefusal(
                "That is this document. A comparison is of two designs — pick the one that is "
              + "known to work, not the one being judged.",
                RailRefusalControl.None);
            return;
        }

        RailComparisonReport report;
        try
        {
            report = await Task.Run(() => Build(vm, rail.Name, target, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or NotSupportedException)
        {
            vm.Refusal = new RailRefusal(
                $"The reference design '{Path.GetFileName(path)}' did not read: {ex.Message}",
                RailRefusalControl.None);
            return;
        }

        var dialog = new RailCompareDialog(
            report,
            vm.BoardLayout?.Model,
            vm.Board?.Technology,
            vm.BoardOverlayLayer.Scene,
            vm.ExportProvenance()?.Lines ?? []);

        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// The reference side, and the pairing — <b>off the UI thread</b>, because it is a whole second
    /// resolve and solve of another board and that is seconds, not milliseconds.
    /// </summary>
    /// <remarks>
    /// The second view model is constructed HERE rather than being handed in: it is a working
    /// object with a lifetime of one comparison, it is never shown, and nothing outside this method
    /// may hold a reference to it — a view model over a document nobody has open is exactly the
    /// thing that goes stale if it is kept.
    /// </remarks>
    private static RailComparisonReport Build(
        RailRfViewModel vm, string railName, RailComparisonSide target, string referencePath)
    {
        var referenceDoc = RailDocumentIo.LoadFromFile(referencePath);

        var referenceVm = new RailRfViewModel(referenceDoc, referencePath);
        referenceVm.LoadDocumentReferences();

        // The SAME model kind on both sides — this file's own header says why.
        var kind = vm.ResultsModelKind ?? Design.Layout.Pdn.PdnModelKind.Fast;
        var reference = referenceVm.SolveOnce(kind, railName) ?? new RailComparisonSide();

        var match = RailComparison.Match(
            referenceDoc, vm.Document, railName,
            Name(referencePath, referenceDoc), Name(vm.DocumentPath, vm.Document));

        return RailComparisonReport.Build(match, reference, target);

        static string Name(string? path, RailDocument doc) =>
            doc.Name is { Length: > 0 } n ? n
            : path is { Length: > 0 } p ? Path.GetFileNameWithoutExtension(p)
            : "(unnamed)";
    }
}
