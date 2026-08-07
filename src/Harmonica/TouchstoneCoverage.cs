using RfCore;

namespace CircuitRF.Harmonica;

/// <summary>
/// R-hrf-12 — an embedding file must cover every harmonic harmonicaRF will ask it about, and a file
/// that does not is REFUSED by name.
///
/// <para><b>Why a refusal and not an extrapolation.</b> Silent polynomial extrapolation to 5f₀ would
/// corrupt precisely the study this tool exists for: the whole product is about what happens when the
/// second and third harmonic terminations move, and an embedding invented above its measured band
/// would make those moves read as physics. An explicit opt-in gives constant hold-last-value, which
/// at least states what it is doing; polynomial extrapolation is not offered at any setting.</para>
/// </summary>
public static class TouchstoneCoverage
{
    /// <summary>What a file covers, and whether it is enough.</summary>
    /// <param name="File">The file as the model named it.</param>
    /// <param name="MinHz">Lowest frequency present.</param>
    /// <param name="MaxHz">Highest frequency present.</param>
    /// <param name="NeededHz">The highest frequency harmonicaRF will ask about — K·f₀.</param>
    /// <param name="Covers">Whether <paramref name="NeededHz"/> is inside the file's range.</param>
    public readonly record struct Coverage(string File, double MinHz, double MaxHz, double NeededHz, bool Covers)
    {
        /// <summary>
        /// The refusal's wording: the file, the frequency it does not reach, and the range it does —
        /// all three, because a user cannot act on any two of them.
        /// </summary>
        public string Refusal =>
            $"Embedding file '{File}' does not reach {NeededHz / 1e9:G6} GHz, which harmonicaRF needs " +
            $"for the harmonics it is set to solve. The file covers {MinHz / 1e9:G6} to " +
            $"{MaxHz / 1e9:G6} GHz. Lower the harmonic order or the fundamental, supply a file that " +
            $"reaches {NeededHz / 1e9:G6} GHz, or opt in to holding the last measured value constant " +
            $"above the file's range — harmonicaRF will not extrapolate an embedding, because an " +
            $"invented response at 2f0 and 3f0 is exactly the quantity this tool exists to study.";
    }

    /// <summary>Reads the frequency range a Touchstone file actually carries.</summary>
    public static Coverage Check(string file, double neededHz)
    {
        var snp = TouchstoneIO.ReadFile(file, readComments: false);
        if (snp.Frequencies.Length == 0)
            return new Coverage(file, 0, 0, neededHz, false);

        double min = snp.Frequencies[0];
        double max = snp.Frequencies[^1];
        return new Coverage(file, min, max, neededHz, neededHz <= max * (1 + 1e-12));
    }

    /// <summary>
    /// Checks every Touchstone file the model names against K·f₀. Returns the refusals, empty when
    /// everything is covered.
    /// </summary>
    /// <param name="allowHoldLastValue">
    /// The explicit opt-in of R-hrf-12: hold the last measured value constant above the file's range.
    /// It suppresses the refusal and NOTHING else — in particular it never enables extrapolation.
    /// </param>
    public static IReadOnlyList<Coverage> CheckAll(
        CircuitModel model, Func<string, string>? resolve = null, bool allowHoldLastValue = false)
    {
        if (allowHoldLastValue) return [];

        double needed = model.Settings.HarmonicCount * model.Settings.FrequencyHz;
        var refusals = new List<Coverage>();

        foreach (string file in model.Embedding.TouchstoneFiles)
        {
            var c = Check(resolve?.Invoke(file) ?? file, needed);
            if (!c.Covers) refusals.Add(c with { File = file });
        }
        return refusals;
    }
}
