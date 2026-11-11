using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// <b>Brings an item of a <see cref="ListBox"/> to the top of its pane with an eased scroll</b>, the way the .ctech
/// stackup's card list follows a click on the cross-section, and the RF Chain Budget's stage list a click on a block.
///
/// <para>The list's own <c>AutoScrollToSelectedItem</c> must be off: it posts a <c>ScrollIntoView</c> that jumps, and
/// a jump is exactly what this exists to replace. The scroll is <see cref="ScrollOffsetAnimator"/>'s; this class only
/// says where to: nowhere when the item is already wholly on screen (a click on something already in view moves
/// nothing), otherwise the offset that puts the item's TOP at the top of the pane (<c>ScrollIntoView</c> brings an item
/// minimally into view, which for an item taller than the pane leaves its first rows scrolled away). An item whose
/// container is not realised yet sets out on an estimate and the animator re-aims from the realised container on
/// every frame, so the list is never moved anywhere it is not going (a jump de-realises the viewport and flashes it
/// blank).</para>
///
/// <para>The caller posts the call at Background priority after the selection changes, so the list has arranged.</para>
/// </summary>
internal static class ListBoxEasedScroll
{
    /// <summary>The <see cref="ScrollViewer"/> inside <paramref name="list"/>'s template, or null before it is applied.</summary>
    internal static ScrollViewer? ScrollerOf(ListBox list)
        => list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>Whether <paramref name="item"/>'s container is realised and wholly inside the viewport. False for an
    /// item taller than the pane, which can never be wholly inside one and therefore always wants its top at the top.</summary>
    internal static bool IsFullyVisible(ListBox list, object item, ScrollViewer scroll)
    {
        if (list.ContainerFromItem(item) is not Control container) return false;
        if (container.TranslatePoint(default, scroll) is not { } p) return false;

        return p.Y >= -0.5 && p.Y + container.Bounds.Height <= scroll.Viewport.Height + 0.5;
    }

    /// <summary>The offset that puts <paramref name="item"/>'s container's top edge at the top of the pane, or null
    /// when its container has not been realised.</summary>
    internal static double? TopOffsetOf(ListBox list, object item, ScrollViewer scroll)
    {
        if (list.ContainerFromItem(item) is not Control container) return null;

        // The container's offset within the scroller's viewport; adding the scroller's own offset gives the position
        // in the content, which is what Offset is measured in.
        if (container.TranslatePoint(default, scroll) is not { } p) return null;

        return Math.Clamp(scroll.Offset.Y + p.Y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
    }

    /// <summary>Roughly where an item that has not been realised yet will turn out to be: its share of the list's own
    /// extent, which is the estimate the virtualizing panel itself is using for unrealised, variable-height items.</summary>
    internal static double EstimatedOffsetOf(int index, int count, ScrollViewer scroll)
    {
        if (index < 0 || count == 0) return scroll.Offset.Y;
        return Math.Clamp(scroll.Extent.Height * index / count, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
    }

    /// <summary>
    /// Eases <paramref name="list"/> until <paramref name="item"/> (at <paramref name="index"/> of
    /// <paramref name="count"/>) is in view with its top at the top of the pane, or leaves it alone when it already is
    /// wholly in view. <paramref name="animator"/> is the surface's one animator, so a second call cancels the first's
    /// scroll rather than racing it.
    ///
    /// <para><b>It checks where it landed, and goes on when it fell short.</b> A short pane realises only the rows
    /// near the top, so a scroll to a distant row sets out on an estimate, and the row can still be unrealised when
    /// the ease ends — which left it below the pane (owner, 2026-11-10, a stage list dragged short). On landing, the
    /// row is asked for again: an item now realised and not at the top is eased the rest of the way from its real
    /// position, and one still unrealised from a fresh estimate. A few rounds at most, so an item that can never be
    /// reached (a list still filling) cannot keep the list moving.</para>
    ///
    /// <para><b>When the estimates run out, the list is asked to realise the row.</b> A virtualizing list re-estimates its
    /// own extent as it scrolls (4,858 px, then 3,162, then 4,695 within one scroll of the .ctech stackup's cards), so a
    /// fresh estimate can miss as badly as the first and the row stays unrealised every round: the cards stopped on
    /// whatever was there. The last round then calls <c>ScrollIntoView</c>, which realises the row from wherever the
    /// scroll ended, and puts the row's top at the top. It is a jump, but a short one from a near miss, and only then.</para>
    /// </summary>
    internal static void BringToTop(ListBox list, object item, int index, int count, ScrollOffsetAnimator animator)
        => BringToTop(list, item, index, count, animator, rounds: 3);

    private static void BringToTop(ListBox list, object item, int index, int count, ScrollOffsetAnimator animator, int rounds)
    {
        var scroll = ScrollerOf(list);
        if (scroll is null) { list.ScrollIntoView(item); return; }
        if (IsFullyVisible(list, item, scroll)) return;

        double target = TopOffsetOf(list, item, scroll) ?? EstimatedOffsetOf(index, count, scroll);
        animator.AnimateTo(scroll, target, () => TopOffsetOf(list, item, scroll), landed: () =>
        {
            if (rounds <= 1)
            {
                if (TopOffsetOf(list, item, scroll) is not null) return;
                list.ScrollIntoView(item);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (TopOffsetOf(list, item, scroll) is { } top) scroll.Offset = scroll.Offset.WithY(top);
                }, Avalonia.Threading.DispatcherPriority.Background);
                return;
            }
            // After layout, so a row the last frames brought into view has been realised and measured.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsFullyVisible(list, item, scroll)) return;
                if (TopOffsetOf(list, item, scroll) is { } top && Math.Abs(top - scroll.Offset.Y) < ScrollOffsetAnimator.MinPixels) return;
                BringToTop(list, item, index, count, animator, rounds - 1);
            }, Avalonia.Threading.DispatcherPriority.Background);
        });
    }
}
