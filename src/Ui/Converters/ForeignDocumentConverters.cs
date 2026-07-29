using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// brief-foreign-documents.md §4 item 3 (tab header tint) / R-fgn-7: maps a document's
/// <c>IsForeign</c> flag to a background tint for its tab header — amber, matching the edge band's
/// own <c>CrfForeignBandBrush</c> color exactly (never red, which means error), and transparent for
/// every other (workspace-bound, or non-foreign-aware) dockable. Bound against
/// <c>Dock.Model.Core.IDockable</c> in the shared <c>DocumentControl.HeaderTemplate</c> override in
/// App.axaml, where the runtime item may or may not actually declare <c>IsForeign</c> — a document
/// type with no such property (SchematicDocument, SymbolEditorDocument, …) simply binds null here and
/// renders untinted, exactly like a workspace-bound LayoutDocument.
/// </summary>
public class ForeignDocumentTintConverter : IValueConverter
{
    public static readonly ForeignDocumentTintConverter Instance = new();

    private static readonly IBrush Tinted     = new SolidColorBrush(Color.FromArgb(0x33, 0xE8, 0xA3, 0x3D));
    private static readonly IBrush Untinted   = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Tinted : Untinted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
