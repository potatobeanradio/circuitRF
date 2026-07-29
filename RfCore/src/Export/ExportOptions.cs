// ================================================================
//  ExportOptions.cs  —  Options record for DataSetExporter
//
//  See docs/design/data-export.md §7.1.
// ================================================================

using System.Collections.Generic;

namespace RfCore.Export;

/// <summary>
/// Immutable options record passed to <see cref="DataSetExporter.Export"/>.
/// All properties have safe defaults; callers only set what they need.
/// </summary>
/// <param name="Format">
/// Target file format.  Default: <see cref="ExportFormat.Mat"/>.
/// </param>
/// <param name="IncludeLinearNetwork">
/// When <c>true</c> and a non-null <see cref="ILinearNetworkPayload"/> is supplied,
/// serialise the per-harmonic linear MNA data (G, bSrc, iNl, index maps) into the
/// file.  Zero cost when <c>false</c>.  Default: <c>false</c>.
/// </param>
/// <param name="LinearEvalMode">
/// Controls whether the exporter evaluates linear-interior node voltages and branch
/// currents.  Ignored when <see cref="IncludeLinearNetwork"/> is <c>false</c>.
/// Default: <see cref="Export.LinearEvalMode.EvaluateNone"/>.
/// </param>
/// <param name="EvalNodeNames">
/// Absolute downward node paths to evaluate when
/// <see cref="LinearEvalMode"/> is <see cref="Export.LinearEvalMode.EvaluateSpecified"/>.
/// Format: same as <c>V(X1.drain)</c> measurement syntax.
/// Ignored otherwise.  Default: empty.
/// </param>
/// <param name="EvalBranchRefs">
/// Branch references to evaluate (format: <c>"I:instancePath:terminal"</c>) when
/// <see cref="LinearEvalMode"/> is <see cref="Export.LinearEvalMode.EvaluateSpecified"/>.
/// Ignored otherwise.  Default: empty.
/// </param>
/// <param name="SizeWarningThresholdMiB">
/// Estimated disk-size threshold in mebibytes (1 MiB = 2²⁰ bytes) above which a warning
/// is emitted to <see cref="System.Console.Error"/> before writing.  The write always
/// proceeds (warn-and-continue, no abort).  Default: 100 MiB.
/// </param>
public sealed record ExportOptions(
    ExportFormat  Format                    = ExportFormat.Mat,
    bool          IncludeLinearNetwork      = false,
    LinearEvalMode LinearEvalMode           = LinearEvalMode.EvaluateNone,
    IReadOnlyList<string>? EvalNodeNames    = null,
    IReadOnlyList<string>? EvalBranchRefs   = null,
    double        SizeWarningThresholdMiB   = 100.0)
{
    /// <summary>Default options: .mat format, no linear network, 100 MiB warning threshold.</summary>
    public static ExportOptions Default { get; } = new();
}
