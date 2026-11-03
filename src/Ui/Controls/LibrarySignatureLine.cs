using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// A signed library's line on its row (brief vendorlib-15 §5c): the verdict's sentence, and for a key this user has
/// not trusted, Trust / Not now. Shared by Add Vendor Library and Manage Libraries so the two rows read alike.
/// Null for an unsigned library — it shows nothing, as today. Trusting writes the per-user trust store only, never
/// the workspace.
/// </summary>
public static class LibrarySignatureLine
{
    public static Control? For(LibrarySignatureInspection inspection, Action? trusted = null)
    {
        var verdict = UserVendorLibraries.Judge(inspection);
        if (verdict.RowText is not { } text) return null;

        bool bad = verdict.State is LibrarySignatureState.DoesNotVerify or LibrarySignatureState.TrustedNameDifferentKey;
        var line = new SelectableTextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        if (bad && Avalonia.Application.Current?.TryFindResource("CrfWarningBrush", out var b) == true && b is IBrush brush)
            line.Foreground = brush;
        if (!verdict.AsksForTrust) return line;

        var trust = new Button { Content = "Trust", FontSize = 11, Padding = new Avalonia.Thickness(8, 2) };
        var later = new Button { Content = "Not now", FontSize = 11, Padding = new Avalonia.Thickness(8, 2) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { trust, later } };
        ToolTip.SetTip(trust, $"Trust {verdict.Publisher} with the key {verdict.Fingerprint}, for this user on this machine.");
        trust.Click += (_, _) =>
        {
            UserVendorLibraries.TrustPublisher(verdict.Publisher, verdict.Fingerprint);
            line.Text = UserVendorLibraries.Judge(inspection).RowText;
            buttons.IsEnabled = false;
            trusted?.Invoke();
        };
        // Not now leaves the trust store as it was; the buttons stay where they are, greyed, so nothing moves.
        later.Click += (_, _) => buttons.IsEnabled = false;
        return new StackPanel { Spacing = 4, Children = { line, buttons } };
    }
}
