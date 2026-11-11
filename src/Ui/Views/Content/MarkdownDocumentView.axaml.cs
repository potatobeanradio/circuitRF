using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media.Imaging;
using CircuitRF.Ui.Markdown;

namespace CircuitRF.Ui.Views.Content;

/// <summary>
/// The view for <see cref="MarkdownDocument"/>. It renders and does nothing else — no parse (that is
/// <see cref="SmallMarkdown"/>, which the document already ran), no file access beyond decoding the
/// pictures the document resolved, and no editing of any kind.
/// </summary>
public partial class MarkdownDocumentView : UserControl
{
    /// <summary>The text block's <c>MaxWidth</c> in the XAML: the widest a picture is drawn.</summary>
    private const double ColumnWidth = 820;

    /// <summary>The text block's left and right margins together, in the XAML.</summary>
    private const double SideMargins = 56;

    /// <summary>The bitmaps the current page decoded, released when it is rebuilt or detached.</summary>
    private readonly List<IDisposable> _bitmaps = [];

    public MarkdownDocumentView()
    {
        InitializeComponent();
        // A picture is held to the column the text wraps in, which narrows with the window; the text
        // block does not do that for an inline control, which it measures at its own size.
        BodyScroll.SizeChanged += (_, _) => FitPictures();
    }

    /// <summary>
    /// Built on DataContext rather than in the constructor: Dock hands a document view its document
    /// after construction, and it re-uses a view when a tab is re-activated, so a one-shot build in
    /// the constructor renders an empty page for every tab after the first.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        BodyText.Inlines?.Clear();
        ReleaseBitmaps();
        if (DataContext is MarkdownDocument doc)
            MarkdownInlines.Append(BodyText.Inlines, doc.Lines, MarkdownInlines.DocumentBodyFontSize,
                                   ColumnWidth, _bitmaps);
        FitPictures();
    }

    private void FitPictures()
    {
        if (BodyText.Inlines is null) return;
        double viewport = BodyScroll.Bounds.Width;
        double column   = viewport > SideMargins ? Math.Min(ColumnWidth, viewport - SideMargins) : ColumnWidth;
        foreach (Inline inline in BodyText.Inlines)
            if (inline is InlineUIContainer { Child: Image { Source: Bitmap bitmap } image })
                image.MaxWidth = Math.Min(column, bitmap.Size.Width);
    }

    private void ReleaseBitmaps()
    {
        foreach (IDisposable d in _bitmaps) d.Dispose();
        _bitmaps.Clear();
    }
}
