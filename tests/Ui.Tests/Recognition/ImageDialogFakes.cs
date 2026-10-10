// The picture dialog's gates (brief-img-5-dialog.md §4) drive ImageSourceViewModel headless through a recording runner:
// the kind is stated, the trace is the real one unless a test hands its own, and every call is recorded.

using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Engine;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Tests.Imaging;

namespace CircuitRF.Ui.Tests.Recognition;

internal sealed class RecordingImageRunner(DrawingKind kind) : IImageRecognitionRunner
{
    public Technology Technology { get; } = TracePictures.Tech();

    /// <summary>A trace standing in for the real one, or null — <see cref="ImageTrace.Trace"/>.</summary>
    public Func<ImageTraceInput, ImageTraceResult>? TraceAs { get; init; }

    /// <summary>A reading standing in for the real one, or null — <see cref="ImageRecognition.Circuit"/>.</summary>
    public Func<ImageRecognitionInput, ImageRecognitionCircuit>? ReadAs { get; init; }

    public List<ImageTraceInput> Traces { get; } = [];
    public List<(ImageTraceInput Input, ImageTraceTarget Target)> TraceRuns { get; } = [];
    public List<(ImageRecognitionInput Input, ImageRecognitionTarget Target, ImageRecognitionRunOptions Options)> RecognitionRuns { get; } = [];

    public ImageKindResult Classify(RasterImage raster) =>
        new(kind, 0.9, kind == DrawingKind.None ? "a photograph-like picture with no straight line work" : null, []);

    public Technology? LoadTechnology(string path) => Technology;

    public ImageTraceResult Trace(ImageTraceInput input, RunControl? control)
    {
        lock (Traces) Traces.Add(input);
        return TraceAs?.Invoke(input) ?? ImageTrace.Trace(input, control);
    }

    public ImageRecognitionCircuit Read(ImageRecognitionInput input, RecognitionEmitOptions emit, RunControl? control)
    {
        lock (Traces) Traces.Add(input.Trace);
        return ReadAs?.Invoke(input) ?? ImageRecognition.Circuit(input, emit, control);
    }

    public ImageTraceRun TraceRun(ImageTraceInput input, ImageTraceTarget target, ImageTraceRunOptions options, RunControl? control)
    {
        TraceRuns.Add((input, target));
        return new ImageTraceRun(new ImageTraceResult { Report = new RecognitionReport() }, Path.Combine("x", "x.clay"), "x", null, null);
    }

    public ImageRecognitionRun RecognitionRun(ImageRecognitionInput input, ImageRecognitionTarget target, ImageRecognitionRunOptions options,
                                              RunControl? control)
    {
        RecognitionRuns.Add((input, target, options));
        throw new NotSupportedException();
    }
}

internal static class ImageDialog
{
    public static ImageSource Board() => TracePictures.Source(TracePictures.TwoLayerBoard());

    public static ImageSourceViewModel Open(string workspace, RecordingImageRunner runner, ImageSource? source, bool makeSchematic,
                                            string? presets = null) =>
        new(workspace, [new ImageTechnologyChoice("board", Path.Combine(workspace, "board.ctech"))], 0, source, makeSchematic,
            runner, debounce: TimeSpan.Zero, presetDirectory: presets ?? Path.Combine(workspace, "presets"));
}
