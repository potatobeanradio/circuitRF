using System.Text.Json;
using Clipper2Lib;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>
/// Work a generator script asks circuitRF to do for it, mid-generate.
///
/// <para><b>Why the traffic reverses at all.</b> Everywhere else the host asks and the script
/// answers. Layer booleans are the one thing a production cell needs that the script side must not
/// implement: circuitRF already does them with Clipper2, over the same int64 database units, and a
/// second clipper on the far side of a pipe would be two implementations of one rule whose
/// disagreement is invisible — a result off by a database unit renders perfectly and is wrong. It is
/// the same reasoning that keeps metres off the wire, with a worse failure mode. So rather than
/// re-implement, the script asks.</para>
///
/// <para><b>The discriminator is <c>op</c>, and it is unambiguous by construction.</b> Every request
/// carries one; no reply ever does — replies carry <c>ok</c>. So a frame arriving while the host is
/// waiting for a generate reply is a service request if and only if it names an op, and the host
/// needs no mode flag, no sequence number and no state machine to tell the two apart. See
/// <c>docs/design/pcell-wire-schema.md</c> §8.</para>
///
/// <para><b>What this file may never become.</b> A general "run this on the host" channel. Each op
/// is added deliberately, does one geometric thing, and touches no file, no process and no part of
/// the document — a generator script is somebody else's code and this is the surface it can reach.
/// </para>
/// </summary>
public static class PCellWireHostServices
{
    /// <summary>
    /// The most service calls one generate may make before the host stops believing it.
    ///
    /// <para>A script looping forever on service calls would otherwise hold <c>_gate</c> and hang the
    /// UI with no diagnosis. Real cells measured against a vendor kit issue single digits of these;
    /// the bound is far above any honest use and exists only so a runaway ends as a message rather
    /// than a freeze.</para>
    /// </summary>
    public const int MaxServiceCallsPerExchange = 4096;

    /// <summary>
    /// True when <paramref name="frame"/> is a request from the script rather than the reply the host
    /// is waiting for.
    /// </summary>
    public static bool IsServiceRequest(in PCellWireFrame frame, out string op)
    {
        op = "";
        try
        {
            using var doc = JsonDocument.Parse(frame.Json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("op", out var opElement)) return false;
            if (opElement.ValueKind != JsonValueKind.String) return false;
            op = opElement.GetString() ?? "";
            return op.Length > 0;
        }
        catch (JsonException)
        {
            // Not our problem to diagnose here — the caller decodes it as a reply and reports the
            // malformed JSON with the context it has.
            return false;
        }
    }

    /// <summary>
    /// Performs one service request and builds its reply. Never throws for a bad request: a script's
    /// mistake comes back as <c>ok: false</c> with a reason, which the script re-raises at the line
    /// that asked. Only a genuinely broken frame reaches the caller as an exception.
    /// </summary>
    public static PCellWireFrame Serve(in PCellWireFrame frame, string op)
    {
        try
        {
            return op switch
            {
                PCellWireOp.Clip   => ServeClip(frame),
                PCellWireOp.Offset => ServeOffset(frame),
                _ => Refuse($"circuitRF does not provide '{op}'."),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Refuse($"'{op}' failed: {ex.Message}");
        }
    }

    // ── clip ──────────────────────────────────────────────────────────────────

    private static PCellWireFrame ServeClip(in PCellWireFrame frame)
    {
        var request = JsonSerializer.Deserialize<PCellWireClipRequest>(frame.Json, PCellWireJson.Options)
                      ?? throw new PCellWireException("A clip request arrived empty.");

        ClipType clipType = request.Rule switch
        {
            PCellWireClipRule.And => ClipType.Intersection,
            PCellWireClipRule.Or  => ClipType.Union,
            PCellWireClipRule.Not => ClipType.Difference,
            PCellWireClipRule.Xor => ClipType.Xor,
            _ => throw new PCellWireException($"Unknown clip rule '{request.Rule}'."),
        };

        var span = frame.Payload.Span;
        int at = 0;
        var subject = ReadRings(request.Subject, span, ref at, "subject");
        var clip    = ReadRings(request.Clip,    span, ref at, "clip");

        if (at != span.Length)
            throw new PCellWireException(
                $"A clip request described {at} coordinates but sent {span.Length}. " +
                "The counts and the payload disagree.");

        var tree = new PolyTree64();
        Clipper.BooleanOp(clipType, subject, clip, tree, LayoutClipper.Rule);

        var payload = new List<long>();
        var polygons = new List<PCellWireClipPolygon>();
        CollectSolids(tree, polygons, payload);

        var reply = new PCellWireRegionReply { Ok = true, Polygons = polygons };
        return new PCellWireFrame(JsonSerializer.Serialize(reply, PCellWireJson.Options), payload.ToArray());
    }

    // ── offset ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Grow or shrink one region.
    ///
    /// <para>Uses the SAME join and end style circuitRF's own <c>LayoutBooleans.Offset</c> uses
    /// (mitred joins, closed-polygon ends) — a script's grow and the editor's Offset command must
    /// produce the same geometry, which is the whole reason this is a host call and not a second
    /// implementation.</para>
    ///
    /// <para>A shrink that consumes the region entirely yields NO polygons, which is a legitimate
    /// answer rather than a failure — the same outcome the editor's own Offset produces, and the
    /// script sees an empty result.</para>
    /// </summary>
    private static PCellWireFrame ServeOffset(in PCellWireFrame frame)
    {
        var request = JsonSerializer.Deserialize<PCellWireOffsetRequest>(frame.Json, PCellWireJson.Options)
                      ?? throw new PCellWireException("An offset request arrived empty.");

        var span = frame.Payload.Span;
        int at = 0;
        var subject = ReadRings(request.Subject, span, ref at, "subject");

        if (at != span.Length)
            throw new PCellWireException(
                $"An offset request described {at} coordinates but sent {span.Length}. " +
                "The counts and the payload disagree.");

        var inflated = Clipper.InflatePaths(subject, request.DeltaDbu, JoinType.Miter, EndType.Polygon);

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, inflated, new Paths64(), tree, LayoutClipper.Rule);

        var payload = new List<long>();
        var polygons = new List<PCellWireClipPolygon>();
        CollectSolids(tree, polygons, payload);

        var reply = new PCellWireRegionReply { Ok = true, Polygons = polygons };
        return new PCellWireFrame(JsonSerializer.Serialize(reply, PCellWireJson.Options), payload.ToArray());
    }

    /// <summary>
    /// Reads one operand's rings out of the payload.
    ///
    /// <para><b>Every ring is normalised to positive orientation, deliberately.</b> Under
    /// <see cref="LayoutClipper.Rule"/> (NonZero, stated once for the whole repository and never
    /// varied per call site) two rings of opposite winding CANCEL rather than combine — so a set of
    /// separate figures whose winding a generator never thought about would silently lose regions.
    /// Normalising makes an operand's rings a plain union, which is what a list of figures means.
    /// The cost is that an input ring cannot itself carry a hole; the script side does not produce
    /// one, and a donut is refused there rather than mis-clipped here.</para>
    /// </summary>
    private static Paths64 ReadRings(IReadOnlyList<int>? counts, ReadOnlySpan<long> payload,
                                     ref int at, string which)
    {
        var paths = new Paths64();
        if (counts is null) return paths;

        foreach (int vertices in counts)
        {
            if (vertices < 0)
                throw new PCellWireException($"A clip request's {which} named a ring of {vertices} vertices.");

            int needed = vertices * 2;
            if (at + needed > payload.Length)
                throw new PCellWireException(
                    $"A clip request's {which} ran past the end of its payload. The stream is out of step.");

            var path = new Path64(vertices);
            for (int i = 0; i < vertices; i++, at += 2)
                path.Add(new Point64(payload[at], payload[at + 1]));

            if (Clipper.Area(path) < 0) path.Reverse();
            paths.Add(path);
        }

        return paths;
    }

    /// <summary>
    /// Flattens Clipper2's result tree into outer-ring-plus-holes groups, in a fixed walk order —
    /// the same shape and the same determinism discipline as
    /// <see cref="LayoutClipper.FromClipperTree"/>, but stopping at rings rather than building
    /// <c>LayoutShape</c>s, since what crosses back is coordinates.
    /// </summary>
    private static void CollectSolids(PolyPath64 node, List<PCellWireClipPolygon> polygons, List<long> payload)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var solid = node[i];              // IsHole == false at this recursion level
            var holes = new List<int>();

            Append(solid.Polygon, payload);

            for (int h = 0; h < solid.Count; h++)
            {
                var hole = solid[h];
                Append(hole.Polygon, payload);
                holes.Add(hole.Polygon?.Count ?? 0);
            }

            polygons.Add(new PCellWireClipPolygon
            {
                Outer = solid.Polygon?.Count ?? 0,
                Holes = holes,
            });

            // An island inside a hole is a separate solid, exactly as the tree already says — but it
            // is emitted only once THIS polygon is complete, so that the order of the descriptions
            // and the order of the coordinates stay in step. Recursing inside the loop above would
            // interleave an island's coordinates into the middle of its parent's holes.
            for (int h = 0; h < solid.Count; h++)
                CollectSolids(solid[h], polygons, payload);
        }
    }

    private static void Append(Path64? path, List<long> payload)
    {
        if (path is null) return;
        foreach (var p in path) { payload.Add(p.X); payload.Add(p.Y); }
    }

    private static PCellWireFrame Refuse(string message)
        => new(JsonSerializer.Serialize(new PCellWireRegionReply { Ok = false, Error = message },
                                        PCellWireJson.Options));
}
