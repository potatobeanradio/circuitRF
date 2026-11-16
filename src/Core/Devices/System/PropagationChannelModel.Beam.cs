// ================================================================
//  PropagationChannelModel.Beam.cs  —  the words of picking the beam (brief PCH-9, note §6)
//
//  BeamSelect=auto|rank|index|manual, BeamRank=n, TxBeam=/RxBeam= (a 1-based codebook entry or a
//  direction "(θ, φ)" in degrees), Codebook= (a CSV of directions), BeamSearch=exhaustive|alternating,
//  ScanLimit= (the default codebook's extent from boresight). The selection itself is src/Engine's
//  (BeamSelection), at the start of a run, when the carrier is known; the channel only holds what
//  was asked and, after a run, the table it produced.
// ================================================================

using System.Globalization;

namespace CircuitRF.Core.Devices;

/// <summary>brief-pch-9 — BeamSelect.</summary>
public enum BeamSelectMode { Auto, Rank, Index, Manual }

/// <summary>brief-pch-9 — BeamSearch; Default is alternating past 10⁶ pairs, exhaustive below.</summary>
public enum BeamSearchMode { Default, Exhaustive, Alternating }

/// <summary>brief-pch-9 R-pch9-4 — TxBeam / RxBeam: a 1-based codebook entry, or a direction in degrees.</summary>
public sealed record BeamChoice(int? Index, double ThetaDeg, double PhiDeg)
{
    public bool IsDirection => Index is null;

    public override string ToString() => Index is { } i ? i.ToString(CultureInfo.InvariantCulture)
        : string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##})", ThetaDeg, PhiDeg);

    /// <summary>Null for an empty word; refused when it is neither a whole number from 1 nor two angles.</summary>
    public static BeamChoice? Parse(string word, string key)
    {
        string w = word.Trim().Trim('"').Trim();
        if (w.Length == 0) return null;
        if (int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k))
            return k >= 1 ? new BeamChoice(k, double.NaN, double.NaN) : throw new ParameterRefusalException(PropagationChannelDiagnostics.BeamChoice(key, word));
        string inner = w.StartsWith('(') && w.EndsWith(')') ? w[1..^1] : w;
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        static double? Angle(string s)
        {
            s = s.Trim();
            foreach (string unit in new[] { "deg", "°" })
                if (s.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) s = s[..^unit.Length].Trim();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && double.IsFinite(x) ? x : null;
        }
        if (parts.Length == 2 && Angle(parts[0]) is { } t && Angle(parts[1]) is { } p && t >= 0 && t <= 90)
            return new BeamChoice(null, t, p);
        throw new ParameterRefusalException(PropagationChannelDiagnostics.BeamChoice(key, word));
    }
}

/// <summary>brief-pch-9 R-pch9-1 — one row of a Codebook file: a direction in degrees and the end it belongs to (null: both).</summary>
public sealed record CodebookRow(double ThetaDeg, double PhiDeg, string? End)
{
    /// <summary>
    /// A CSV of directions: <c>theta, phi[, end]</c> in degrees, one per line, <c>end</c> tx, rx or both (blank: both); a first line
    /// whose first field is not a number is a header; '#' starts a comment.
    /// </summary>
    public static IReadOnlyList<CodebookRow> Read(string path)
    {
        if (!File.Exists(path)) throw new ParameterRefusalException(PropagationChannelDiagnostics.Codebook(path, "no such file."));
        var rows = new List<CodebookRow>();
        int line = 0;
        foreach (string raw in File.ReadLines(path))
        {
            line++;
            int hash = raw.IndexOf('#');
            string text = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (text.Length == 0) continue;
            var f = text.Split(',', StringSplitOptions.TrimEntries);
            bool numeric = double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t);
            if (!numeric && rows.Count == 0 && line <= 2) continue;   // a header
            if (!numeric || f.Length < 2 || !double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                throw new ParameterRefusalException(PropagationChannelDiagnostics.Codebook(path, $"line {line} is not theta, phi in degrees."));
            if (!(t >= 0 && t <= 90))
                throw new ParameterRefusalException(PropagationChannelDiagnostics.Codebook(path, $"line {line}: θ {t:G6}° is not in the front half-space (0 to 90°)."));
            string? end = f.Length > 2 ? f[2].ToLowerInvariant() : null;
            if (end is "" or "both") end = null;
            if (end is not (null or "tx" or "rx"))
                throw new ParameterRefusalException(PropagationChannelDiagnostics.Codebook(path, $"line {line}: the end is tx, rx or both, not '{f[2]}'."));
            rows.Add(new CodebookRow(t, p, end));
        }
        if (rows.Count == 0) throw new ParameterRefusalException(PropagationChannelDiagnostics.Codebook(path, "it lists no direction."));
        return rows;
    }
}
