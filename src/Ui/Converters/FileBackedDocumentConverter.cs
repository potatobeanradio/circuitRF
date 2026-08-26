using System;
using System.Globalization;
using Avalonia.Data.Converters;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// True when the bound dockable is a document that has been saved to a file — i.e. when
/// "Reveal in Finder/Explorer" has something to point at. Whether that file is still THERE is
/// FileReveal's business (it is a no-op if not); the menu offers the entry on a saved document.
///
/// Bound from <c>Styles/DocumentTabContextMenu.axaml</c> to hide the Reveal item rather than
/// disable it: a scratch document has never been saved, so the entry is not "temporarily
/// unavailable", it does not apply. Keeping this a converter (rather than a <c>FilePath</c> path
/// binding) lets the menu stay <c>x:CompileBindings="True"</c> against <c>IDockable</c>, which is
/// what the dockable actually is.
/// </summary>
public sealed class FileBackedDocumentConverter : IValueConverter
{
    public static readonly FileBackedDocumentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is IFileBackedDocument { FilePath.Length: > 0 };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
