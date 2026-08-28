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

    /// <summary>
    /// What the last typed N did, or empty — the rack's single refusal line.
    /// </summary>
    /// <remarks>
    /// One line for the WHOLE rack rather than one per row (owner, 2026-08-20: the N boxes became
    /// inline editors, and "its input must be validated/refused if bad input"). A message that
    /// appears inside a row pushes every row under it down, which is the same layout shift
    /// <c>InlineEditText</c>'s own measure exists to prevent.
    /// </remarks>
    [ObservableProperty] private string _transformNote = "";

    /// <summary>Posts one refusal from a row's inline editor. Empty clears it.</summary>
    internal void SetTransformNote(string note) => TransformNote = note ?? "";

    /// <summary>
    /// True when the rack's controls mean anything — i.e. the design is in bandpass form.
    /// </summary>
    /// <remarks>
    /// A lowpass or highpass ladder is single elements, so every like-kind pair in it shares an
    /// orientation and <c>NortonTransform.Discover</c> finds nothing (match.md §16.5). Nothing is
    /// hidden by this that could otherwise be used: <c>+ add</c> would open an empty menu,
    /// <c>− remove</c> has nothing to remove, and the link toggle has nothing to link.
    /// </remarks>
    public bool TransformRackApplies => _design.Form == NetworkForm.Bandpass;

    /// <summary>
    /// The one line the rack shows instead of an empty list in lowpass and highpass form.
    /// </summary>
    /// <remarks>
    /// <b>It must not read as a fault</b> (match.md §16.5). The rack's own "no transformable pair"
    /// refusal is about a bandpass ladder that came out without one — a thing that stops the design
    /// completing. Here there are no pairs BY CONSTRUCTION and the design is finished anyway, because
    /// the DC pin already put the far resistance on its target. Same empty list, opposite meaning.
    /// </remarks>
    public string TransformRackNote => TransformRackApplies
        ? ""
        : "Lowpass and highpass networks have no Norton pairs: every value is the prototype's.";

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

        // Held against the analysis landings for the whole edit — see AsOneEdit.
        AsOneEdit(() =>
        {
            _design.Transforms.Add(
                new TransformRecord(pair.ElementA, pair.ElementB, TransformForm.Pi, seed, false));
            Refresh(specChanged: true);

            if (_design.LinkTransforms && _design.Transforms.Count > 1)
            {
                SetTransformN(_design.Transforms.Count - 1, seed);
                return;
            }
            Commit();
        });
    }

    /// <summary>Removes the last transform.</summary>
    public void RemoveLastTransform()
    {
        AsOneEdit(() =>
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
        });
    }

    /// <summary>True when there is a transform to remove.</summary>
    public bool CanRemoveTransform => _design.Transforms.Count > 0;

    /// <summary>
    /// Moves one transform, redistributing the unlocked others through <c>MatchLinkage</c>.
    /// <b>A locked row is never written</b> — including the one being asked to move.
    /// </summary>
    public void SetTransformN(int index, double n) => AsOneEdit(() =>
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
    });

    /// <summary>Switches one transform between the pi and the T equivalent.</summary>
    public void SetTransformForm(int index, TransformForm form) => AsOneEdit(() =>
    {
        if (index < 0 || index >= _design.Transforms.Count) return;
        _design.Transforms[index] = _design.Transforms[index] with { Form = form };
        Refresh(specChanged: false);
        Commit();
    });

    /// <summary>Locks or unlocks one transform.</summary>
    public void SetTransformLocked(int index, bool locked) => AsOneEdit(() =>
    {
        if (index < 0 || index >= _design.Transforms.Count) return;
        _design.Transforms[index] = _design.Transforms[index] with { Locked = locked };
        Refresh(specChanged: false);
        Commit();
    });

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

    /// <summary>
    /// Narrows every row's slider travel to the N it can <b>actually reach</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"sometimes I can move the slider control higher, but the
    /// transform is already at its maximum level."</i>
    ///
    /// <para><b>The slider was bounded by the wrong range.</b> <c>TransformRange</c> is the
    /// POSITIVITY bound — how far this transform can go before one of its own three products goes
    /// negative — and that is the right bound for <c>MatchLinkage.Redistribute</c> to clamp against.
    /// It is not what the user can drag to. With link on, <c>Redistribute</c> ends by recomputing the
    /// dragged transform from what the OTHERS settled at
    /// (<c>n[current] = Clamp(sqrt(required / rest))</c>), so once the other unlocked transforms are
    /// parked on their own bounds, <c>rest</c> stops moving and the dragged N stops with it — while
    /// the thumb, bounded by the positivity range, keeps travelling. Dragged far enough with every
    /// other transform LOCKED, the value is a constant and the slider moves the whole way for
    /// nothing.</para>
    ///
    /// <para>So the reachable end points are <b>measured, not derived</b>: ask
    /// <c>Redistribute</c> — the very function that will run — what it settles on for a request at
    /// each end of the positivity range, and use those two answers as the slider's bounds. A second,
    /// analytic implementation of the same rule is a second thing to disagree with it. The settled
    /// value is monotone non-decreasing in the request, so the two probes are the interval's ends.
    /// It costs two O(k²) arithmetic passes over a handful of slots per rebuild.</para>
    ///
    /// <para>With link OFF the two coincide (the request is simply clamped into the range) and this
    /// changes nothing, which is the check that it is narrowing for the right reason.</para>
    /// </remarks>
    private void RefreshReachableRanges()
    {
        double required = _rebuild?.Required ?? 1.0;
        if (!double.IsFinite(required) || required <= 0) required = 1.0;

        var slots = new List<LinkSlot>(_design.Transforms.Count);
        for (int i = 0; i < _design.Transforms.Count; i++)
        {
            var rec = _design.Transforms[i];
            slots.Add(new LinkSlot(rec.N, rec.Locked, RangeFor(i, rec)));
        }

        for (int i = 0; i < Transforms.Count && i < slots.Count; i++)
        {
            var row = Transforms[i];
            var range = slots[i].Range;

            // NOT gated on this row's OWN lock (owner-reported, 2026-08-20: "when I lock a slider it
            // is disabled — good — but the position of the slider circle changes between locked and
            // unlocked state — bad"). Falling back to the positivity range for a locked row made its
            // bounds widen the moment it was locked, and a thumb draws at a FRACTION of its range, so
            // N stood still while the circle jumped. A lock decides whether the value may be moved;
            // it says nothing about where the value could live, and `Redistribute` agrees — it
            // ignores the driven slot's own lock, so probing a locked row returns exactly what
            // probing it unlocked does. Dropping the term makes the two states identical by
            // construction rather than by coincidence.
            if (!_design.LinkTransforms || slots.Count < 2 || row.IsDropped)
            {
                row.Reachable = range;
                continue;
            }

            double lo = MatchLinkage.Redistribute(slots, i, range.Min, required, link: true).N[i];
            double hi = MatchLinkage.Redistribute(slots, i, range.Max, required, link: true).N[i];
            if (lo > hi) (lo, hi) = (hi, lo);

            // A COLLAPSED interval is not "this N cannot move" — it is "the linkage cannot improve
            // Π N² from where the rack stands", and those are different statements (owner-reported,
            // 2026-08-20: "I can't slide any transforms anymore, they are all disabled even when they
            // are unlocked"). The state that produces it is an UNREACHABLE ratio: ask for 5 Ω into
            // 200 Ω and the required product is 54 while every transform's positivity range caps at
            // 1, so every probe clamps to the same bound at both ends and every row reads as pinned.
            // That is precisely the design the user is trying to rescue, and taking the whole rack
            // away from them is the worst possible moment to do it. So the narrowing applies only
            // when it describes a real interval; otherwise the slider keeps its positivity range and
            // behaves exactly as it did before any of this.
            row.Reachable = hi - lo > Math.Max(1e-12, Math.Abs(hi) * 1e-9)
                ? range with { Min = lo, Max = hi }
                : range;
        }
    }

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
        }

        // A SECOND pass, after every row's Range is in place: the reachable end points are a question
        // about the whole rack, so no row can be asked before all of them have been told.
        RefreshReachableRanges();
        foreach (var row in Transforms) row.Refresh();

        OnPropertyChanged(nameof(CanRemoveTransform));
        OnPropertyChanged(nameof(TransformRackApplies));
        OnPropertyChanged(nameof(TransformRackNote));
    }
}
