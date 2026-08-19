// ================================================================
//  TraceLabeler.cs  —  Per-plot minimal label policy (§2.7)
//
//  Computes the shortest label for each trace that still disambiguates
//  it within the set.  Identity components (source, quantity) are
//  extracted per-trace; any component constant across the whole set
//  is dropped from every label.
//
//  Headless, no Avalonia, no Skia — call from any layer.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CircuitRF.Ui.DataDisplay
{
    public static class TraceLabeler
    {
        /// <summary>
        /// Returns one minimal label per trace, computed over the full set.
        /// A component constant across every trace is dropped from all labels.
        /// </summary>
        /// <param name="traces">All traces in the plot (left + right axis).</param>
        /// <param name="alwaysShowSource">
        /// When true the source token is forced on even if it is identical for
        /// every trace (mirrors <c>AppSettings.AlwaysDisplayDataSourcePrefix</c>).
        /// </param>
        /// <param name="aliasFor">
        /// Resolves a trace's user-facing source alias (R-res-4 — "baseline"/"tuned" style short
        /// names), typically <see cref="DataDisplay.ViewModels.DataSourceLibraryViewModel.AliasFor"/>
        /// applied to the trace's SourcePath. Falls back to the file-name stem (the pre-alias
        /// behaviour) when null or when it returns null/empty for a given trace — so a trace whose
        /// source isn't in the library yet (or a caller with no library reference at all) still
        /// gets a sensible source component.
        /// </param>
        public static IReadOnlyList<string> ComputeMinimalLabels(
            IReadOnlyList<Trace> traces,
            bool alwaysShowSource = false,
            Func<Trace, string?>? aliasFor = null)
        {
            if (traces.Count == 0) return Array.Empty<string>();

            // ---- Step 1: extract identity components -----------------------
            var sources    = new string?[traces.Count];
            var quantities = new string [traces.Count];

            for (int i = 0; i < traces.Count; i++)
            {
                var t     = traces[i];
                var alias = aliasFor?.Invoke(t);
                sources[i] = !string.IsNullOrEmpty(alias)
                    ? alias
                    : (t.EffectiveSourcePath is { } sp ? Path.GetFileNameWithoutExtension(sp) : null);
                quantities[i] = t.IsCubeBound
                    ? BuildCubeQuantity(t)
                    : t.ShortDescription;
            }

            // ---- Step 2: decide which components to show -------------------
            //  Source is dropped when it is constant across all traces (or null
            //  for every trace) — unless alwaysShowSource forces it on.
            bool sourceConstant =
                sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1;
            bool showSource = alwaysShowSource || !sourceConstant;

            // ---- Step 3: build labels --------------------------------------
            var result = new string[traces.Count];
            for (int i = 0; i < traces.Count; i++)
            {
                string? src = sources[i];
                string  qty = quantities[i];

                result[i] = (showSource && !string.IsNullOrEmpty(src))
                    ? $"{src}·{qty}"
                    : qty;
            }

            return result;
        }

        // ----------------------------------------------------------------
        //  Private helpers
        // ----------------------------------------------------------------

        private static string BuildCubeQuantity(Trace t)
        {
            var sb = new StringBuilder(t.CubeName ?? "");

            // Append pinned-axis selectors, e.g. "(node=0)".
            //
            // The S/Y/Z port axes are the exception: when BOTH are pinned the pair is written
            // positionally as "S(1,2)" rather than "S(i=1,j=2)". A matrix element is universally
            // read positionally in RF, the names carry nothing, and the network (Touchstone) path
            // has always written it that way — so this also stops the same quantity from being
            // labelled two different ways depending on which path produced it. With only ONE port
            // axis pinned (the other iterated as a family) the name is KEPT, because a lone "S(1)"
            // would not say which index it is.
            if (t.Slice is not null)
            {
                bool bothPortsPinned =
                    t.Slice.Count(x => x.Role == AxisRole.PinToIndex && x.AxisName is "i" or "j") == 2;

                bool first = true;
                foreach (var s in t.Slice)
                {
                    if (s.Role != AxisRole.PinToIndex) continue;
                    bool isPort = s.AxisName is "i" or "j";
                    sb.Append(first ? '(' : ',');

                    // The owner resolves what a pinned axis READS as, because that answer lives on
                    // the cube and a Trace deliberately never holds one: the swept VALUE with its
                    // unit ("VDS=3.5 V"), or a labelled axis's own label alone ("IDS" — the label
                    // names the quantity, so repeating the axis name in front of it says nothing).
                    // It is the WHOLE token, not just the value, so the owner owns that choice in
                    // one place. Falling back to the raw index when the owner resolved nothing keeps
                    // a hand-built trace — and every test that builds one directly — unchanged.
                    string? display = isPort ? null : t.PinnedAxisDisplay(s.AxisName);
                    if (display is not null)
                    {
                        sb.Append(display);
                        first = false;
                        continue;
                    }

                    if (!(isPort && bothPortsPinned))
                    {
                        sb.Append(s.AxisName);
                        sb.Append('=');
                    }
                    // i/j are S/Y/Z port axes — show 1-based port numbers (i=0 ⇒ port 1).
                    sb.Append(isPort ? s.Index + 1 : s.Index);
                    first = false;
                }
                if (!first) sb.Append(')');
            }

            // Append transform suffix (not folded into the cube name).
            if (t.Transform != CubeTransform.None)
            {
                sb.Append(t.Transform switch
                {
                    CubeTransform.dB20  => " dB20",
                    CubeTransform.dB10  => " dB10",
                    CubeTransform.dB    => " dB",
                    CubeTransform.Mag   => " Mag",
                    CubeTransform.Phase => " Phase",
                    CubeTransform.Real  => " Real",
                    CubeTransform.Imag  => " Imag",
                    CubeTransform.Conj  => " Conj",
                    _                   => ""
                });
            }

            // A binding that FAILED to resolve must say so on the plot, not only under the spec box on
            // the card. Nothing else in this label can express it: the label is built from the trace's
            // authoring state (cube + pins + transform), which is still perfectly well-formed when the
            // resolve failed — a "plot versus" trace with a complex X, a cube missing from a re-run,
            // a bad typed spec. Without this the trace simply vanishes from the plot with a label that
            // looks entirely normal.
            if (t.InvalidSpecText is not null || t.ExpressionError is not null)
                sb.Append(" <invalid>");

            return sb.ToString();
        }
    }
}
