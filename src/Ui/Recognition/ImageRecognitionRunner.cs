// The picture dialog's one door into the reading — brief-img-5-dialog.md R-im5-9, overview D2.
//
// Each call is the function the CLI calls and no other: the kind (ImageKind.Classify), the read-only trace a layout is
// previewed from (ImageTrace.Trace), the read-only reading a schematic is previewed from (ImageRecognition.Circuit), and
// the two writes (ImageTrace.Run, ImageRecognition.Run). An interface only so a test can RECORD what the dialog hands
// across; nothing here decides anything.

using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Recognition;

/// <summary>What the picture dialog calls to read and to write.</summary>
public interface IImageRecognitionRunner
{
    /// <summary>What kind of drawing the picture is (IM-2).</summary>
    ImageKindResult Classify(RasterImage raster);

    /// <summary>A technology file, read as the CLI reads it; null when it cannot be read.</summary>
    Technology? LoadTechnology(string path);

    /// <summary>Trace, writing nothing — Make Layout's preview.</summary>
    ImageTraceResult Trace(ImageTraceInput input, RunControl? control);

    /// <summary>Trace, recognise and emit, writing nothing — Make Schematic's preview.</summary>
    ImageRecognitionCircuit Read(ImageRecognitionInput input, RecognitionEmitOptions emit, RunControl? control);

    /// <summary>Make Layout.</summary>
    ImageTraceRun TraceRun(ImageTraceInput input, ImageTraceTarget target, ImageTraceRunOptions options, RunControl? control);

    /// <summary>Make Schematic, from a layout picture.</summary>
    ImageRecognitionRun RecognitionRun(ImageRecognitionInput input, ImageRecognitionTarget target, ImageRecognitionRunOptions options,
                                       RunControl? control);
}

/// <summary>The real one: the entry points, unchanged.</summary>
public sealed class ImageRecognitionRunner : IImageRecognitionRunner
{
    public static ImageRecognitionRunner Instance { get; } = new();

    private ImageRecognitionRunner() { }

    public ImageKindResult Classify(RasterImage raster) => ImageKind.Classify(raster);

    public Technology? LoadTechnology(string path) => TechnologyResolver.LoadForPath(path);

    public ImageTraceResult Trace(ImageTraceInput input, RunControl? control) => ImageTrace.Trace(input, control);

    public ImageRecognitionCircuit Read(ImageRecognitionInput input, RecognitionEmitOptions emit, RunControl? control) =>
        ImageRecognition.Circuit(input, emit, control);

    public ImageTraceRun TraceRun(ImageTraceInput input, ImageTraceTarget target, ImageTraceRunOptions options, RunControl? control) =>
        ImageTrace.Run(input, target, options, control);

    public ImageRecognitionRun RecognitionRun(ImageRecognitionInput input, ImageRecognitionTarget target,
                                              ImageRecognitionRunOptions options, RunControl? control) =>
        ImageRecognition.Run(input, target, options, control);
}
