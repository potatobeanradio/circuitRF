using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// The five editor toolbars — Schematic, Symbol, Layout, Data Display and wBond — captured as
/// figures AND as a machine-readable manifest, from the same live control.
///
/// <para><b>Why a manifest and not a hand-written table.</b> The documentation lists these toolbars
/// button by button. A hand-typed table goes out of order the first time somebody inserts a button,
/// and nothing reports it: the picture and the prose simply stop agreeing. Reading the ordered
/// button list, each button's tooltip and each button's icon off the SAME control the figure was
/// rendered from makes that class of drift unrepresentable — the table and the picture cannot
/// disagree, because they are the same traversal.</para>
///
/// <para>Each toolbar is found by the <c>DocsToolbar</c> name its view declares. That is deliberate:
/// finding it by shape ("the first docked panel holding three or more buttons") would silently pick
/// a different panel after a refactor and produce a confident, wrong figure.</para>
/// </summary>
public static class ToolbarCatalog
{
    /// <summary>The name every documented toolbar panel carries in its view's XAML.</summary>
    public const string PanelName = "DocsToolbar";

    /// <summary>One documented toolbar.</summary>
    /// <param name="Id">File stem and <c>{{toolbar: …}}</c> key. Ids are a contract; renaming breaks a page.</param>
    /// <param name="Title">Human name used in the figure caption.</param>
    /// <param name="Width">The parent width the toolbar is bounded to before layout — there is no natural size.</param>
    /// <param name="Height">
    /// The captured height. Stated rather than measured for the same reason as the width, and
    /// generous enough for the wrapping toolbars (Layout and wBond use a WrapPanel, so at a narrower
    /// width they reflow onto a second row rather than scrolling).
    /// </param>
    public readonly record struct Row(string Id, string Title, int Width, int Height);

    public static readonly IReadOnlyList<Row> Catalog =
    [
        new("schematic",    "Schematic editor",   1180,  40),
        new("symbol",       "Symbol editor",      1180,  40),
        new("layout",       "Layout editor",      1180,  84),
        new("datadisplay",  "Data Display",       1180,  44),
        new("wbond",        "wBond editor",       1180,  84),
    ];

    // ── The manifest ──────────────────────────────────────────────────────────

    /// <summary>One toolbar entry, in the order the toolbar presents it.</summary>
    /// <param name="Index">
    /// 1-based number drawn beside the item in the indexed figure and printed in the per-button
    /// table. <b>Zero for a separator</b>: a separator is not a button, and numbering the gaps makes
    /// the prose count wrong the first time somebody reads it out.
    /// </param>
    /// <param name="Slot">Position in the panel's children — how the callout finds the item to sit under.</param>
    /// <param name="Id">The control's <c>x:Name</c>, or an empty string when the button is unnamed.</param>
    /// <param name="Tooltip">The button's ToolTip.Tip text. Empty is a UI bug, not a blank table cell.</param>
    /// <param name="Icon">The icon it shows: a Material icon kind, "path" for vector artwork, or a symbol kind.</param>
    /// <param name="Command">The bound command's type name, or null — most of these buttons use a Click handler.</param>
    /// <param name="Kind">"button", "toggle", "combo", "text" or "separator".</param>
    public sealed record Entry(int Index, int Slot, string Id, string Tooltip, string Icon, string? Command, string Kind);

    /// <summary>Walk <paramref name="toolbar"/> in presentation order and describe every item on it.</summary>
    public static IReadOnlyList<Entry> Manifest(Panel toolbar)
    {
        var entries = new List<Entry>();
        int number = 0, slot = 0;
        foreach (var child in toolbar.Children)
        {
            // A collapsed child is not on the toolbar the reader is looking at. Listing it would put
            // a number in the table with nothing under it in the figure — and, because a collapsed
            // control arranges to a zero rectangle at the panel's origin, every one of them would
            // stack its callout on top of the first button. Measured: the Layout toolbar carries ten
            // state-dependent items that are collapsed for a fresh document.
            if (!child.IsVisible || child.Bounds.Width <= 0 || child.Bounds.Height <= 0) { slot++; continue; }

            var probe = Describe(child, 0, slot);
            if (probe is not null)
            {
                bool numbered = probe.Kind != "separator";
                if (numbered) number++;
                entries.Add(probe with { Index = numbered ? number : 0 });
            }
            slot++;
        }
        return entries;
    }

    private static Entry? Describe(Control c, int index, int slot)
    {
        switch (c)
        {
            case Separator:
                return new Entry(index, slot, NameOf(c), "", "separator", null, "separator");

            // A 1px Border with no child is how these toolbars draw a separator.
            case Border { Child: null }:
                return new Entry(index, slot, NameOf(c), "", "separator", null, "separator");

            case ToggleButton tb:
                return new Entry(index, slot, NameOf(tb), Tooltip(tb), IconOf(tb), CommandOf(tb), "toggle");

            case Button b:
                return new Entry(index, slot, NameOf(b), Tooltip(b), IconOf(b), CommandOf(b), "button");

            case ComboBox cb:
                return new Entry(index, slot, NameOf(cb), Tooltip(cb), "combo", null, "combo");

            case TextBlock t:
                return new Entry(index, slot, NameOf(t), t.Text ?? "", "text", null, "text");

            // A layout wrapper (a StackPanel holding a label plus a value) is one item as far as the
            // reader is concerned; describe it by its text rather than descending into it.
            case Panel p:
                return new Entry(index, slot, NameOf(p), string.Join(" ",
                    p.GetLogicalDescendants().OfType<TextBlock>().Select(x => x.Text).Where(x => !string.IsNullOrEmpty(x))),
                    "text", null, "text");

            default:
                return new Entry(index, slot, NameOf(c), Tooltip(c), c.GetType().Name, null, "text");
        }
    }

    private static string NameOf(Control c) => c.Name ?? "";

    private static string Tooltip(Control c) => ToolTip.GetTip(c) as string ?? "";

    private static string? CommandOf(Control c)
        => c is Button { Command: { } cmd } ? cmd.GetType().Name : null;

    /// <summary>
    /// What the button shows. Material.Icons exposes its kind as an enum property; our own palette
    /// glyphs name a <see cref="Schematic.SymbolKind"/>; anything else is vector artwork.
    /// </summary>
    private static string IconOf(Control c)
    {
        foreach (var d in c.GetLogicalDescendants().OfType<Control>())
        {
            var t = d.GetType();
            if (t.Name == "MaterialIcon")
            {
                var kind = t.GetProperty("Kind")?.GetValue(d);
                if (kind is not null) return kind.ToString() ?? "icon";
            }
            if (t.Name == "PaletteGlyphControl")
            {
                var kind = t.GetProperty("Kind")?.GetValue(d);
                if (kind is not null) return "symbol:" + kind;
            }
            if (d is Avalonia.Controls.Shapes.Path) return "path";
        }
        return c is ContentControl { Content: string s } ? "text:" + s : "none";
    }

    /// <summary>The manifest as the JSON the page generator reads (<c>toolbar-schematic.json</c>).</summary>
    public static string ToJson(string id, string title, IReadOnlyList<Entry> entries)
        => JsonSerializer.Serialize(new
        {
            regenerate = UiArtworkGenerator.RegenerateCommand,
            id,
            title,
            buttons = entries,
        }, new JsonSerializerOptions { WriteIndented = true });

    // ── The indexed variant ───────────────────────────────────────────────────

    /// <summary>
    /// Wraps <paramref name="toolbar"/> with a numbered callout under each item, so prose can say
    /// "3 - Rotate" and stay correct when a button is inserted: the numbers come from the same
    /// traversal that produced the manifest, so they renumber themselves.
    /// </summary>
    /// <remarks>
    /// <para>The callouts are positioned from each child's arranged bounds rather than laid out in a
    /// parallel strip — the buttons are not equal width (a combo box is 200 px, an icon button is
    /// ~28), so a strip of evenly spaced numbers would point at the wrong buttons and look right
    /// while doing it. A <see cref="Layoutable.LayoutUpdated"/> handler re-reads the bounds, which
    /// is what makes the numbers follow a re-layout instead of being baked in at construction.</para>
    ///
    /// <para><b>Both coordinates, not just X.</b> The Layout and wBond toolbars are WrapPanels and
    /// genuinely reflow onto a second row; a strip under the toolbar put row-two's numbers on top of
    /// row-one's, out of order, and looked deliberate. Each callout sits directly under its own item,
    /// and the panel's line spacing is opened up for the indexed variant to leave room for them —
    /// this figure exists to be read, and giving the numbers somewhere to live is what a printed
    /// figure would do.</para>
    /// </remarks>
    public static Control WithCallouts(Panel toolbar, IReadOnlyList<Entry> entries, ColorVariant variant,
                                       double toolbarHeight)
    {
        const double Dot = 18;

        // Room for a row of numbers under every row of buttons.
        if (toolbar is WrapPanel wrap) wrap.LineSpacing = Math.Max(wrap.LineSpacing, Dot + 8);

        // Hold the toolbar to the SAME height the plain figure captures it at, pinned to the top.
        // Left to fill the taller indexed frame, every separator — a stretched one-pixel Border —
        // grows with it and runs down past the buttons through the row of numbers, which reads as a
        // rendering fault rather than as a group divider (owner: "the vertical bar spacings within
        // the svg are too tall").
        toolbar.Height = toolbarHeight;
        toolbar.VerticalAlignment = VerticalAlignment.Top;

        var callouts = new Canvas { ClipToBounds = false };
        var dots = new List<(Border Dot, int Slot)>();

        foreach (var e in entries)
        {
            if (e.Index == 0) continue;   // separators carry no number
            var dot = CalloutDot.Build(e.Index, variant, Dot);
            dots.Add((dot, e.Slot));
            callouts.Children.Add(dot);
        }

        void Place(object? _, EventArgs __)
        {
            var kids = toolbar.Children;
            foreach (var (dot, slot) in dots)
            {
                if (slot >= kids.Count) continue;
                var b = kids[slot].Bounds;
                Canvas.SetLeft(dot, Math.Max(0, b.Center.X - Dot / 2));
                Canvas.SetTop(dot, b.Bottom + 2);
            }
        }

        toolbar.LayoutUpdated += Place;

        // One cell: the callouts overlay the toolbar so a number can sit under the row it belongs
        // to, not under the whole panel.
        var overlay = new Grid();
        overlay.Children.Add(toolbar);
        overlay.Children.Add(callouts);
        return overlay;
    }
}
