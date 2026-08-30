namespace CircuitRF.Core;

/// <summary>
/// HB-P4 — the test-visible switch and counters for the grid evaluation path.
///
/// <para>The grid path's whole claim is that it produces the scalar path's answer bit for bit, which
/// can only be GATED by running both on the same input. <see cref="DisableGridEvaluate"/> is how a
/// test asks for the scalar one from an engine that would otherwise take the grid, and the counters
/// are how it confirms which door a fixture actually went through — a "bit-identical" test that
/// silently took the same path twice would prove nothing.</para>
///
/// <para><b>Everything here is thread-affine on purpose.</b> The test suite runs classes in
/// parallel, and a process-wide switch would let one class's "now take the scalar path" reach
/// another class's solve. A device pass runs on the thread that started it (the parallel split lives
/// inside <c>SddModel.EvaluateGrid</c>, below where any of this is read), so per-thread state is both
/// sufficient and isolating.</para>
///
/// <para>Counting is off by default and costs one predictable branch per evaluation when it is.
/// Neither switch is used by the application.</para>
/// </summary>
public static class NonlinearEvalDiagnostics
{
    [ThreadStatic] private static bool _disableGrid;
    [ThreadStatic] private static bool _counting;
    [ThreadStatic] private static long _grid;
    [ThreadStatic] private static long _scalar;

    /// <summary>Forces every model's <see cref="ComponentModel.PrefersGridEvaluate"/> to false, on
    /// this thread.</summary>
    public static bool DisableGridEvaluate { get => _disableGrid; set => _disableGrid = value; }

    /// <summary>Enables <see cref="GridCalls"/>/<see cref="ScalarCalls"/> accounting on this thread.</summary>
    public static bool Counting { get => _counting; set => _counting = value; }

    /// <summary>SDD grid evaluations on this thread since the last <see cref="Reset"/>.</summary>
    public static long GridCalls => _grid;

    /// <summary>SDD per-sample evaluations on this thread since the last <see cref="Reset"/>.</summary>
    public static long ScalarCalls => _scalar;

    /// <summary>Zeroes both counters.</summary>
    public static void Reset() { _grid = 0; _scalar = 0; }

    internal static void CountGrid() { if (_counting) _grid++; }
    internal static void CountScalar() { if (_counting) _scalar++; }
}
