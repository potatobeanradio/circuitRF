// ================================================================
//  RendererText.cs  —  Font-fallback text helpers for Data Display renderers
//
//  IBM Plex Sans (primary) lacks some Unicode code points used in RF data
//  strings (e.g. ∠ U+2220 in MA/DB complex values).  These helpers split
//  text into consecutive runs by coverage and draw each run with the
//  appropriate typeface (Plex if covered, DejaVu otherwise).
//
//  All callers are left-aligned — the left-advance run-splitter is exact
//  and requires no center/right width-reconciliation.
// ================================================================

using System.Collections.Generic;
using System.Text;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay
{
    internal static class RendererText
    {
        /// <summary>
        /// Draws <paramref name="text"/> left-aligned at (<paramref name="x"/>, <paramref name="y"/>),
        /// using <paramref name="primary"/> (Plex) per glyph and falling back to
        /// <paramref name="fallback"/> (DejaVu) for any code point the primary typeface lacks.
        /// Returns total advance width.
        /// </summary>
        public static float DrawLeftTextWithFallback(
            SKCanvas canvas, string text, float x, float y,
            SKFont primary, SKFont fallback, SKPaint paint)
        {
            float penX = x;
            foreach (var (run, usePrimary) in SplitRuns(text, primary.Typeface))
            {
                var f = usePrimary ? primary : fallback;
                canvas.DrawText(run, penX, y, SKTextAlign.Left, f, paint);
                penX += f.MeasureText(run);
            }
            return penX - x;
        }

        /// <summary>
        /// Measures the advance width of <paramref name="text"/> using per-glyph fallback
        /// (primary for covered glyphs, <paramref name="fallback"/> for uncovered ones).
        /// </summary>
        public static float MeasureTextWithFallback(string text, SKFont primary, SKFont fallback)
        {
            float total = 0f;
            foreach (var (run, usePrimary) in SplitRuns(text, primary.Typeface))
            {
                var f = usePrimary ? primary : fallback;
                total += f.MeasureText(run);
            }
            return total;
        }

        // Yields consecutive runs of text grouped by whether the primary typeface
        // covers each rune (GetGlyph returns 0 for a missing glyph).
        private static IEnumerable<(string run, bool usePrimary)> SplitRuns(
            string text, SKTypeface primary)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            var    sb      = new StringBuilder();
            bool?  current = null;

            foreach (var rune in text.EnumerateRunes())
            {
                bool covered = primary.GetGlyph(rune.Value) != 0;
                if (current is null)
                {
                    current = covered;
                    sb.Append(rune.ToString());
                }
                else if (covered == current.Value)
                {
                    sb.Append(rune.ToString());
                }
                else
                {
                    yield return (sb.ToString(), current.Value);
                    sb.Clear();
                    sb.Append(rune.ToString());
                    current = covered;
                }
            }

            if (sb.Length > 0)
                yield return (sb.ToString(), current!.Value);
        }
    }
}
