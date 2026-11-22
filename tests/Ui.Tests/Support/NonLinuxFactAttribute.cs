using System;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips WITH A REASON on Linux, in <c>NonWindowsFact</c>'s shape. For a test whose
/// claim rests on reading text back out of a picture: Skia rasterises glyphs through FreeType on Linux, and the text
/// reader misreads some labels drawn that way which it reads correctly from the macOS raster (seen 2026-11-22).
/// </summary>
internal sealed class NonLinuxFactAttribute : FactAttribute
{
    public NonLinuxFactAttribute(string why)
    {
        if (OperatingSystem.IsLinux()) Skip = why;
    }
}

/// <summary>The <see cref="TheoryAttribute"/> twin of <see cref="NonLinuxFactAttribute"/>.</summary>
internal sealed class NonLinuxTheoryAttribute : TheoryAttribute
{
    public NonLinuxTheoryAttribute(string why)
    {
        if (OperatingSystem.IsLinux()) Skip = why;
    }
}
