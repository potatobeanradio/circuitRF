// ================================================================
//  ContourColormaps.cs  —  Matplotlib-style colormap ramps for contour fills
//
//  Each colormap is a piecewise-linear ramp defined by (t, R, G, B) control
//  points where t∈[0,1] and R/G/B∈[0,1].  Sample(map, t) lerps between the
//  two surrounding control points and returns an opaque SKColor.
//
//  Enum order matches ContourColorMap:
//    0 Gray  1 Bone  2 Pink  3 Spring  4 Summer  5 Autumn  6 Winter
//    7 Cool  8 Wistia  9 Hot  10 Afmhot  11 GistHeat  12 Copper
// ================================================================

using System;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay
{
    internal static class ContourColormaps
    {
        // Each entry is an array of (t, R, G, B) control points, sorted by t.
        private static readonly (float T, float R, float G, float B)[][] Ramps =
        {
            // 0 Gray  — linear black→white
            new[] { (0f, 0f, 0f, 0f), (1f, 1f, 1f, 1f) },

            // 1 Bone  — blue-tinted greyscale
            new[] { (0f, 0f, 0f, 0f),
                    (0.375f, 0.329f, 0.329f, 0.443f),
                    (0.750f, 0.663f, 0.776f, 0.776f),
                    (1f, 1f, 1f, 1f) },

            // 2 Pink  — pink-tinted greyscale
            new[] { (0f, 0.118f, 0f, 0f),
                    (0.375f, 0.643f, 0.451f, 0.451f),
                    (0.750f, 0.874f, 0.776f, 0.776f),
                    (1f, 1f, 1f, 1f) },

            // 3 Spring — magenta → yellow
            new[] { (0f, 1f, 0f, 1f), (1f, 1f, 1f, 0f) },

            // 4 Summer — dark-green → warm yellow-green
            new[] { (0f, 0f, 0.5f, 0.4f), (1f, 1f, 1f, 0.4f) },

            // 5 Autumn — red → yellow
            new[] { (0f, 1f, 0f, 0f), (1f, 1f, 1f, 0f) },

            // 6 Winter — blue → cyan-green
            new[] { (0f, 0f, 0f, 1f), (1f, 0f, 1f, 0.5f) },

            // 7 Cool — cyan → magenta
            new[] { (0f, 0f, 1f, 1f), (1f, 1f, 0f, 1f) },

            // 8 Wistia — warm yellow-green to vibrant yellow
            new[] { (0f, 0.886f, 1f, 0.118f),
                    (0.5f, 1f, 0.706f, 0f),
                    (1f, 1f, 0.882f, 0f) },

            // 9 Hot — black → red → yellow → white
            new[] { (0f,     0f, 0f, 0f),
                    (0.333f, 1f, 0f, 0f),
                    (0.667f, 1f, 1f, 0f),
                    (1f,     1f, 1f, 1f) },

            // 10 Afmhot — smoother black→red→yellow→white
            new[] { (0f,    0f,   0f,   0f),
                    (0.25f, 0.5f, 0f,   0f),
                    (0.50f, 1f,   0.5f, 0f),
                    (0.75f, 1f,   1f,   0.5f),
                    (1f,    1f,   1f,   1f) },

            // 11 GistHeat — black → red → orange → white
            new[] { (0f,     0f, 0f,    0f),
                    (0.333f, 1f, 0f,    0f),
                    (0.667f, 1f, 0.627f,0f),
                    (1f,     1f, 1f,    1f) },

            // 12 Copper — black → copper tone
            new[] { (0f,   0f, 0f,    0f),
                    (0.8f, 1f, 0.625f, 0.398f),
                    (1f,   1f, 0.781f, 0.498f) },
        };

        /// <summary>Sample colormap at t∈[0,1] — returns an opaque SKColor.</summary>
        public static SKColor Sample(ContourColorMap map, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            var ramp = Ramps[(int)map];

            // Find the enclosing segment.
            int hi = 1;
            while (hi < ramp.Length - 1 && ramp[hi].T < (float)t) hi++;
            var lo  = ramp[hi - 1];
            var hip = ramp[hi];

            float span = hip.T - lo.T;
            float f    = span > 0f ? (float)((t - lo.T) / span) : 0f;

            float r = Math.Clamp(lo.R + f * (hip.R - lo.R), 0f, 1f);
            float g = Math.Clamp(lo.G + f * (hip.G - lo.G), 0f, 1f);
            float b = Math.Clamp(lo.B + f * (hip.B - lo.B), 0f, 1f);

            return new SKColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
        }
    }
}
