// Owner: PCD-6 (brief-pcd-0 §5, lane-internal entry point; brief-pcd-6 R-pcd6-5). Framework-free.
//
// The PCell Designer's live circuit response: the cell's schematic is its circuit model (S2), so the response is that
// schematic's own extraction run by the one circuit run, at the designer's values. Nothing is written.

using System.Text.RegularExpressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>The response: the S-parameters, or the sentence saying why there are none.</summary>
public sealed record PCellDesignResponseResult(SNP? S, string? Refusal);

/// <summary>brief-pcd-6 R-pcd6-5: a designed cell's circuit response at some values.</summary>
public static partial class PCellDesignResponse
{
    /// <summary>The sweep when neither the schematic nor the <c>.ccell</c> states one (brief-pcd-6 D5).</summary>
    public const string DefaultSweep = "start=1 stop=20 npts=201 Unit=GHz";

    /// <summary>The name of the S-parameter analysis appended when the schematic has none.</summary>
    public const string AnalysisName = "PCD_SP";

    [GeneratedRegex(@"^\s*analysis\s+(\S+)\s.*\btype\s*=\s*sparam\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SparamLine();

    /// <summary>
    /// <see cref="SchematicCircuit.CnlTextOf(SchematicEditModel, string)"/> → <see cref="PreparedCircuit.FromText(string, string?, string?, string)"/>
    /// → <see cref="CircuitEvaluation.Evaluate"/> with <paramref name="values"/> as <c>Sets</c> (name, value text with its
    /// unit), running the schematic's first S-parameter analysis, or <paramref name="sweep"/> (<see cref="DefaultSweep"/>
    /// when null) appended as one when it has none. <paramref name="schematic"/>'s <c>SchematicDirectory</c> is the
    /// cell's schematic folder, so the cell's own parameters bind at their defaults first.
    /// </summary>
    public static PCellDesignResponseResult Run(SchematicEditModel schematic, IReadOnlyList<(string Name, string Expression)> values,
                                                string? sweep = null, string? workspaceRootDir = null,
                                                CancellationToken cancel = default)
    {
        string text;
        try { text = SchematicCircuit.CnlTextOf(schematic, "tb"); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new PCellDesignResponseResult(null, $"The cell's netlist could not be extracted: {e.Message}");
        }

        string analysis;
        if (SparamLine().Match(text) is { Success: true } m) analysis = m.Groups[1].Value;
        else
        {
            analysis = AnalysisName;
            text = text.TrimEnd() + $"\nanalysis {AnalysisName} type=sparam {sweep ?? DefaultSweep}\n";
        }
        cancel.ThrowIfCancellationRequested();

        var circuit = PreparedCircuit.FromText(text, schematic.SchematicDirectory, workspaceRootDir);
        // An EM-only cell (an MSHAPE with Model="none") has no circuit response: a run would draw its open pins as a flat
        // floor that reads like a result, so there is no curve, only the sentence pointing to Run EM.
        if (circuit is { Tb: { } tb, Lib: { } lib } && EmModelSwitch.WithoutCircuitModel(tb, lib) is { Count: > 0 } emOnly)
            return new PCellDesignResponseResult(null,
                $"no circuit model: {string.Join(", ", emOnly)} {(emOnly.Count == 1 ? "is" : "are")} drawn metal simulated by EM only. Run EM for the cell's response.");
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
        {
            Sets = values,
            Analyses = [analysis],
            Measurements = false,
            Control = new RunControl { Token = cancel },
        });
        cancel.ThrowIfCancellationRequested();

        foreach (var r in run.Results)
            if (DataSetBuilder.FindCubeSpec(r.Data, "S") is { } spec)
                return new PCellDesignResponseResult(DataSetBuilder.ToSnp(r.Data, spec), null);
        return new PCellDesignResponseResult(null, run.StatusMessage is { Length: > 0 } why ? why : "The run produced no S-parameters.");
    }
}
