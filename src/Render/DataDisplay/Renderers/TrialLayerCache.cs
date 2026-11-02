// ================================================================
//  TrialLayerCache.cs  —  a family's members, kept as one picture
//
//  A trial family is hundreds of translucent strokes (500 trials of
//  151 points is ~60 ms of raster per plot at retina size), and a
//  host that redraws a plot it has not changed — the Data Display
//  page being panned moves every plot on it, and each frame redraws
//  them all — pays that every frame. The members are the bulk and
//  the part that does not change while the page moves, so this keeps
//  them as an image in DEVICE pixels and blits it.
//
//  First sight of a member set draws it directly, as without a cache:
//  a pan of the plot's own axes changes the members every frame and
//  must not pay for a picture it never reuses. The image is made when
//  the SAME members are asked for again, and drawn from then on.
//
//  Opt-in per call (TrialRenderer.Draw's cache argument), because the
//  same renderer writes SVG and PDF, where a picture would rasterise
//  vector output. Raster (CPU) images only: their memory may be freed
//  on any thread, which a GPU texture's may not.
// ================================================================

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    /// <summary>One host's cached member layers, one per trial trace it draws (see the file header).</summary>
    public sealed class TrialLayerCache
    {
        /// <summary>The largest picture kept, in device pixels: past it the members are drawn directly.</summary>
        internal const long MaxPixels = 16L * 1024 * 1024;

        private readonly ConditionalWeakTable<Trace, Entry> _entries = new();

        /// <summary>How many pictures were made — what a test reads to know the cache was used.</summary>
        internal int Builds { get; private set; }

        private sealed class Entry
        {
            public Key     Key;
            public SKImage? Image;
        }

        /// <summary>
        /// Everything the members' pixels depend on, relative to the picture's own origin: which curves (by
        /// reference — a re-resolve refills the list with new ones), which of them in what colour and order, the
        /// stroke width and axis, the transform, and the canvas's scale and sub-pixel offset. NOT the canvas's
        /// whole-pixel translation, which is what a pan of the page changes and the blit absorbs.
        /// </summary>
        private readonly record struct Key(
            Trace.FamilyCurve[] Curves, int[] Members, uint[] Colors, float Width, bool Secondary,
            (double, double, double, double) Primary, (double, double, double, double) SecondaryMap,
            bool XLog, bool YLog, (double W, double H) CanvasSize, PlotRect Viewport,
            float ScaleX, float ScaleY, int SubX, int SubY, int PixelW, int PixelH)
        {
            public bool Matches(in Key o) =>
                Width == o.Width && Secondary == o.Secondary && Primary == o.Primary && SecondaryMap == o.SecondaryMap
                && XLog == o.XLog && YLog == o.YLog && CanvasSize == o.CanvasSize && Viewport == o.Viewport
                && ScaleX == o.ScaleX && ScaleY == o.ScaleY && SubX == o.SubX && SubY == o.SubY
                && PixelW == o.PixelW && PixelH == o.PixelH
                && Same(Curves, o.Curves) && Members.AsSpan().SequenceEqual(o.Members) && Colors.AsSpan().SequenceEqual(o.Colors);

            private static bool Same(Trace.FamilyCurve[] a, Trace.FamilyCurve[] b)
            {
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false;
                return true;
            }
        }

        /// <summary>
        /// Draws <paramref name="members"/> of <paramref name="trace"/> from the picture when one matches, making it
        /// when the same members are asked for a second time. False: nothing was drawn — the caller draws the members
        /// itself (first sight, a canvas that is rotated or skewed, a picture too large).
        /// </summary>
        internal bool TryDraw(SKCanvas canvas, Trace trace, TransformSet tf, bool sec, float width,
                              IReadOnlyList<TrialStroke> members, Action<SKCanvas> drawMembers)
        {
            var m = canvas.TotalMatrix;
            if (m.SkewX != 0 || m.SkewY != 0 || m.Persp0 != 0 || m.Persp1 != 0 || m.ScaleX <= 0 || m.ScaleY <= 0) return false;

            var local  = PlotRenderer.ViewportClipRect(tf.Viewport, tf.CanvasSize);
            var device = m.MapRect(local);
            var pixels = SKRectI.Ceiling(device);
            pixels = new SKRectI((int)Math.Floor(device.Left), (int)Math.Floor(device.Top), pixels.Right, pixels.Bottom);
            if (pixels.Width <= 0 || pixels.Height <= 0 || (long)pixels.Width * pixels.Height > MaxPixels) return false;

            var curves = trace.FamilyCurves.ToArray();
            var idx    = new int[members.Count];
            var cols   = new uint[members.Count];
            for (int i = 0; i < members.Count; i++) { idx[i] = members[i].Member; cols[i] = (uint)members[i].Color; }
            var key = new Key(curves, idx, cols, width, sec, tf.Primary, tf.Secondary, tf.XLog, tf.YLog, tf.CanvasSize,
                              tf.Viewport, m.ScaleX, m.ScaleY,
                              (int)Math.Round((m.TransX - pixels.Left) * 64), (int)Math.Round((m.TransY - pixels.Top) * 64),
                              pixels.Width, pixels.Height);

            if (!_entries.TryGetValue(trace, out var entry) || !entry.Key.Matches(key))
            {
                // First sight: remember it, draw directly. The old picture is released here, on the drawing thread.
                entry?.Image?.Dispose();
                _entries.AddOrUpdate(trace, new Entry { Key = key });
                return false;
            }

            if (entry.Image is null)
            {
                using var surface = SKSurface.Create(new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                if (surface is null) return false;
                var c = surface.Canvas;
                c.Clear(SKColors.Transparent);
                c.Translate(-pixels.Left, -pixels.Top);
                c.Concat(in m);
                c.ClipRect(local);
                drawMembers(c);
                entry.Image = surface.Snapshot();
                Builds++;
            }

            canvas.Save();
            canvas.ResetMatrix();
            canvas.DrawImage(entry.Image, pixels.Left, pixels.Top);
            canvas.Restore();
            return true;
        }
    }
}
