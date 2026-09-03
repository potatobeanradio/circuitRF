// GDSII stream reader (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §2). Streams one structure
// at a time — never materializes the whole file (§2.5). Format-specific: touches only bytes and
// records, never CellFolder/Messages/dialogs — that orchestration lives in GdsiiImport.

using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>Parsed from the GDSII UNITS record — the user unit and database unit, both in meters.</summary>
public readonly record struct GdsiiUnits(double UserUnitMeters, double DbUnitMeters)
{
    /// <summary>The source file's own DBU-per-micron, for comparison against a destination
    /// <see cref="LayoutView.DbuPerMicron"/> (§2.2).</summary>
    public double SourceDbuPerMicron => 1e-6 / DbUnitMeters;
}

/// <summary>Streams a GDSII library's structures lazily. <see cref="Units"/> is available immediately
/// after <see cref="Open"/> (HEADER/BGNLIB/LIBNAME/UNITS always precede the first structure).</summary>
public sealed class GdsiiReader
{
    /// <summary>GDSII has no native text-height record; this codebase's own writer always emits a
    /// WIDTH record on TEXT to carry <c>LabelShape.Height</c> (a documented, valid use of WIDTH per
    /// the spec). A third-party file lacking it falls back to this constant.</summary>
    public const long DefaultTextHeightDbu = 1000;

    private readonly GdsiiRecordReader _records;
    private readonly List<string> _diagnostics = [];
    private bool _bgnStrAlreadyConsumed;

    public GdsiiUnits Units { get; private set; }

    /// <summary>Approximation notes accumulated while reading (arbitrary-angle snaps, non-standard
    /// PATHTYPE 4 extensions) — read after fully enumerating <see cref="ReadStructures"/>.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    private GdsiiReader(Stream stream) => _records = new GdsiiRecordReader(stream);

    public static GdsiiReader Open(Stream stream)
    {
        var reader = new GdsiiReader(stream);
        reader.ReadPreamble();
        return reader;
    }

    private void ReadPreamble()
    {
        double userUnit = 0.001, dbUnit = 1e-9;
        while (_records.TryReadNext(out var rec))
        {
            if (rec.Type == GdsiiRecordType.Units)
            {
                var v = rec.AsReal8Array();
                userUnit = v[0];
                dbUnit = v[1];
            }
            else if (rec.Type == GdsiiRecordType.BgnStr)
            {
                _bgnStrAlreadyConsumed = true;
                break;
            }
            else if (rec.Type == GdsiiRecordType.EndLib)
            {
                break;
            }
        }
        Units = new GdsiiUnits(userUnit, dbUnit);
    }

    public IEnumerable<InterchangeStructure> ReadStructures()
    {
        while (true)
        {
            if (!_bgnStrAlreadyConsumed)
            {
                if (!_records.TryReadNext(out var rec)) yield break;
                if (rec.Type == GdsiiRecordType.EndLib) yield break;
                if (rec.Type != GdsiiRecordType.BgnStr) continue;
            }
            _bgnStrAlreadyConsumed = false;
            yield return ReadOneStructure();
        }
    }

    private InterchangeStructure ReadOneStructure()
    {
        string name = "";
        var shapes = new List<LayoutShape>();
        var instances = new List<LayoutInstance>();

        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.StrName: name = rec.AsAscii(); break;
                case GdsiiRecordType.EndStr: return new InterchangeStructure(name, shapes, instances);
                case GdsiiRecordType.Boundary: shapes.Add(ReadBoundary()); break;
                case GdsiiRecordType.Path: shapes.Add(ReadPath()); break;
                case GdsiiRecordType.Text: shapes.Add(ReadText()); break;
                case GdsiiRecordType.SRef: instances.Add(ReadRef(isArray: false)); break;
                case GdsiiRecordType.ARef: instances.Add(ReadRef(isArray: true)); break;
                default: break; // BGNSTR sub-fields, unsupported/unknown records — ignore, forward-compat
            }
        }
        throw new InvalidDataException("GDSII structure is missing its ENDSTR record.");
    }

    private PolygonShape ReadBoundary()
    {
        int layer = 0, datatype = 0;
        long[] xy = [];
        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.Layer: layer = rec.AsInt16Array()[0]; break;
                case GdsiiRecordType.Datatype: datatype = rec.AsInt16Array()[0]; break;
                case GdsiiRecordType.Xy: xy = ToLongPairs(rec.AsInt32Array()); break;
                case GdsiiRecordType.EndEl:
                    return new PolygonShape { Layer = new LayerKey(layer, datatype), Xy = DropClosingDuplicate(xy) };
            }
        }
        throw new InvalidDataException("GDSII BOUNDARY is missing its ENDEL record.");
    }

    private PathShape ReadPath()
    {
        int layer = 0, datatype = 0;
        long width = 0;
        int pathType = 0;
        long bgnExtn = 0, endExtn = 0;
        long[] xy = [];
        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.Layer: layer = rec.AsInt16Array()[0]; break;
                case GdsiiRecordType.Datatype: datatype = rec.AsInt16Array()[0]; break;
                case GdsiiRecordType.Width: width = Math.Abs(rec.AsInt32Array()[0]); break;
                case GdsiiRecordType.PathType: pathType = rec.AsInt16Array()[0]; break;
                case GdsiiRecordType.BgnExtn: bgnExtn = rec.AsInt32Array()[0]; break;
                case GdsiiRecordType.EndExtn: endExtn = rec.AsInt32Array()[0]; break;
                case GdsiiRecordType.Xy: xy = ToLongPairs(rec.AsInt32Array()); break;
                case GdsiiRecordType.EndEl:
                    var end = PathEndOf(pathType, width, bgnExtn, endExtn);
                    return new PathShape { Layer = new LayerKey(layer, datatype), Xy = xy, Width = width, End = end };
            }
        }
        throw new InvalidDataException("GDSII PATH is missing its ENDEL record.");
    }

    /// <summary>PATHTYPE 0/1/2 map directly; PATHTYPE 4 maps to <see cref="PathEndStyle.Extended"/>
    /// (our model has no configurable extension length) — exact when both extensions equal
    /// <c>Width/2</c> (our own writer's convention), reported as an approximation otherwise.</summary>
    private PathEndStyle PathEndOf(int pathType, long width, long bgnExtn, long endExtn)
    {
        switch (pathType)
        {
            case 0: return PathEndStyle.Flush;
            case 1: return PathEndStyle.Round;
            case 2: return PathEndStyle.Square;
            case 4:
                long expected = width / 2;
                if (bgnExtn != expected || endExtn != expected)
                    _diagnostics.Add(
                        $"PATH with PATHTYPE 4 and non-standard BGNEXTN/ENDEXTN ({bgnExtn}/{endExtn}, " +
                        $"expected {expected}) approximated as Extended.");
                return PathEndStyle.Extended;
            default:
                _diagnostics.Add($"PATH with unrecognized PATHTYPE {pathType} approximated as Flush.");
                return PathEndStyle.Flush;
        }
    }

    private LabelShape ReadText()
    {
        int layer = 0;
        int textType = 0;
        double angle = 0;
        bool reflect = false;
        long width = DefaultTextHeightDbu;
        long x = 0, y = 0;
        string text = "";
        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.Layer: layer = rec.AsInt16Array()[0]; break;
                case GdsiiRecordType.TextType: textType = rec.AsInt16Array()[0]; break;
                case GdsiiRecordType.Strans: reflect = (rec.AsInt16Array()[0] & 0xFFFF & 0x8000) != 0; break;
                case GdsiiRecordType.Angle: angle = rec.AsReal8Array()[0]; break;
                case GdsiiRecordType.Width: width = Math.Abs(rec.AsInt32Array()[0]); break;
                case GdsiiRecordType.Xy:
                    var pts = rec.AsInt32Array();
                    x = pts[0]; y = pts[1];
                    break;
                case GdsiiRecordType.StringRec: text = rec.AsAscii(); break;
                case GdsiiRecordType.EndEl:
                    // Reflection on TEXT is not represented in LabelShape (no MirrorX field there) —
                    // a documented, minor limitation; only the rotation angle carries through.
                    if (reflect)
                        _diagnostics.Add($"TEXT \"{text}\" reflection flag ignored (labels carry rotation only).");
                    // A label's angle is carried exactly, like an instance's — LabelShape.RotationDegrees
                    // was widened past the cardinals on 2026-08-25, so the snap this path used to apply
                    // (and report) is gone along with the codec's own, R-L3d-8.
                    var (_, textDeg) = GdsiiTransformCodec.FromGdsii(false, angle);
                    return new LabelShape
                    {
                        Layer = new LayerKey(layer, 0),
                        X = x, Y = y, Text = text, Height = width, RotationDegrees = textDeg, IsPort = textType == 1,
                    };
            }
        }
        throw new InvalidDataException("GDSII TEXT is missing its ENDEL record.");
    }

    private LayoutInstance ReadRef(bool isArray)
    {
        string sname = "";
        bool reflect = false;
        double mag = 1.0, angle = 0.0;
        int cols = 1, rows = 1;
        long[] xy = [];
        while (_records.TryReadNext(out var rec))
        {
            switch (rec.Type)
            {
                case GdsiiRecordType.SName: sname = rec.AsAscii(); break;
                case GdsiiRecordType.Strans: reflect = (rec.AsInt16Array()[0] & 0xFFFF & 0x8000) != 0; break;
                case GdsiiRecordType.Mag: mag = rec.AsReal8Array()[0]; break;
                case GdsiiRecordType.Angle: angle = rec.AsReal8Array()[0]; break;
                case GdsiiRecordType.ColRow:
                    var cr = rec.AsInt16Array();
                    cols = cr[0]; rows = cr[1];
                    break;
                case GdsiiRecordType.Xy: xy = ToLongPairs(rec.AsInt32Array()); break;
                case GdsiiRecordType.EndEl:
                    // R-L3d-8: no snap, no loss report — an instance carries the file's own angle.
                    var (mirrorX, rotDeg) = GdsiiTransformCodec.FromGdsii(reflect, angle);

                    long originX = xy[0], originY = xy[1];
                    long pitchX = 0, pitchY = 0;
                    if (isArray)
                    {
                        // AREF's three points (origin, column-reference, row-reference) are already
                        // WORLD-transformed absolute coordinates (§2.1 item 5) — a compliant writer
                        // (including our own, see GdsiiWriter) writes the literal placement of the
                        // Cols-th column and Rows-th row directly, so no reader-side rotation math is
                        // needed to recover them; only division by the count. A source array whose
                        // column/row vectors are not axis-aligned (a genuinely rotated GDSII AREF from
                        // another tool) is approximated by its dominant axis — this codebase's own
                        // array model stores pitch in the PARENT's unrotated frame (a documented
                        // simplification, see LayoutInstanceTransform's own doc comment), so an
                        // arbitrarily rotated array vector cannot be stored exactly regardless.
                        long colRefX = xy[2], colRefY = xy[3];
                        long rowRefX = xy[4], rowRefY = xy[5];
                        pitchX = cols != 0 ? (colRefX - originX) / cols : 0;
                        pitchY = rows != 0 ? (rowRefY - originY) / rows : 0;
                        if (colRefY != originY || rowRefX != originX)
                            _diagnostics.Add(
                                $"AREF \"{sname}\" has non-axis-aligned column/row vectors — approximated by dominant axis.");
                    }

                    return new LayoutInstance
                    {
                        CellRef = sname, // resolved to a real relative path by GdsiiImport
                        X = originX, Y = originY,
                        RotationDegrees = rotDeg, MirrorX = mirrorX, Mag = mag,
                        Rows = isArray ? rows : 1, Cols = isArray ? cols : 1,
                        PitchX = pitchX, PitchY = pitchY,
                    };
            }
        }
        throw new InvalidDataException($"GDSII {(isArray ? "AREF" : "SREF")} is missing its ENDEL record.");
    }

    private static long[] ToLongPairs(int[] flat)
    {
        var result = new long[flat.Length];
        for (int i = 0; i < flat.Length; i++) result[i] = flat[i];
        return result;
    }

    /// <summary>BOUNDARY's XY explicitly repeats the first point as the last (§2.1 item 3); our own
    /// <c>Xy</c> convention is implicitly closed and never repeats it.</summary>
    private static long[] DropClosingDuplicate(long[] xy)
    {
        if (xy.Length < 4) return xy;
        int last = xy.Length - 2;
        if (xy[0] == xy[last] && xy[1] == xy[last + 1])
            return xy[..last];
        return xy;
    }
}
