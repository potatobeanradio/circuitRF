namespace CircuitRF.Core.Matching;

/// <summary>
/// What one end's DC block did, or why it did nothing — the status strip's own line (match.md §22.5).
/// </summary>
/// <param name="End">1 or 2 — which termination's block this is about.</param>
/// <param name="Applied">
/// True when the block was attached to a real shunt inductor. False when the design carries a value
/// but this end's DC path has nowhere to put it; the value is KEPT either way.
/// </param>
/// <param name="Farads">The block capacitance the design asked for.</param>
/// <param name="ElementName">The inductor it sits in series with, or "" when nothing was applied.</param>
/// <param name="InductanceBefore">That inductor's value as the synthesis left it, henries.</param>
/// <param name="InductanceAfter">Its compensated value, henries. Equal to before when inactive.</param>
/// <param name="SeriesResonanceHz">f_s of the compensated branch, Hz.</param>
/// <param name="BandSpread">
/// Half the peak-to-peak variation of L_eff/L across the design's outer band, as a fraction —
/// 0.013 is "±1.3 %". See <see cref="MatchDcBlock.BandSpread"/>.
/// </param>
/// <param name="Warn">True when f_s is above <see cref="MatchDcBlock.WarnAboveRatio"/> of f₀.</param>
/// <param name="Reason">
/// Why nothing was applied, as a sentence. Empty when <paramref name="Applied"/> is true.
/// </param>
/// <param name="Path">
/// The real series inductors the DC path crosses between the termination and the host, end-first;
/// empty when the host sits on the end node (match.md §22.1). The bias feed's own route.
/// </param>
/// <param name="StopElementName">
/// The real series capacitor that isolates this end, when <see cref="DcBlockStop.SeriesCapacitor"/>
/// is why nothing was applied; "" otherwise.
/// </param>
public sealed record DcBlockNote(
    int End, bool Applied, double Farads, string ElementName,
    double InductanceBefore, double InductanceAfter,
    double SeriesResonanceHz, double BandSpread, bool Warn, string Reason,
    IReadOnlyList<string> Path, string StopElementName)
{
    /// <summary>A note with no path and no stop element — the end-node host, or the lowpass case.</summary>
    public DcBlockNote(
        int end, bool applied, double farads, string elementName,
        double inductanceBefore, double inductanceAfter,
        double seriesResonanceHz, double bandSpread, bool warn, string reason)
        : this(end, applied, farads, elementName, inductanceBefore, inductanceAfter,
               seriesResonanceHz, bandSpread, warn, reason, [], "")
    {
    }
}

/// <summary>Why <see cref="MatchDcBlock.ResolveHost"/>'s walk ended.</summary>
public enum DcBlockStop
{
    /// <summary>
    /// It met a real series capacitor, which ends the DC path. When no host was found before it,
    /// that capacitor already isolates this end and a block is withheld (match.md §22.1).
    /// </summary>
    SeriesCapacitor,

    /// <summary>It ran off the far end of the ladder — the lowpass case when no host was found.</summary>
    EndOfLadder,
}

/// <summary>One real shunt inductor on a termination's DC path, and how the path reached it.</summary>
/// <param name="Index">The inductor's index in <c>MatchNetwork.Elements</c>.</param>
/// <param name="Path">The real series inductors crossed between the termination and it, end-first.</param>
public readonly record struct DcBlockHostSite(int Index, IReadOnlyList<string> Path);

/// <summary>
/// Where one end's blocks go: the result of walking the DC path in from that termination.
/// </summary>
/// <remarks>
/// <b>Every real shunt inductor up to the next real series capacitor, not only the first</b> (owner,
/// 2026-08-28): a Norton π of inductors on the end pair is shunt-L / series-L / shunt-L, and a block
/// on the first alone sends the bias through the series product to the second. The bandpass and
/// highpass BASES have one per end; a π has two, and a chain of transforms could have more.
/// </remarks>
/// <param name="Hosts">The hosts, outermost first. Empty when this end has none.</param>
/// <param name="Stop">Why the walk ended.</param>
/// <param name="StopElementName">The series capacitor that ended it, or "" when it ran off the ladder.</param>
public readonly record struct DcBlockHost(
    IReadOnlyList<DcBlockHostSite> Hosts, DcBlockStop Stop, string StopElementName)
{
    /// <summary>The outermost host's index, or -1 when there is none.</summary>
    public int Index => Hosts.Count > 0 ? Hosts[0].Index : -1;

    /// <summary>The outermost host's path — empty when it sits on the end node or there is no host.</summary>
    public IReadOnlyList<string> Path => Hosts.Count > 0 ? Hosts[0].Path : [];
}

/// <summary>
/// The DC block on a termination's first shunt inductor (match.md §22): a capacitor in series with
/// the branch, and the inductor enlarged so the branch's reactance at the band centre is unchanged.
/// </summary>
/// <remarks>
/// <b>This is a post-rebuild step, not a synthesis input.</b> A shunt inductor at a biased node is a
/// short across the supply, and a DC current starting at a termination reaches EVERY real shunt
/// inductor it can through real series inductors until a real series capacitor ends its path
/// (§22.1) — so a block belongs on each of them. That is the END node's inductor when the end arm is
/// shunt; one series inductor in when the end arm is a series arm whose capacitor is the
/// termination's own absorbed reactance (a FET input), or a Norton T's series product; and BOTH
/// shunt products of a Norton π of inductors, since the π's series product passes DC between them.
/// Absorbed elements are not on the board, so the walk treats them as transparent; a real series
/// capacitor — a <c>CFano</c> or <c>CDetune</c> — isolates the end, and where it comes before any
/// host the block is withheld with that capacitor named. Resolved by NODE after the transforms have
/// run, never by name. <c>MatchSynthesis</c>,
/// <c>NortonTransform</c>, <c>MatchSolutionSearch</c> and both fingerprints therefore never see it,
/// and <see cref="MatchRebuild.Rebuild"/> is the only place <see cref="Apply"/> is called.
///
/// <para><b>The compensation does not care where the host is.</b> <c>L' = L + 1/(ω₀²C)</c> keeps the
/// BRANCH's reactance at ω₀, and a branch one series inductor in from the port is compensated
/// identically; the through-path inductor between the termination and the host is simply the bias
/// feed's route, which the note reports as <see cref="DcBlockNote.Path"/>. One end has ONE block
/// value; with several hosts each inductor gets a capacitor of that value and its own compensation,
/// and the rebuild reports one note per host.</para>
///
/// <para><b>The compensation is exact at ω₀ and second order elsewhere.</b> The branch's reactance
/// becomes j(ωL′ − 1/ωC), so an effective inductance <c>L_eff(ω) = L′ − 1/(ω²C)</c> that equals the
/// synthesised L at ω₀ and runs above it at the top of the band and below it at the bottom. Making
/// that residual go away exactly would mean re-synthesising the branch as a finite transmission zero
/// at f_s — the extraction match.md §6.8 excluded on structural grounds — which is not worth having
/// for a spread the status line can simply report.</para>
/// </remarks>
public static class MatchDcBlock
{
    /// <summary>
    /// f_s above this fraction of f₀ warns: the block is small enough to detune the band.
    /// </summary>
    /// <remarks>
    /// <b>A hint, never a refusal.</b> The compensation is exact at ω₀ for any positive capacitance,
    /// so nothing here is wrong at f₀/5 — it is the band edges that move, and by how much is a number
    /// the status line quotes. §22.2 measured 21.6 → 18.8 dB of worst return loss at 500 pF on the
    /// drain fixture, which is a real cost and not a broken design.
    /// </remarks>
    public const double WarnAboveRatio = 0.2;

    /// <summary>
    /// The default block puts f_s at f₀/10 — <c>C = 100/(ω₀²L)</c>, a spread under 1 % on any
    /// ordinary bandwidth.
    /// </summary>
    public const double DefaultResonanceRatio = 0.1;

    /// <summary>The suffix a block capacitor's name carries: <c>L1</c> blocks with <c>L1blk</c>.</summary>
    public const string NameSuffix = "blk";

    /// <summary>The block capacitor's instance name for a given inductor.</summary>
    public static string BlockName(string inductorName) => inductorName + NameSuffix;

    /// <summary>The compensated inductance, <c>L + 1/(ω₀²C)</c> — exact at ω₀.</summary>
    public static double Compensate(double inductance, double blockFarads, double omega0)
    {
        if (!(blockFarads > 0) || !(omega0 > 0) || !double.IsFinite(blockFarads)) return inductance;
        return inductance + 1.0 / (omega0 * omega0 * blockFarads);
    }

    /// <summary>The uncompensated inductance a compensated one came from, <c>L′ − 1/(ω₀²C)</c>.</summary>
    public static double Uncompensate(double compensatedL, double blockFarads, double omega0)
    {
        if (!(blockFarads > 0) || !(omega0 > 0) || !double.IsFinite(blockFarads)) return compensatedL;
        return compensatedL - 1.0 / (omega0 * omega0 * blockFarads);
    }

    /// <summary>
    /// The seed value for a new block: <c>100/(ω₀²L)</c>, capped at <paramref name="maxFarads"/>.
    /// </summary>
    /// <remarks>
    /// <b>The cap is the reason this takes a third argument</b> (owner, 2026-08-28: too big a
    /// capacitor can be impossible to build). At a low band with a small end inductor the f₀/10 rule
    /// alone reaches tens of nanofarads — fine on a board, absurd on an MMIC — so the Designer's own
    /// <c>DcBlockMaxFarads</c> setting decides where the seed stops. It is only a SEED: any positive
    /// value the user types afterwards is accepted, compensated exactly at ω₀, and reported with what
    /// it costs.
    /// </remarks>
    public static double DefaultFor(double inductance, double omega0, double maxFarads)
    {
        if (!(inductance > 0) || !(omega0 > 0)) return 0.0;
        double c = 1.0 / (DefaultResonanceRatio * DefaultResonanceRatio * omega0 * omega0 * inductance);
        return maxFarads > 0 ? Math.Min(c, maxFarads) : c;
    }

    /// <summary>f_s, the compensated branch's series resonance, Hz.</summary>
    public static double SeriesResonanceHz(double compensatedL, double blockFarads)
    {
        if (!(compensatedL > 0) || !(blockFarads > 0)) return 0.0;
        return 1.0 / (2.0 * Math.PI * Math.Sqrt(compensatedL * blockFarads));
    }

    /// <summary>
    /// Half the peak-to-peak variation of <c>L_eff/L</c> across <paramref name="f1"/>…<paramref name="f2"/>,
    /// as a fraction — 0.013 reads "±1.3 %".
    /// </summary>
    /// <remarks>
    /// <b>Evaluated, not estimated.</b> match.md §22.2 quotes the second-order estimate
    /// <c>±2 (f_s/f₀)² (Δf_half/f₀)</c>, which tracks the UPPER band edge closely and understates the
    /// lower one, because the 1/ω² term is not symmetric about ω₀ — on the section's own 500 pF row
    /// the estimate is ±2.3 % where the L_eff values printed beside it run −2.9 % / +2.3 %, a half
    /// range of ±2.6 %. See <c>src/Core/Match/RESOLVED.md</c> §MN-DCB.
    /// </remarks>
    public static double BandSpread(
        double compensatedL, double blockFarads, double omega0, double f1, double f2)
    {
        if (!(blockFarads > 0) || !(omega0 > 0) || !(f1 > 0) || !(f2 > 0)) return 0.0;
        double l = Uncompensate(compensatedL, blockFarads, omega0);
        if (!(l > 0)) return 0.0;

        double Eff(double hz)
        {
            double om = 2.0 * Math.PI * hz;
            return compensatedL - 1.0 / (om * om * blockFarads);
        }

        double a = Eff(Math.Min(f1, f2)), b = Eff(Math.Max(f1, f2));
        return (b - a) / (2.0 * l);
    }

    /// <summary>
    /// Attaches the design's blocks to the first shunt inductor on each termination's DC path.
    /// </summary>
    /// <param name="network">The finished ladder, after <c>WithEndSplits</c>.</param>
    /// <param name="design">The design, for <c>Term1DcBlock</c>/<c>Term2DcBlock</c> and the bands.</param>
    /// <param name="omega0">
    /// The band centre — <c>2π√(f_lowest·f_highest)</c> of the EFFECTIVE outer pair, which is the same
    /// centre every arm is resonated at, multiband included.
    /// </param>
    /// <param name="notes">One entry per end that carries a value, applied or not.</param>
    /// <returns>A clone when anything changed; the same instance when nothing did.</returns>
    public static MatchNetwork Apply(
        MatchNetwork network, MatchDesign design, double omega0, out IReadOnlyList<DcBlockNote> notes)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(design);

        var list = new List<DcBlockNote>(2);
        notes = list;

        if (!(design.Term1DcBlock > 0) && !(design.Term2DcBlock > 0)) return network;

        var (lo, hi) = design.Effective.Outer;

        // Both ends resolved up front, because in a ladder whose two walks reach ONE element — the
        // degenerate single-shunt-arm ladder, or two series ends whose paths meet at the same interior
        // node — the second block would otherwise silently overwrite the first's compensation.
        var h1 = design.Term1DcBlock > 0 ? ResolveHost(network, 1) : None;
        var h2 = design.Term2DcBlock > 0 ? ResolveHost(network, 2) : None;
        var claimed = new HashSet<int>();

        MatchNetwork? working = null;

        foreach (int end in (ReadOnlySpan<int>)[1, 2])
        {
            double c = end == 1 ? design.Term1DcBlock : design.Term2DcBlock;
            if (!(c > 0) || !double.IsFinite(c)) continue;

            var host = end == 1 ? h1 : h2;
            string n = end.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (host.Hosts.Count == 0)
            {
                // NOT a refusal. The user may be mid-way through changing the order or the form, and
                // the value they typed is theirs — it is kept on the design and simply has nowhere to
                // be right now (§2 step 2).
                list.Add(new DcBlockNote(
                    end, false, c, "", 0, 0, 0, 0, false,
                    InactiveReason(n, host, design.Form), [], host.StopElementName));
                continue;
            }

            bool any = false;
            foreach (var site in host.Hosts)
            {
                if (!claimed.Add(site.Index)) continue;   // termination 1 already blocked this one
                any = true;

                working ??= network.Clone();
                var el = working.Elements[site.Index];
                double before = el.Value;
                double after = Compensate(before, c, omega0);
                el.DcBlock = c;
                el.Value = after;

                double fs = SeriesResonanceHz(after, c);
                double f0 = omega0 / (2.0 * Math.PI);
                list.Add(new DcBlockNote(
                    end, true, c, el.Name, before, after, fs,
                    BandSpread(after, c, omega0, lo, hi),
                    f0 > 0 && fs > WarnAboveRatio * f0, "", site.Path, ""));
            }

            if (!any)
                list.Add(new DcBlockNote(
                    end, false, c, "", 0, 0, 0, 0, false,
                    $"DC block at termination {n}: both ends of this ladder reach the same shunt "
                    + "inductor, and it already carries termination 1's block — stored, not applied.",
                    host.Path, ""));
        }

        return working ?? network;
    }

    private static readonly DcBlockHost None = new([], DcBlockStop.EndOfLadder, "");

    /// <summary>The stored-not-applied sentence for one end, by why its walk found no host.</summary>
    private static string InactiveReason(string end, DcBlockHost host, NetworkForm form)
    {
        if (host.Stop == DcBlockStop.SeriesCapacitor)
            return $"DC block at termination {end}: {host.StopElementName} is a real capacitor in this "
                 + "end's through path and already isolates it from DC — a block on a shunt inductor "
                 + "beyond it would not protect this termination; feed its bias on the termination's "
                 + $"own side of {host.StopElementName}. Stored, not applied.";

        // The lowpass form has no shunt inductor ANYWHERE — it passes DC end to end — so the reason
        // is about the form, not about this end's path. See match.md §22.1.
        if (form == NetworkForm.Lowpass)
            return $"DC block at termination {end}: a lowpass ladder passes DC end to end and has no "
                 + "shunt inductor anywhere; a series block in the through path is not offered — "
                 + "stored, not applied.";

        return $"DC block at termination {end}: no shunt inductor lies on this end's DC path — "
             + "stored, not applied.";
    }

    /// <summary>
    /// Where one end's blocks go, walking the DC path in from that termination (match.md §22.1).
    /// </summary>
    /// <remarks>
    /// <b>DC does not stop at an arm boundary, and it does not stop at a shunt inductor either; it
    /// stops at a real series capacitor.</b> The walk runs over the element list from the
    /// termination inward — indices <c>0..n-1</c> for end 1 and <c>n-1..0</c> for end 2, because
    /// <see cref="MatchNetwork.AssignNets"/> derives the topology from exactly that order: a series
    /// element steps the through node, a shunt element hangs off the current one. At each element:
    /// <list type="bullet">
    /// <item><b>absorbed</b> — skipped. It is the termination's own reactance, not on the board
    /// (§11.3 flattens it as a disabled instance); the ladder-side node of an absorbed series element
    /// IS the device terminal.</item>
    /// <item><b>shunt L</b> — a host, recorded with the path so far. The walk CONTINUES: a Norton π of
    /// inductors puts a second shunt inductor one series inductor further in, and blocking the first
    /// alone would send the bias through the series product to the second (owner, 2026-08-28).</item>
    /// <item><b>shunt C</b> — invisible to DC; continue.</item>
    /// <item><b>series L</b> — passes DC; its name joins the path and the walk continues.</item>
    /// <item><b>series C</b> — ends the path: <see cref="DcBlockStop.SeriesCapacitor"/>, naming it. A
    /// <c>CFano</c> or <c>CDetune</c> is OURS and real, so it stops the walk. With no host before it,
    /// that capacitor already isolates this end.</item>
    /// <item>end of list — <see cref="DcBlockStop.EndOfLadder"/> (the lowpass form, when no host).</item>
    /// </list>
    ///
    /// <para><b>Withholding the block behind a real series capacitor is deliberate.</b> With a real
    /// <c>Cx</c> between the termination and the first shunt inductor, a block on that inductor
    /// protects nothing — the termination is already isolated, and its bias has to be fed on its own
    /// side of <c>Cx</c>. The note and the tooltip say so rather than offering a block that does
    /// nothing for the device. Recorded in match.md §22.1 as an owner-overridable assumption.</para>
    ///
    /// <para>Public because the Designer asks the same question the rebuild does — the toggle is
    /// enabled exactly when this returns a host, and its default value is computed from them.</para>
    /// </remarks>
    public static DcBlockHost ResolveHost(MatchNetwork network, int end)
    {
        ArgumentNullException.ThrowIfNull(network);
        var elements = network.Elements;
        var hosts = new List<DcBlockHostSite>();
        var path = new List<string>();
        int count = elements.Count;
        for (int step = 0; step < count; step++)
        {
            int i = end == 1 ? step : count - 1 - step;
            var e = elements[i];
            if (e.IsAbsorbed) continue;

            if (e.IsShunt)
            {
                if (e.Type == ElementType.L) hosts.Add(new DcBlockHostSite(i, [.. path]));
                continue;
            }

            if (e.Type == ElementType.L) { path.Add(e.Name); continue; }
            return new DcBlockHost(hosts, DcBlockStop.SeriesCapacitor, e.Name);
        }
        return new DcBlockHost(hosts, DcBlockStop.EndOfLadder, "");
    }

    /// <summary>The OUTERMOST host <see cref="ResolveHost"/> names for one end, or null when there is none.</summary>
    public static MatchElement? EndShuntInductor(MatchNetwork? network, int end)
    {
        if (network is null) return null;
        int i = ResolveHost(network, end).Index;
        return i < 0 ? null : network.Elements[i];
    }

    /// <summary>
    /// Which end's block a host element belongs to — 1 or 2 — or 0 when it is on neither end's DC
    /// path. Where both ends reach one inductor, termination 1 owns it, exactly as <see cref="Apply"/>
    /// claims it.
    /// </summary>
    /// <remarks>
    /// Read off the WALK, not the net: a Norton π's second shunt product sits on an interior node
    /// and is still termination 1's block, and a series-RC end's host is interior by construction.
    /// </remarks>
    public static int EndOf(MatchNetwork network, int index)
    {
        ArgumentNullException.ThrowIfNull(network);
        if (ResolveHost(network, 1).Hosts.Any(h => h.Index == index)) return 1;
        if (ResolveHost(network, 2).Hosts.Any(h => h.Index == index)) return 2;
        return 0;
    }

    /// <summary>Every host element for one end, outermost first; empty when there is none.</summary>
    public static IReadOnlyList<MatchElement> EndShuntInductors(MatchNetwork? network, int end)
    {
        if (network is null) return [];
        var host = ResolveHost(network, end);
        return [.. host.Hosts.Select(h => network.Elements[h.Index])];
    }
}
