using System;
using Avalonia;
using Avalonia.Headless;

namespace CircuitRF.DocGen;

/// <summary>
/// Brings up a real, drawing-capable Avalonia application with no display attached.
///
/// <para><b><c>UseHeadlessDrawing = false</c> is load-bearing.</b> The default headless platform
/// stubs drawing out entirely: <c>RenderAsync</c> then produces an empty SVG document and reports no
/// error whatsoever. Every figure comes back blank and nothing fails. Do not remove the flag.</para>
/// </summary>
public static class HeadlessHost
{
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        AppBuilder.Configure<DocsApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        // The animation clock is advanced by the RENDER TIMER, and nothing ticks it in a headless
        // process that never enters a render loop — so a keyframe animation declared in a control
        // theme (an Expander's chevron) is photographed at whatever angle wall-clock happened to
        // reach. Handing the generator this is what lets it run an animation to its end before
        // capturing; it is Avalonia.Headless API, which src/Ui may not reference.
        CircuitRF.Ui.Diagnostics.UiArtworkGenerator.AdvanceFrames =
            frames => AvaloniaHeadlessPlatform.ForceRenderTimerTick(frames);

        _started = true;
    }
}
