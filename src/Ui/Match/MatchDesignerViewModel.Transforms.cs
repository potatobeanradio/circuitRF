using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// The Designer's transform rack (match.md §9.4) — the part the window exists for: slide the Norton
/// transforms until the element values are ones the user can build, and watch the response not move
/// while they do.
/// </summary>
public sealed partial class MatchDesignerViewModel
{
    /// <summary>One row per applied transform, in application order.</summary>
    public ObservableCollection<MatchTransformRowViewModel> Transforms { get; } = [];

    /// <summary>Moving one N re-solves the unlocked others so <c>Π N²</c> stays on target.</summary>
    public bool LinkTransforms
    {
        get => _design.LinkTransforms;
        set
        {
            if (value == _design.LinkTransforms) return;
            _design.LinkTransforms = value;
            // Turning link ON has to make the product true immediately, not at the next drag —
            // otherwise the strip says "not matched" about a state the user just asked to be matched.
            if (value && _design.Transforms.Count > 0)
            {
                SetTransformN(0, _design.Transforms[0].N);
                return;
            }
            Refresh(specChanged: false);
            Commit();
        }
    }

    /// <summary>
    /// The pairs the <c>+ add</c> menu offers, <b>by element name</b> and against the ladder as it
    /// stands after every applied transform. A pair whose elements a transform has already consumed
    /// is simply not in the network any more, so it cannot be offered twice.
    /// </summary>
    public IReadOnlyList<MatchAvailablePair> AvailablePairs()
    {
        if (_rebuild?.Basis is not { Ok: true } basis) return [];
        var seq = MatchRebuild.ApplySequence(basis, _design.Transforms, _design.AllowNegativeComponents);
        return [.. NortonTransform.Discover(seq.Network)
                    .Select(p => new MatchAvailablePair(p.NameA, p.NameB))];
    }

    /// <summary>
    /// Adds one transform on the named pair. The new N is seeded at its equal geometric share of the
    /// required product — <b>not at 1</b>, for the reason <c>MatchSolutionSearch.Drive</c> records:
    /// a transform asked for the whole ratio at once is clamped onto its positivity threshold, and one
    /// of the three pi/T products then goes to kilohenries a part in 1e9 from its pole.
    /// </summary>
    public void AddTransform(MatchAvailablePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        double required = _rebuild?.Required ?? 1.0;
        int k = _design.Transforms.Count + 1;
        double seed = required > 0 && double.IsFinite(required)
            ? Math.Pow(required, 1.0 / (2.0 * k))
            : 1.0;

        _design.Transforms.Add(new TransformRecord(pair.ElementA, pair.ElementB, TransformForm.Pi, seed, false));
        Refresh(specChanged: true);

        if (_design.LinkTransforms && _design.Transforms.Count > 1)
        {
            SetTransformN(_design.Transforms.Count - 1, seed);
            return;
        }
        Commit();
    }

    /// <summary>Removes the last transform.</summary>
    public void RemoveLastTransform()
    {
        if (_design.Transforms.Count == 0) return;
        _design.Transforms.RemoveAt(_design.Transforms.Count - 1);
        Refresh(specChanged: true);
        if (_design.LinkTransforms && _design.Transforms.Count > 0)
        {
            SetTransformN(0, _design.Transforms[0].N);
            return;
        }
        Commit();
    }

    /// <summary>True when there is a transform to remove.</summary>
    public bool CanRemoveTransform => _design.Transforms.Count > 0;

    /// <summary>
    /// Moves one transform, redistributing the unlocked others through <c>MatchLinkage</c>.
    /// <b>A locked row is never written</b> — including the one being asked to move.
    /// </summary>
    public void SetTransformN(int index, double n)
    {
        if (index < 0 || index >= _design.Transforms.Count) return;
        if (_design.Transforms[index].Locked) return;

        var slots = new List<LinkSlot>(_design.Transforms.Count);
        for (int i = 0; i < _design.Transforms.Count; i++)
        {
            var rec = _design.Transforms[i];
            slots.Add(new LinkSlot(rec.N, rec.Locked, RangeFor(i, rec)));
        }

        double required = _rebuild?.Required ?? 1.0;
        if (!double.IsFinite(required) || required <= 0) required = 1.0;

        var result = MatchLinkage.Redistribute(slots, index, n, required, _design.LinkTransforms);

        for (int i = 0; i < _design.Transforms.Count; i++)
        {
            if (_design.Transforms[i].Locked) continue;   // belt and braces: the rule, asserted here too
            _design.Transforms[i] = _design.Transforms[i] with { N = result.N[i] };
        }

        Refresh(specChanged: false);

        for (int i = 0; i < Transforms.Count; i++)
        {
            Transforms[i].IsAtLimit = result.AtLimit.Contains(i);
            Transforms[i].Refresh();
        }

        if (!_isDragging) Commit();
    }

    /// <summary>Switches one transform between the pi and the T equivalent.</summary>
    public void SetTransformForm(int index, TransformForm form)
    {
        if (index < 0 || index >= _design.Transforms.Count) return;
        _design.Transforms[index] = _design.Transforms[index] with { Form = form };
        Refresh(specChanged: false);
        Commit();
    }

    /// <summary>Locks or unlocks one transform.</summary>
    public void SetTransformLocked(int index, bool locked)
    {
        if (index < 0 || index >= _design.Transforms.Count) return;
        _design.Transforms[index] = _design.Transforms[index] with { Locked = locked };
        Refresh(specChanged: false);
        Commit();
    }

    // ── The drag ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a slider gesture. While one is running the ladder and every value track the slider
    /// live; only the response PLOTS are held, because they are an S-parameter sweep at
    /// <see cref="MatchDesign.PlotPoints"/> points and are the one part of the chain that could bite
    /// (brief §5). Nothing else is throttled.
    /// </summary>
    public void BeginTransformDrag()
    {
        _isDragging = true;
        _plotsStaleFromDrag = false;
    }

    /// <summary>Ends the gesture: refreshes the held plots and commits the design, once.</summary>
    public void EndTransformDrag()
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (_plotsStaleFromDrag) UpdatePlots();
        _plotsStaleFromDrag = false;
        Commit();
    }

    /// <summary>True between <see cref="BeginTransformDrag"/> and <see cref="EndTransformDrag"/>.</summary>
    public bool IsDragging => _isDragging;

    // ── Row upkeep ────────────────────────────────────────────────────────────

    private TransformRange RangeFor(int index, TransformRecord rec) =>
        index < Transforms.Count && Transforms[index].Range is { } r
            ? r
            // A dropped transform has no range because its pair is gone; pinning it to its own value
            // keeps the linkage arithmetic well-defined without inventing a bound for it.
            : new TransformRange(rec.N, rec.N, true, rec.N, rec.N > 1.0);

    private void RefreshTransformRows()
    {
        while (Transforms.Count > _design.Transforms.Count) Transforms.RemoveAt(Transforms.Count - 1);
        while (Transforms.Count < _design.Transforms.Count)
            Transforms.Add(new MatchTransformRowViewModel(this, Transforms.Count));

        var dropped = _rebuild?.Dropped ?? [];
        var applied = _rebuild?.Applied ?? [];
        int a = 0;

        for (int i = 0; i < _design.Transforms.Count; i++)
        {
            var row = Transforms[i];
            row.Index = i;

            var rec = _design.Transforms[i];
            bool isDropped = dropped.Any(d => ReferenceEquals(d, rec));
            row.IsDropped = isDropped;

            if (!isDropped && a < applied.Count)
            {
                row.Range = applied[a].Range;
                row.WasClamped = applied[a].Clamped;
                // The rebuild clamps into the range it recomputed; showing the requested N beside a
                // ladder built from a different one is the one reading nobody could act on, so the
                // design records what actually ran. `WasClamped` is what says it moved.
                _design.Transforms[i] = applied[a].Record;
                a++;
            }
            else
            {
                row.Range = null;
                row.WasClamped = false;
            }
            row.Refresh();
        }

        OnPropertyChanged(nameof(CanRemoveTransform));
    }
}
