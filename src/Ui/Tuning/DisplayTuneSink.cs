using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Design.Results;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using RfCore.Data;

namespace CircuitRF.Ui.Tuning;

/// <summary>
/// A session's results, sent to every open Data Display under the schematic's own results path
/// (brief-tuneopt-3 R-to3-5, D8) and, on Stop, written there by <see cref="RunResultsWriter"/> — the
/// same file Simulate writes, so a display that was showing tuned data keeps showing it.
/// </summary>
public sealed class DisplayTuneSink(
    string baseDir,
    string schematicKey,
    string? fileNameOverride,
    Func<IEnumerable<DataSourceLibraryViewModel>> openLibraries,
    IMessageSink? messages,
    string chip = DataSourceLibraryViewModel.TuningChip,
    Action<IReadOnlyList<CircuitRF.Design.Circuit.FieldsArtifact>>? fieldsWritten = null) : ITuneResultSink, Optimization.IOptimizerDisplay
{
    /// <summary>brief-sar-10 R-sar10-3 — the 3D views showing a recombined field draw it again.</summary>
    public void FieldsWritten(IReadOnlyList<CircuitRF.Design.Circuit.FieldsArtifact> fields) => fieldsWritten?.Invoke(fields);

    /// <summary>The results file every published DataSet stands in for.</summary>
    public string ResultsPath { get; } = Path.GetFullPath(Path.Combine(
        ResultsWriter.ResultsDirectory(baseDir), RunResultsWriter.ResolveFileName(fileNameOverride, schematicKey)));

    private readonly string _baseChip = chip;
    private string _chip = chip;

    /// <summary>The chip the display shows on what is published: <c>Tuning</c>, or at a corner
    /// <c>Tuning · ss_85</c> (brief-yield-7 R-ya7-4).</summary>
    public string Chip => _correctLabel is { } label ? $"{_chip} · {label}" : _chip;

    public void Publish(DataSet data)
    {
        if (_correct is { } correct) data = correct(data);
        foreach (var lib in openLibraries()) lib.Publish(ResultsPath, data, Chip);
    }

    public void AtCorner(string? corner) => _chip = corner is null ? _baseChip : $"{_baseChip} · {corner}";

    // brief-sdl-7 D7: the circuit model with the last EM solve's offset added, labelled as an estimate.
    private Func<DataSet, DataSet>? _correct;
    private string? _correctLabel;

    public void Correct(Func<DataSet, DataSet>? correct, string? label)
    {
        _correct = correct;
        _correctLabel = correct is null ? null : label;
    }

    /// <summary>brief-sdl-7 D7 — the EM solve's traces, faded, under the live ones: the snapshot ghost the display
    /// already draws (a yield trial's re-run uses it the same way).</summary>
    public void ShowOverlay(DataSet? data)
    {
        foreach (var lib in openLibraries())
        {
            if (data is null) lib.ClearSnapshot(ResultsPath);
            else lib.ShowSnapshot(ResultsPath, data);
        }
    }

    /// <summary>The Optimizer's best point; <paramref name="partial"/> when it ran only the goals'
    /// analyses, so the others keep the file's data and draw dimmed (brief-tuneopt-10 R-to10-7).</summary>
    public void Publish(DataSet data, bool partial)
    {
        foreach (var lib in openLibraries()) lib.Publish(ResultsPath, data, Chip, partial);
    }

    public void Commit(DataSet data, IReadOnlyDictionary<string, string> values)
    {
        var written = RunResultsWriter.WriteRun(
            baseDir, schematicKey, TunedProvenance.Stamp(data, values), messages, fileNameOverride);
        if (written.Count == 0) return;
        // The file now holds what every display is showing: keep it as the file's version, so nothing
        // flickers back to the old run and nothing re-reads what is already in memory.
        foreach (var lib in openLibraries()) lib.KeepPublishedAsFile(ResultsPath);
    }

    public void Drop()
    {
        foreach (var lib in openLibraries())
        {
            lib.Unpublish(ResultsPath);
            lib.ClearSnapshot(ResultsPath);
        }
    }

    public void Snapshot()
    {
        foreach (var lib in openLibraries()) lib.TakeSnapshot(ResultsPath);
    }

    public void ClearSnapshot()
    {
        foreach (var lib in openLibraries()) lib.ClearSnapshot(ResultsPath);
    }
}
