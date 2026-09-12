// ================================================================
//  SampleProvenance.cs  —  which published samples a run actually SOLVED
//
//  An adaptively sampled sweep publishes every frequency the user asked for, but only solves
//  some of them; the rest come out of an interpolant built from the solved ones. That is the
//  right thing to publish — a caller asked for a grid and gets the grid — but it leaves the
//  result carrying two kinds of number under one name, and NOTHING in the file said which was
//  which.
//
//  The cost of that silence is what this file exists to end (owner report, 2026-09-11): a run
//  STOPPED after six points published a hundred, and a plot with markers turned on drew a
//  hundred markers. A marker is the one piece of plot furniture that means "this is a sample" —
//  so the picture asserted ninety-four solves that never happened, in a form nobody can see is
//  wrong.
//
//  THE MASK IS A CUBE, on the swept axis, 1 = solved and 0 = modelled — the same shape and the
//  same spelling family as `PointAddedBySearch` beside it. It is a cube rather than a note
//  because it has to survive the .npy round trip and be readable by anything that reads the
//  result, and it is emitted UNCONDITIONALLY (all ones when nothing was modelled) for the reason
//  ANT-9 gives for its own: a reader can always ask the question and get "all of them" rather
//  than a missing cube it has to interpret.
// ================================================================

using System;
using System.Collections.Generic;

namespace RfCore.Data
{
    /// <summary>
    /// The convention by which a result says which of its published samples were actually computed
    /// and which were modelled from those. Written by the producer (the planar EM kernel is the only
    /// one today), read by anything that draws or reports the result.
    /// </summary>
    public static class SampleProvenance
    {
        /// <summary>
        /// The well-known cube name: rank 1, on the swept axis, real, 1 = solved / 0 = modelled.
        /// One spelling, shared by the writer and every reader — a second spelling would be a mask
        /// that silently stops applying.
        /// </summary>
        public const string SolvedCubeName = "PointSolved";

        /// <summary>
        /// Builds the mask cube for a sweep: <paramref name="axis"/> long, 1 where the frequency is
        /// in <paramref name="solved"/>. An EMPTY <paramref name="solved"/> means "the question does
        /// not arise" — every point was solved — which is what a non-adaptive run reports.
        /// </summary>
        public static DataCube BuildSolvedCube(Axis axis, IReadOnlyList<double> solved)
        {
            var flags = new double[axis.Length];
            if (solved is null || solved.Count == 0)
            {
                Array.Fill(flags, 1.0);
                return new DataCube([axis], flags);
            }

            // Matched by VALUE, not by index: the published list and the solved list are two
            // orderings of frequencies that came from the same array, and an index correspondence
            // between them stops being true the moment anything is spliced in (the resonance search
            // does exactly that).
            var set = new HashSet<double>(solved.Count);
            foreach (double f in solved) set.Add(f);
            for (int i = 0; i < flags.Length; i++)
                flags[i] = set.Contains(axis.Values[i]) ? 1.0 : 0.0;
            return new DataCube([axis], flags);
        }

        /// <summary>
        /// The mask that applies to a trace drawn against <paramref name="axis"/>, or null when this
        /// DataSet says nothing about the question — which is the ordinary case (a Touchstone file,
        /// a circuit analysis) and means every sample is a sample.
        ///
        /// <para>Found by axis NAME, LENGTH and VALUES, in any group. The name alone is not enough:
        /// a result can carry several axes called "freq" of different lengths (a far-field grid is
        /// one), and a mask applied to the wrong one would hide markers at frequencies that were
        /// solved.</para>
        /// </summary>
        public static bool[]? SolvedMaskFor(DataSet? ds, Axis? axis)
        {
            if (ds is null || axis is null || axis.Length == 0) return null;

            foreach (string group in ds.Groups)
            {
                if (!ds.CubesIn(group).TryGetValue(SolvedCubeName, out var cube)) continue;
                if (cube.Rank != 1 || cube.DataKind != DataKind.Real) continue;

                var a = cube.Axes[0];
                if (a.Length != axis.Length) continue;
                if (!string.Equals(a.Name, axis.Name, StringComparison.OrdinalIgnoreCase)) continue;

                bool sameGrid = true;
                for (int i = 0; i < a.Length && sameGrid; i++)
                    sameGrid = a.Values[i] == axis.Values[i];
                if (!sameGrid) continue;

                var values = cube.RealValues;
                var mask   = new bool[values.Length];
                bool any   = false;
                for (int i = 0; i < values.Length; i++)
                {
                    mask[i] = values[i] != 0.0;
                    any    |= mask[i];
                }

                // A mask that hides EVERY point is not a mask, it is a result with no samples in it.
                // Nothing can produce one today (a sweep has at least its two seeds); if something
                // ever does, drawing the points is the failure that can be seen.
                return any ? mask : null;
            }

            return null;
        }
    }
}
