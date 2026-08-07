using System.Text.Json;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>
/// Turns circuitRF's own types into wire messages and back. See
/// <c>docs/design/pcell-wire-schema.md</c>.
///
/// <para><b>This class is where §1 is enforced rather than described.</b> A length crosses as int64
/// DBU, converted here with <see cref="PCellUnits.MetresToDbu"/> — the same function an in-process
/// generator calls — and <c>dbuPerMicron</c> is never written into a message. A script therefore has
/// no metre and no resolution to do its own conversion with, which is what keeps R7's single
/// rounding rule true across a process boundary.</para>
/// </summary>
public static class PCellWireCodec
{
    // ── Requests (host → generator) ───────────────────────────────────────────

    public static PCellWireFrame EncodeDescribe()
        => new(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["op"]              = PCellWireOp.Describe,
            ["wireVersion"]     = PCellWireVersion.Current,
            ["contractVersion"] = PCellContractVersion.Current,
        }, PCellWireJson.Options));

    public static PCellWireFrame EncodeShutdown()
        => new(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["op"] = PCellWireOp.Shutdown,
        }, PCellWireJson.Options));

    /// <summary>
    /// Builds a <c>generate</c> request. <paramref name="declarations"/> is what the generator said
    /// in <c>describe</c>: it decides which parameters are lengths, and therefore which are converted
    /// to DBU. A parameter the generator never declared is passed through unconverted — the host must
    /// not guess a dimension it was not told, and a generator that wanted a conversion had one job.
    /// </summary>
    public static PCellWireFrame EncodeGenerate(
        string generatorId,
        IReadOnlyDictionary<string, PCellValue> parametersInSi,
        IReadOnlyList<PCellWireParameterDecl> declarations,
        Technology? technology,
        PCellLayerSelection layerSelection,
        int dbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(parametersInSi);
        ArgumentNullException.ThrowIfNull(declarations);

        var dims = new Dictionary<string, PCellWireDimension>(StringComparer.Ordinal);
        foreach (var d in declarations) dims[d.Name] = d.Dimension;

        var wireParams = new Dictionary<string, PCellValue>(StringComparer.Ordinal);
        foreach (var (name, value) in parametersInSi)
            wireParams[name] = dims.TryGetValue(name, out var dim) && dim == PCellWireDimension.Length
                ? ToDbuValue(value, dbuPerMicron)
                : value;

        var request = new PCellWireGenerateRequest
        {
            GeneratorId = generatorId,
            Parameters  = wireParams,
            Layers      = BuildLayers(technology, layerSelection),
            Stackup     = BuildStackup(technology),
            // Already in hand — it is what converted the length parameters two statements above.
            // Version 1 used it and threw it away; version 2 also tells the generator, so a cell
            // carrying its own process constants can express them (schema §1).
            DbuPerMicron = dbuPerMicron,
        };

        return new PCellWireFrame(JsonSerializer.Serialize(request, PCellWireJson.Options));
    }

    /// <summary>
    /// A length value's SI metres become DBU. <b>The kind is preserved</b> — a Real stays a Real, now
    /// carrying an integral DBU count. The kind describes what the parameter IS (a continuous
    /// quantity, versus a count); turning it into an Int because the value happens to be whole would
    /// contradict the rule that a parameter's kind belongs to whoever declared it.
    /// </summary>
    private static PCellValue ToDbuValue(PCellValue value, int dbuPerMicron) => value.Kind switch
    {
        PCellValueKind.Real => PCellValue.Real(PCellUnits.MetresToDbu(value.AsReal(), dbuPerMicron)),
        PCellValueKind.Int  => PCellValue.Int(PCellUnits.MetresToDbu(value.AsInt(), dbuPerMicron)),
        // A Bool or a String declared as a length is the generator contradicting itself. Passing it
        // through unchanged is the honest answer: there is nothing to convert, and inventing a
        // number would put geometry somewhere on the strength of a declaration error.
        _ => value,
    };

    private static PCellWireLayers BuildLayers(Technology? technology, PCellLayerSelection selection)
    {
        var layers = new PCellWireLayers();
        if (technology is null) return layers;

        foreach (var def in technology.Layers)
            layers.Table.Add(new PCellWireLayerDef
            {
                Layer = def.Key.Layer, Datatype = def.Key.Datatype, Name = def.Name, Purpose = def.Purpose,
            });

        // The resolved answer, computed by circuitRF's own rule — never the question.
        layers.Signal = PCellWireLayer.From(SubstrateResolver.ResolveSignalLayerKey(technology, selection, out _));

        // The ground reference resolves by conductor NAME (that is what a stackup entry is keyed
        // on); its drawing layer is that entry's own first mapped layer. A plane may legitimately map
        // to several — the first is what a generator would draw on, and offering the whole list would
        // be asking a script to re-make a choice circuitRF has already made.
        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, selection);
        if (substrate is not null)
            foreach (var entry in technology.Stackup.Layers)
                if (entry.Kind == StackupKind.Conductor
                    && string.Equals(entry.Name, substrate.GroundConductorName, StringComparison.Ordinal)
                    && entry.DrawingLayers.Count > 0)
                {
                    layers.Ground = PCellWireLayer.From(entry.DrawingLayers[0]);
                    break;
                }

        return layers;
    }

    private static PCellWireStackup? BuildStackup(Technology? technology)
    {
        if (technology is null) return null;

        var stackup = new PCellWireStackup
        {
            Top    = technology.Stackup.Top.ToString().ToLowerInvariant(),
            Bottom = technology.Stackup.Bottom.ToString().ToLowerInvariant(),
        };

        foreach (var layer in technology.Stackup.Layers)
        {
            var wire = new PCellWireStackupLayer
            {
                Kind              = layer.Kind.ToString().ToLowerInvariant(),
                Name              = layer.Name,
                Thickness         = layer.ThicknessDbu,
                IsGroundReference = layer.IsGroundReference,
            };

            // Only what the entry's own kind gives meaning to. A dielectric's conductivity and a
            // conductor's permittivity are both noise, and a script reading one would be reading a
            // default that means nothing.
            if (layer.Kind == StackupKind.Dielectric)
            {
                wire.Epsr = layer.Epsr;
                wire.Tand = layer.TanD;
                wire.Mur  = layer.Mur;
            }
            else
            {
                wire.Sigma = layer.SigmaSm;
            }

            foreach (var dl in layer.DrawingLayers) wire.DrawingLayers.Add(PCellWireLayer.From(dl));
            stackup.Layers.Add(wire);
        }

        return stackup;
    }

    // ── Replies (generator → host) ────────────────────────────────────────────

    /// <summary>
    /// Encodes a <see cref="PCellResult"/> as a reply frame. Present because a C# generator must be
    /// drivable over the wire too: B7's gate is the same cell written twice producing byte-identical
    /// geometry, and that is only checkable if both sides can speak the same format.
    /// </summary>
    public static PCellWireFrame EncodeGenerateReply(PCellResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var payload = new List<long>();
        var reply = new PCellWireGenerateReply
        {
            Diagnostics = result.Diagnostics is { Count: > 0 } d ? [.. d] : null,
        };

        foreach (var shape in result.Shapes)
        {
            var encoded = EncodeShape(shape, payload);
            if (encoded is not null) reply.Shapes.Add(encoded);
        }

        foreach (var pin in result.Pins)
            reply.Pins.Add(new PCellWirePin
            {
                Name = pin.Name, X = pin.X, Y = pin.Y,
                Layer = PCellWireLayer.From(pin.Layer),
                Width = pin.WidthDbu, OutwardDeg = pin.OutwardDirectionDeg,
            });

        if (result.Preview == PCellPreviewMode.Deferred)
            reply.Preview = PCellWirePreviewMode.Deferred;

        if (result.Handles is { Count: > 0 })
        {
            reply.Handles = new List<PCellWireHandle>(result.Handles.Count);
            foreach (var h in result.Handles)
                reply.Handles.Add(new PCellWireHandle
                {
                    Parameter = h.Parameter,
                    Kind      = h.Kind == PCellHandleKind.Angular
                                    ? PCellWireHandleKind.Angular : PCellWireHandleKind.Linear,
                    // The four coordinates go in the PAYLOAD, never the JSON — §2's guarantee that a
                    // fractional coordinate is unrepresentable holds by there being nowhere to write one.
                    Span      = Append(payload, [h.AnchorX, h.AnchorY, h.X, h.Y]),
                    AxisDeg   = h.AxisDeg,
                    Label     = h.Label,
                    Min       = h.Min,
                    Max       = h.Max,
                    CrossParameter = h.Cross?.Parameter,
                    CrossLabel     = h.Cross?.Label,
                    CrossMin       = h.Cross?.Min,
                    CrossMax       = h.Cross?.Max,
                    KeepAnchorFixed = h.KeepAnchorFixed,
                });
        }

        return new PCellWireFrame(JsonSerializer.Serialize(reply, PCellWireJson.Options), payload.ToArray());
    }

    private static PCellWireShape? EncodeShape(LayoutShape shape, List<long> payload)
    {
        var wire = new PCellWireShape { Layer = PCellWireLayer.From(shape.Layer), Net = shape.Net };

        switch (shape)
        {
            case RectShape r:
                wire.Kind = PCellWireShapeKind.Rect;
                wire.Xy   = Append(payload, [r.X1, r.Y1, r.X2, r.Y2]);
                return wire;

            case RoundedRectShape r:
                wire.Kind         = PCellWireShapeKind.RRect;
                wire.Xy           = Append(payload, [r.X1, r.Y1, r.X2, r.Y2]);
                wire.CornerRadius = r.CornerRadius;
                wire.FlattenTol   = r.FlattenTolDbu;
                return wire;

            case CircleShape c:
                wire.Kind       = PCellWireShapeKind.Circle;
                wire.Xy         = Append(payload, [c.Cx, c.Cy]);
                wire.Radius     = c.R;
                wire.FlattenTol = c.FlattenTolDbu;
                return wire;

            case PolygonShape p:
                wire.Kind  = PCellWireShapeKind.Poly;
                wire.Xy    = Append(payload, p.Xy);
                wire.Holes = AppendHoles(payload, p.Holes);
                return wire;

            case CurveShape c:
                wire.Kind       = PCellWireShapeKind.Curve;
                wire.Xy         = Append(payload, c.Xy);
                wire.Holes      = AppendHoles(payload, c.Holes);
                wire.Edges      = EncodeEdges(c.Edges, payload);
                wire.FlattenTol = c.FlattenTolDbu;
                return wire;

            case PathShape p:
                wire.Kind       = PCellWireShapeKind.Path;
                wire.Xy         = Append(payload, p.Xy);
                wire.Edges      = EncodeEdges(p.Edges, payload);
                wire.Width      = p.Width;
                wire.End        = p.End.ToString().ToLowerInvariant();
                wire.FlattenTol = p.FlattenTolDbu;
                return wire;

            case ViaShape v:
                wire.Kind         = PCellWireShapeKind.Via;
                wire.Xy           = Append(payload, [v.X, v.Y]);
                wire.PadSize      = v.PadSize;
                wire.DrillSize    = v.DrillSize;
                wire.LandingLayer = v.LandingLayer is { } l ? PCellWireLayer.From(l) : null;
                return wire;

            case LabelShape label:
                wire.Kind     = PCellWireShapeKind.Label;
                wire.Xy       = Append(payload, [label.X, label.Y]);
                wire.Text     = label.Text;
                wire.Height   = label.Height;
                wire.Rotation = label.Rotation.ToString().ToLowerInvariant();
                wire.IsPort   = label.IsPort;
                return wire;

            // BitmapShape lands here. Dropped rather than encoded — see PCellWireShapeKind.All's own
            // note on why it is permanently absent. A generator cannot produce one, so this is only
            // reachable by encoding a hand-built result.
            default:
                return null;
        }
    }

    private static List<PCellWireEdge>? EncodeEdges(List<LayoutEdge>? edges, List<long> payload)
    {
        if (edges is not { Count: > 0 }) return null;

        var list = new List<PCellWireEdge>(edges.Count);
        foreach (var e in edges)
            list.Add(e.Kind switch
            {
                EdgeKind.Arc   => new PCellWireEdge { Kind = "arc", Bulge = e.Bulge },
                EdgeKind.Cubic => new PCellWireEdge
                {
                    Kind = "cubic", Control = Append(payload, [e.C1X, e.C1Y, e.C2X, e.C2Y]),
                },
                _ => new PCellWireEdge { Kind = "line" },
            });
        return list;
    }

    private static PCellWireSpan Append(List<long> payload, IReadOnlyList<long> values)
    {
        var span = new PCellWireSpan { At = payload.Count, Count = values.Count };
        payload.AddRange(values);
        return span;
    }

    private static List<PCellWireSpan>? AppendHoles(List<long> payload, List<long[]>? holes)
    {
        if (holes is not { Count: > 0 }) return null;
        var list = new List<PCellWireSpan>(holes.Count);
        foreach (var h in holes) list.Add(Append(payload, h));
        return list;
    }

    // ── Decoding a reply ──────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a <c>generate</c> reply into circuitRF's own result type. Every failure is a
    /// <see cref="PCellWireException"/> naming what was wrong — this is the boundary where a
    /// third-party program's output becomes geometry, so nothing here is permissive.
    /// </summary>
    public static PCellResult DecodeGenerateReply(in PCellWireFrame frame)
    {
        PCellWireGenerateReply? reply;
        try { reply = JsonSerializer.Deserialize<PCellWireGenerateReply>(frame.Json, PCellWireJson.Options); }
        catch (JsonException ex) { throw new PCellWireException($"The PCell generator's reply was not valid JSON: {ex.Message}", ex); }

        if (reply is null) throw new PCellWireException("The PCell generator sent an empty reply.");
        if (!reply.Ok)
            throw new PCellWireException(reply.Error is { Length: > 0 } e
                ? $"The PCell generator refused: {e}"
                : "The PCell generator refused, without saying why.");

        var payload = frame.Payload.Span;
        var shapes  = new List<LayoutShape>(reply.Shapes.Count);
        foreach (var s in reply.Shapes) shapes.Add(DecodeShape(s, payload));

        var pins = new List<PCellPin>(reply.Pins.Count);
        foreach (var p in reply.Pins)
            pins.Add(new PCellPin(p.Name, p.X, p.Y, p.Layer.ToKey(), p.Width, p.OutwardDeg));

        var diagnostics = reply.Diagnostics is { Count: > 0 } ? new List<string>(reply.Diagnostics) : null;
        var handles = DecodeHandles(reply.Handles, payload, ref diagnostics);

        // An unrecognised preview value reads as Auto: it is a performance hint, and refusing a
        // generate over one would trade a working cell for a preference.
        var preview = string.Equals(reply.Preview, PCellWirePreviewMode.Deferred, StringComparison.Ordinal)
            ? PCellPreviewMode.Deferred : PCellPreviewMode.Auto;

        return new PCellResult(shapes, pins,
            diagnostics is { Count: > 0 } ? diagnostics : null,
            handles, preview);
    }

    private static LayoutShape DecodeShape(PCellWireShape s, ReadOnlySpan<long> payload)
    {
        if (!PCellWireShapeKind.All.Contains(s.Kind))
            throw new PCellWireException(
                $"The PCell generator emitted a shape of kind '{s.Kind}', which this build does not know. " +
                "It is refused rather than skipped: a silently dropped shape leaves a cell that renders, " +
                "looks complete, and is missing a piece.");

        var layer = s.Layer.ToKey();

        switch (s.Kind)
        {
            case PCellWireShapeKind.Rect:
            {
                var v = Read(s.Xy, payload, 4, s.Kind);
                return new RectShape { Layer = layer, Net = s.Net, X1 = v[0], Y1 = v[1], X2 = v[2], Y2 = v[3] };
            }
            case PCellWireShapeKind.RRect:
            {
                var v = Read(s.Xy, payload, 4, s.Kind);
                return new RoundedRectShape
                {
                    Layer = layer, Net = s.Net, X1 = v[0], Y1 = v[1], X2 = v[2], Y2 = v[3],
                    CornerRadius = s.CornerRadius ?? 0, FlattenTolDbu = s.FlattenTol,
                };
            }
            case PCellWireShapeKind.Circle:
            {
                var v = Read(s.Xy, payload, 2, s.Kind);
                return new CircleShape
                {
                    Layer = layer, Net = s.Net, Cx = v[0], Cy = v[1],
                    R = s.Radius ?? 0, FlattenTolDbu = s.FlattenTol,
                };
            }
            case PCellWireShapeKind.Poly:
                return new PolygonShape
                {
                    Layer = layer, Net = s.Net,
                    Xy    = ReadRing(s.Xy, payload, s.Kind),
                    Holes = ReadHoles(s.Holes, payload, s.Kind),
                };
            case PCellWireShapeKind.Curve:
                return new CurveShape
                {
                    Layer = layer, Net = s.Net,
                    Xy    = ReadRing(s.Xy, payload, s.Kind),
                    Holes = ReadHoles(s.Holes, payload, s.Kind),
                    Edges = DecodeEdges(s.Edges, payload),
                    FlattenTolDbu = s.FlattenTol,
                };
            case PCellWireShapeKind.Path:
                return new PathShape
                {
                    Layer = layer, Net = s.Net,
                    Xy    = ReadRing(s.Xy, payload, s.Kind),
                    Edges = DecodeEdges(s.Edges, payload),
                    Width = s.Width ?? 0,
                    End   = ParseEnum(s.End, PathEndStyle.Flush, s.Kind, "end style"),
                    FlattenTolDbu = s.FlattenTol,
                };
            case PCellWireShapeKind.Via:
            {
                var v = Read(s.Xy, payload, 2, s.Kind);
                return new ViaShape
                {
                    Layer = layer, Net = s.Net, X = v[0], Y = v[1],
                    PadSize = s.PadSize ?? 0, DrillSize = s.DrillSize ?? 0,
                    LandingLayer = s.LandingLayer?.ToKey(),
                };
            }
            default: // Label — the only remaining member of the known set.
            {
                var v = Read(s.Xy, payload, 2, s.Kind);
                return new LabelShape
                {
                    Layer = layer, Net = s.Net, X = v[0], Y = v[1],
                    Text = s.Text ?? "", Height = s.Height ?? 0,
                    Rotation = ParseEnum(s.Rotation, LayoutRotation.R0, s.Kind, "rotation"),
                    IsPort = s.IsPort ?? false,
                };
            }
        }
    }

    private static List<LayoutEdge>? DecodeEdges(List<PCellWireEdge>? edges, ReadOnlySpan<long> payload)
    {
        if (edges is not { Count: > 0 }) return null;

        var list = new List<LayoutEdge>(edges.Count);
        foreach (var e in edges)
            switch (e.Kind)
            {
                case "line":
                    list.Add(new LayoutEdge { Kind = EdgeKind.Line });
                    break;
                case "arc":
                    list.Add(new LayoutEdge { Kind = EdgeKind.Arc, Bulge = e.Bulge });
                    break;
                case "cubic":
                {
                    var c = Read(e.Control, payload, 4, "cubic edge");
                    list.Add(new LayoutEdge
                    {
                        Kind = EdgeKind.Cubic, C1X = c[0], C1Y = c[1], C2X = c[2], C2Y = c[3],
                    });
                    break;
                }
                default:
                    throw new PCellWireException(
                        $"The PCell generator emitted an edge of kind '{e.Kind}', which this build does not know.");
            }
        return list;
    }

    /// <summary>
    /// Wire version 6. <b>An unrecognised <c>kind</c> drops THAT handle and says so — it never fails
    /// the generate and never touches the cell's other handles.</b> That is what lets a further kind
    /// be added later without the next version bump becoming a cliff for anyone: a newer script
    /// talking to an older host loses the grips that host cannot draw and keeps its artwork, which
    /// is the correct degradation. The report rides on the diagnostics list because that is already
    /// the ONE channel a generate has for saying something without failing.
    /// </summary>
    private static IReadOnlyList<PCellHandle>? DecodeHandles(
        List<PCellWireHandle>? handles, ReadOnlySpan<long> payload, ref List<string>? diagnostics)
    {
        if (handles is not { Count: > 0 }) return null;

        var list = new List<PCellHandle>(handles.Count);
        var droppedKinds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var h in handles)
        {
            PCellHandleKind kind;
            if (string.Equals(h.Kind, PCellWireHandleKind.Linear, StringComparison.Ordinal))
                kind = PCellHandleKind.Linear;
            else if (string.Equals(h.Kind, PCellWireHandleKind.Angular, StringComparison.Ordinal))
                kind = PCellHandleKind.Angular;
            else
            {
                droppedKinds.Add(h.Kind);
                continue;
            }

            var cross = h.CrossParameter is { Length: > 0 } cp
                ? new PCellHandleCrossAxis(cp, h.CrossLabel, h.CrossMin, h.CrossMax)
                : null;
            var c = Read(h.Span, payload, 4, "handle");
            list.Add(new PCellHandle(h.Parameter, c[0], c[1], c[2], c[3], h.AxisDeg, kind,
                                     h.Label, h.Min, h.Max, cross, h.KeepAnchorFixed));
        }

        // Once per distinct kind, not once per handle — a cell declaring twelve grips of one unknown
        // kind is one thing to tell the user about, not twelve.
        foreach (string kind in droppedKinds.OrderBy(k => k, StringComparer.Ordinal))
        {
            diagnostics ??= [];
            diagnostics.Add(
                $"This generator declares a '{kind}' drag handle, which this build does not support. " +
                "That handle is ignored; the cell's artwork and its other handles are unaffected.");
        }

        return list.Count > 0 ? list : null;
    }

    // ── Payload access ────────────────────────────────────────────────────────

    private static long[] Read(PCellWireSpan? span, ReadOnlySpan<long> payload, int expected, string what)
    {
        var values = ReadSpan(span, payload, what);
        if (values.Length != expected)
            throw new PCellWireException(
                $"A '{what}' declared {values.Length} coordinate(s); it takes exactly {expected}.");
        return values;
    }

    private static long[] ReadRing(PCellWireSpan? span, ReadOnlySpan<long> payload, string what)
    {
        var values = ReadSpan(span, payload, what);
        if (values.Length < 4)
            throw new PCellWireException(
                $"A '{what}' declared {values.Length / 2} vertex/vertices; a run needs at least two.");
        return values;
    }

    private static List<long[]>? ReadHoles(List<PCellWireSpan>? holes, ReadOnlySpan<long> payload, string what)
    {
        if (holes is not { Count: > 0 }) return null;
        var list = new List<long[]>(holes.Count);
        foreach (var h in holes) list.Add(ReadRing(h, payload, what + " hole"));
        return list;
    }

    private static long[] ReadSpan(PCellWireSpan? span, ReadOnlySpan<long> payload, string what)
    {
        if (span is null)
            throw new PCellWireException($"A '{what}' carried no coordinates.");

        if (span.At < 0 || span.Count < 0 || (long)span.At + span.Count > payload.Length)
            throw new PCellWireException(
                $"A '{what}' pointed at coordinates {span.At}..{span.At + span.Count} of a payload " +
                $"holding {payload.Length}. The reply is not self-consistent.");

        if (span.Count % 2 != 0)
            throw new PCellWireException(
                $"A '{what}' declared {span.Count} coordinates, which is not a whole number of points.");

        return payload.Slice(span.At, span.Count).ToArray();
    }

    private static TEnum ParseEnum<TEnum>(string? text, TEnum fallback, string what, string field)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrEmpty(text)) return fallback;
        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed)) return parsed;
        throw new PCellWireException($"A '{what}' declared {field} '{text}', which this build does not know.");
    }
}
