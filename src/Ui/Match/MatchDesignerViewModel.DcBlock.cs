using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Matching;

namespace CircuitRF.Ui.Matching;

/// <summary>One rendered status line for one end's DC block.</summary>
/// <param name="End">1 or 2 — which termination.</param>
/// <param name="Text">The whole sentence, in the Designer's own units.</param>
/// <param name="Warn">
/// True when f_s is above <see cref="MatchDcBlock.WarnAboveRatio"/> of f₀ — the line carries the
/// <c>warn</c> class then, and nothing else changes. <b>A hint, never a refusal.</b>
/// </param>
public sealed record MatchDcBlockLine(int End, string Text, bool Warn);

/// <summary>
/// The DC block on a termination's first shunt inductor, as the Designer offers it (match.md §22.5).
/// </summary>
/// <remarks>
/// <b>Nothing here is state.</b> The value lives on <c>MatchDesign.Term1DcBlock</c>/
/// <c>Term2DcBlock</c>, the compensation is <c>MatchDcBlock.Apply</c>'s and it runs inside
/// <c>MatchRebuild.Rebuild</c>, and every write goes through <see cref="Commit"/> — so a block
/// toggles and edits from either window and undoes from either window, exactly like every other
/// Designer edit.
///
/// <para>The ONE piece of view-model-only state is <see cref="_dcBlockShadow"/>, and it is
/// deliberately not on the design: unchecking the toggle has to leave the design holding 0 (that is
/// what "no block" IS), while re-checking should give the user their own value back rather than the
/// f₀/10 seed. A field that only means something between two clicks of one session is exactly the
/// kind of thing brief §0.3 says must not be persisted.</para>
/// </remarks>
public sealed partial class MatchDesignerViewModel
{
    /// <summary>The value a toggle-off took away, per end, so toggling back on restores it.</summary>
    private readonly double[] _dcBlockShadow = new double[3];

    /// <summary>The design's stored block for one end, farads; 0 for none.</summary>
    public double DcBlockOf(int end) => end == 1 ? _design.Term1DcBlock : _design.Term2DcBlock;

    /// <summary>
    /// Where one end's block would go in the network as it now stands — the host, or why there is
    /// none (match.md §22.1).
    /// </summary>
    /// <remarks>
    /// Read off the CURRENT rebuild, not off the form or the topology, because that is the network the
    /// block would actually be attached to — a Norton π on the first pair can replace <c>L1</c> with a
    /// product, a T can put the host one series product in, and the answer has to follow both. It is
    /// <c>MatchDcBlock</c>'s own resolution, so the toggle is enabled exactly when the rebuild would
    /// apply something.
    /// </remarks>
    public DcBlockHost? DcBlockResolution(int end) =>
        _rebuild?.Network is { } net ? MatchDcBlock.ResolveHost(net, end) : null;

    /// <summary>The shunt inductor one end's block would sit in, or null when its DC path has none.</summary>
    public MatchElement? DcBlockHost(int end) => MatchDcBlock.EndShuntInductor(_rebuild?.Network, end);

    /// <summary>True when a real shunt inductor lies on this end's DC path in the network as it now stands.</summary>
    public bool CanDcBlock(int end) => DcBlockHost(end) is not null;

    /// <summary>What the <c>DC Block</c> toggle offers, or the one thing standing in its way.</summary>
    /// <remarks>
    /// <b>The sentence "this end's arm is a series arm — its capacitor already blocks DC" is gone,
    /// because it was false</b> (owner, 2026-08-28): a series-RC termination's capacitor is the
    /// device's own, absorbed and not on the board, and a Norton T's series arm has no capacitor at
    /// all. What isolates an end is a REAL series capacitor, and that is the one case named here.
    /// </remarks>
    public string DcBlockTooltip(int end)
    {
        if (DcBlockResolution(end) is not { } host)
            return "No shunt inductor lies on this end's DC path.";

        if (host.Hosts.Count == 1)
        {
            if (host.Path.Count == 0)
                return "Insert a DC-blocking capacitor in series with this end's shunt inductor. The "
                     + "inductor is enlarged so the branch's reactance at the band centre is unchanged. "
                     + "Edit the value in the network pane.";

            string name = _rebuild!.Network!.Elements[host.Index].Name;
            return $"Insert a DC-blocking capacitor in series with {name}, the first shunt inductor on "
                 + $"this end's DC path ({Through(host.Path)}). {name} is enlarged so the branch's "
                 + "reactance at the band centre is unchanged. Edit the value in the network pane.";
        }

        if (host.Hosts.Count > 1)
        {
            // A Norton π of inductors: shunt / series / shunt, and the series product passes DC
            // between them, so BOTH shunt products need a block (owner, 2026-08-28). One value, one
            // capacitor per inductor, each compensated on its own.
            var net = _rebuild!.Network!;
            var parts = host.Hosts.Select(h =>
                h.Path.Count == 0 ? net.Elements[h.Index].Name
                                  : $"{net.Elements[h.Index].Name} ({Through(h.Path)})").ToList();
            return $"Insert a DC-blocking capacitor in series with each of {Join(parts)} — every shunt "
                 + "inductor on this end's DC path up to the next series capacitor. Each inductor is "
                 + "enlarged so its branch's reactance at the band centre is unchanged. Edit the value "
                 + "in the network pane.";
        }

        if (host.Stop == DcBlockStop.SeriesCapacitor)
            return $"{host.StopElementName} is a real capacitor in this end's through path and already "
                 + "isolates it from DC. A block beyond it would not protect this termination — feed "
                 + $"its bias on the termination's side of {host.StopElementName}.";

        // The lowpass form has no shunt inductor ANYWHERE — it passes DC end to end — so the reason
        // is about the form and not about this end's path. See match.md §22.1.
        if (_design.Form == NetworkForm.Lowpass)
            return "A lowpass ladder passes DC end to end; a series block in the through path is not "
                 + "a shunt-inductor block and is not offered here.";

        return "No shunt inductor lies on this end's DC path.";
    }

    /// <summary>"reached through L4 — a series inductor passes DC" / "… L4 and L6 — series inductors pass DC".</summary>
    private static string Through(IReadOnlyList<string> path) => path.Count == 1
        ? $"reached through {path[0]} — a series inductor passes DC"
        : $"reached through {Join(path)} — series inductors pass DC";

    /// <summary>"L4", "L4 and L6", "L4, L6 and L8".</summary>
    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>
    /// Turns one end's block on or off. On seeds the shadowed value, or <c>DefaultFor</c> when there
    /// is none; off stores what was there and writes 0.
    /// </summary>
    public void SetDcBlockEnabled(int end, bool on)
    {
        double current = DcBlockOf(end);
        if (on == current > 0) return;

        if (!on)
        {
            _dcBlockShadow[end] = current;
            SetDcBlock(end, 0.0);
            return;
        }

        double seed = _dcBlockShadow[end] > 0 ? _dcBlockShadow[end] : DcBlockDefault(end);
        if (seed > 0) SetDcBlock(end, seed);
    }

    /// <summary>
    /// The f₀/10 seed for one end, capped by <c>MatchDesignerSettings.DcBlockMaxFarads</c>; 0 when
    /// this end has no shunt inductor to size it from.
    /// </summary>
    /// <remarks>
    /// The inductance it is sized from is the UNCOMPENSATED one, of the smallest host. If a block is already on, the
    /// element's value is L′ — seeding from that would compound the compensation every time the toggle
    /// was cycled.
    /// </remarks>
    public double DcBlockDefault(int end)
    {
        var hosts = MatchDcBlock.EndShuntInductors(_rebuild?.Network, end);
        if (hosts.Count == 0) return 0.0;
        // With several hosts (a π of inductors), size from the SMALLEST inductor: C = 100/(ω₀²L) is
        // largest there, so every host's branch resonates at or below f₀/10.
        double l = hosts.Min(h => MatchDcBlock.Uncompensate(h.Value, h.DcBlock, _design.Omega0));
        return MatchDcBlock.DefaultFor(l, _design.Omega0, Settings.DcBlockMaxFarads);
    }

    /// <summary>
    /// Writes one end's block value and rebuilds. <b>Not a specification change</b> — the block is
    /// applied after the transforms and is no part of <c>MatchSpecKey</c>, so the solution search is
    /// left alone and the listed solutions stay exactly as they were.
    /// </summary>
    public void SetDcBlock(int end, double farads)
    {
        AsOneEdit(() =>
        {
            double value = farads > 0 && double.IsFinite(farads) ? farads : 0.0;
            if (DcBlockOf(end) == value) return;

            if (end == 1) _design.Term1DcBlock = value; else _design.Term2DcBlock = value;
            Refresh(specChanged: false);
            Commit();
        });
    }

    /// <summary>The status strip's block lines, in end order — empty when no block is set.</summary>
    private IReadOnlyList<MatchDcBlockLine> BuildDcBlockLines()
    {
        var notes = _rebuild?.DcBlocks ?? [];
        if (notes.Count == 0) return [];

        var lines = new List<MatchDcBlockLine>(notes.Count);
        foreach (var n in notes)
            lines.Add(n.Applied
                ? new MatchDcBlockLine(n.End, ActiveBlockText(n), n.Warn)
                : new MatchDcBlockLine(n.End, n.Reason, false));
        return lines;
    }

    /// <summary>
    /// One active block's sentence — the value, the compensated inductor and what it came from, the
    /// branch's own resonance, the band spread, and the feed rule (match.md §22.3).
    /// </summary>
    /// <remarks>
    /// <b>The feed rule is stated in the UI because nothing else on screen would say it</b> (owner,
    /// 2026-08-28). A block in the branch with a SEPARATE choke at the drain puts an undamped parallel
    /// resonance inside the baseband — measured 30 kΩ at 5 MHz on §22.3's own fixture — and no
    /// lossless network can remove it. Feeding the bias THROUGH the compensated inductor, with the
    /// block as its far-end decoupling, is the topology this compensation assumes.
    ///
    /// <para>f_s is formatted at <c>AutoUnit</c> rather than the Designer's frequency unit: the band
    /// edges are read in GHz and a baseband resonance in the same unit renders as "0.489 GHz", which
    /// is the number nobody wants to read.</para>
    /// </remarks>
    private string ActiveBlockText(DcBlockNote n)
    {
        int digits = Settings.SignificantDigits;
        string c = MatchValueFormat.FormatWithUnit(
            n.Farads, MatchQuantity.Capacitance, Settings.UnitFor(MatchQuantity.Capacitance), digits);
        string unit = Settings.UnitFor(MatchQuantity.Inductance);
        string after = MatchValueFormat.FormatWithUnit(
            n.InductanceAfter, MatchQuantity.Inductance, unit, digits);
        string before = MatchValueFormat.Format(
            n.InductanceBefore, MatchQuantity.Inductance, unit, digits).Text;
        string fs = MatchValueFormat.FormatWithUnit(
            n.SeriesResonanceHz, MatchQuantity.Frequency, MatchValueFormat.AutoUnit, digits);
        string spread = (n.BandSpread * 100.0).ToString("0.0", CultureInfo.InvariantCulture);
        string end = n.End.ToString(CultureInfo.InvariantCulture);

        // When the host is not on the end node, the line names the route: the series inductor(s)
        // between the termination and the host are where the bias current actually flows, and the
        // feed rule has to say that it reaches the termination through them (match.md §22.3).
        string route = n.Path.Count == 0
            ? ""
            : $" — the DC path from termination {end} reaches {n.ElementName} through {Join(n.Path)}";
        string feed = n.Path.Count == 0
            ? $"Feed the bias through {n.ElementName}, not through a separate choke."
            : $"Feed the bias through {n.ElementName}; it reaches the termination through "
              + $"{Join(n.Path)}, not through a separate choke.";

        string line =
            $"DC block at termination {end}: {c} in series with {n.ElementName} "
            + $"({after}, from {before}){route}; branch resonates at {fs}; inductance ±{spread} % "
            + $"across the band. {feed}";

        return n.Warn
            ? line + " — the block is small enough to detune the band; 10× larger keeps the spread "
                   + "under 1 %."
            : line;
    }
}
