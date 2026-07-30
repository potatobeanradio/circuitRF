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
                    : (t.SourcePath != null ? Path.GetFileNameWithoutExtension(t.SourcePath) : null);
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
            if (t.Slice is not null)
            {
                bool first = true;
                foreach (var s in t.Slice)
                {
                    if (s.Role != AxisRole.PinToIndex) continue;
                    sb.Append(first ? '(' : ',');
                    sb.Append(s.AxisName);
                    sb.Append('=');
                    // i/j are S/Y/Z port axes — show 1-based port numbers (i=1 ⇒ port 1).
                    sb.Append(s.AxisName is "i" or "j" ? s.Index + 1 : s.Index);
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

            return sb.ToString();
        }
    }
}
