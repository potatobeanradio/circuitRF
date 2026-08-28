namespace CircuitRF.Ui.Layout;

/// <summary>Which of the layout editor's three selection channels a picked entry belongs to.</summary>
public enum LayoutPickKind
{
    /// <summary>An in-design ruler annotation — <see cref="LayoutView.Rulers"/>. First in a pick
    /// stack, because rulers paint above everything (R-rul-11).</summary>
    Ruler,

    /// <summary>A <see cref="LayoutShape"/> — <see cref="LayoutView.Shapes"/>.</summary>
    Shape,

    /// <summary>A placed cell — <see cref="LayoutView.Instances"/>.</summary>
    Instance,
}

/// <summary>
/// One entry in a click's pick stack: WHICH channel, and the index within it.
///
/// <para><b>Why the cycling cache is keyed on this rather than on a bare index</b>
/// (docs/design/layout-view.md §6.2 R-L1c-2): overlap cycling used to walk shapes only, so a plain
/// <c>int</c> said everything. Once rulers paint above geometry and are selectable, a click can land
/// on three different kinds of thing at one point, and a stack of bare indices cannot say which list
/// index 3 belongs to. Making the kind part of the entry is what lets ONE cache and ONE algorithm
/// serve all three — the alternative was three parallel caches whose "which am I on" states would
/// drift apart the first time a selection changed from anywhere else.</para>
/// </summary>
public readonly record struct LayoutPick(LayoutPickKind Kind, int Index);
