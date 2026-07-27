namespace CircuitRF.Ui.Tests;

/// <summary>
/// xUnit runs test CLASSES in parallel by default. <c>LayoutTextOutline.TestOverrideTypeface</c> is a
/// single static field (the seam every headless test that renders a <c>LabelShape</c> uses to avoid
/// touching <c>SkiaFonts.PlexRegular</c>, which cannot load without a live Avalonia app host) — any two
/// test classes that set/clear it independently race, and one class's teardown can null it out while
/// another class's label-rendering test is still mid-run, producing a spurious
/// <c>InvalidOperationException</c> from <c>SkiaFonts.Load</c>. Every test class that touches this seam
/// declares <c>[Collection(Name)]</c> so xUnit serializes them relative to each other (xUnit still
/// parallelizes across OTHER collections normally — this is not a blanket
/// <c>DisableTestParallelization</c>, which would slow the whole ~2300-test suite for a race that only
/// four classes are actually party to).
/// </summary>
[CollectionDefinition(Name)]
public class LayoutTextOutlineTypefaceCollection
{
    public const string Name = "LayoutTextOutline.TestOverrideTypeface";
}
