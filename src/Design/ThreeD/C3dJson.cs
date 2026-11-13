// brief-em3d-41 R-em3d41-2 — how a .c3d's numbers are read and written.
//
// Two jobs. WRITING: a point is one line, "[x, y, z]", so a list of vertices is a vertex per line and a
// moved vertex is a one-line diff — the rule CoordinatePairsJsonConverter states for .clay. READING:
// every number is an integer, and a STRING where a number belongs is refused by name (R-em3d41-2d)
// rather than by System.Text.Json's "could not be converted to System.Int64". Since brief 51 a NAMED
// dimension may hold an expression — an object carrying its unit, read by C3dBindings, not here — and since brief 132
// so may a wire's or a polyline's points (C3dBindings too), and since brief-hzn-13 a prism's or a sheet's outline; what these
// readers see is a hole or a polyhedron's vertices, which hold numbers only, and the refusal says so.

using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.ThreeD;

/// <summary>The shared number reads.</summary>
internal static class C3dJson
{
    public static long ReadInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
            throw new JsonException(C3dDiagnostics.ExpressionNotYet(reader.GetString() ?? "").Render());
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException(C3dDiagnostics.NumberExpected(reader.TokenType.ToString()).Render());
        if (reader.TryGetInt64(out long v)) return v;
        throw new JsonException(C3dDiagnostics.IntegerExpected(
            Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan)).Render());
    }

    public static int ReadInt32(ref Utf8JsonReader reader)
    {
        long v = ReadInt64(ref reader);
        if (v is < int.MinValue or > int.MaxValue)
            throw new JsonException(C3dDiagnostics.IntegerExpected(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).Render());
        return (int)v;
    }

    /// <summary>A list of integers written on one line, "[a, b, c]", through the raw-value path so an
    /// indented writer puts it where a value goes and does not break it up.</summary>
    public static void WriteInline(Utf8JsonWriter writer, IEnumerable<long> values)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (long v in values)
        {
            if (!first) sb.Append(", ");
            sb.Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            first = false;
        }
        sb.Append(']');
        writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
    }

    public static List<long> ReadArray(ref Utf8JsonReader reader, string what)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException(C3dDiagnostics.ArrayExpected(what).Render());
        var values = new List<long>(3);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return values;
            values.Add(ReadInt64(ref reader));
        }
        throw new JsonException(C3dDiagnostics.ArrayExpected(what).Render());
    }
}

/// <summary>
/// A list of points, or a list of such lists — written a point per line. Its own converter because
/// <see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/> puts no line break before an ARRAY ELEMENT,
/// so one-line points left to the indented writer would all land on the list's first line. Every list
/// this writes is a property's value, which is where the writer does place a raw value correctly.
/// </summary>
internal static class C3dListLayout
{
    public static string Indent(Utf8JsonWriter writer, int extra)
        => new(writer.Options.IndentCharacter, (writer.CurrentDepth + extra) * writer.Options.IndentSize);

    /// <summary>Items, one per line, one level in from <paramref name="depth"/>.</summary>
    public static void AppendLines(StringBuilder sb, Utf8JsonWriter writer, int depth, IReadOnlyList<string> items)
    {
        sb.Append('[');
        if (items.Count == 0) { sb.Append(']'); return; }
        string nl = writer.Options.NewLine;
        for (int i = 0; i < items.Count; i++)
        {
            sb.Append(nl).Append(Indent(writer, depth + 1)).Append(items[i]);
            if (i + 1 < items.Count) sb.Append(',');
        }
        sb.Append(nl).Append(Indent(writer, depth)).Append(']');
    }

    public static string Inline(IEnumerable<long> values)
        => "[" + string.Join(", ", values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

    public static List<T> ReadList<T>(ref Utf8JsonReader reader, string shape, ReadOne<T> one)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException(C3dDiagnostics.ArrayExpected(shape).Render());
        var items = new List<T>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return items;
            items.Add(one(ref reader));
        }
        throw new JsonException(C3dDiagnostics.ArrayExpected(shape).Render());
    }

    public delegate T ReadOne<T>(ref Utf8JsonReader reader);

    public static C3dPoint2 ReadPoint2(ref Utf8JsonReader reader)
    {
        var v = C3dJson.ReadArray(ref reader, "[u, v]");
        if (v.Count != 2) throw new JsonException(C3dDiagnostics.WrongArity("[u, v]", 2, v.Count).Render());
        return new C3dPoint2(v[0], v[1]);
    }

    public static C3dPoint3 ReadPoint3(ref Utf8JsonReader reader)
    {
        var v = C3dJson.ReadArray(ref reader, "[x, y, z]");
        if (v.Count != 3) throw new JsonException(C3dDiagnostics.WrongArity("[x, y, z]", 3, v.Count).Render());
        return new C3dPoint3(v[0], v[1], v[2]);
    }

    public static List<int> ReadIndices(ref Utf8JsonReader reader)
        => ReadList(ref reader, "[integer]", static (ref Utf8JsonReader r) => C3dJson.ReadInt32(ref r));

    public static string Line(C3dPoint2 p) => Inline([p.U, p.V]);
    public static string Line(C3dPoint3 p) => Inline([p.X, p.Y, p.Z]);
}

/// <summary>An outline or a polyline's points: a point per line.</summary>
internal sealed class C3dPoint2ListJsonConverter : JsonConverter<List<C3dPoint2>>
{
    public override List<C3dPoint2> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => C3dListLayout.ReadList(ref reader, "[[u, v], …]", C3dListLayout.ReadPoint2);

    public override void Write(Utf8JsonWriter writer, List<C3dPoint2> value, JsonSerializerOptions options)
    {
        var sb = new StringBuilder();
        C3dListLayout.AppendLines(sb, writer, 0, [.. value.Select(C3dListLayout.Line)]);
        writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
    }
}

/// <summary>A polyhedron's vertices: a vertex per line.</summary>
internal sealed class C3dPoint3ListJsonConverter : JsonConverter<List<C3dPoint3>>
{
    public override List<C3dPoint3> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => C3dListLayout.ReadList(ref reader, "[[x, y, z], …]", C3dListLayout.ReadPoint3);

    public override void Write(Utf8JsonWriter writer, List<C3dPoint3> value, JsonSerializerOptions options)
    {
        var sb = new StringBuilder();
        C3dListLayout.AppendLines(sb, writer, 0, [.. value.Select(C3dListLayout.Line)]);
        writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
    }
}

/// <summary>A prism's or a sheet's holes: a list of rings, each a point per line.</summary>
internal sealed class C3dRingListJsonConverter : JsonConverter<List<List<C3dPoint2>>>
{
    public override List<List<C3dPoint2>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => C3dListLayout.ReadList(ref reader, "[[[u, v], …], …]",
            static (ref Utf8JsonReader r) => C3dListLayout.ReadList(ref r, "[[u, v], …]", C3dListLayout.ReadPoint2));

    public override void Write(Utf8JsonWriter writer, List<List<C3dPoint2>> value, JsonSerializerOptions options)
    {
        var rings = new List<string>(value.Count);
        foreach (var ring in value)
        {
            var one = new StringBuilder();
            C3dListLayout.AppendLines(one, writer, 1, [.. ring.Select(C3dListLayout.Line)]);
            rings.Add(one.ToString());
        }
        var sb = new StringBuilder();
        C3dListLayout.AppendLines(sb, writer, 0, rings);
        writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
    }
}

/// <summary>A face's hole loops: a loop per line.</summary>
internal sealed class C3dIndexLoopsJsonConverter : JsonConverter<List<List<int>>>
{
    public override List<List<int>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => C3dListLayout.ReadList(ref reader, "[[integer], …]", C3dListLayout.ReadIndices);

    public override void Write(Utf8JsonWriter writer, List<List<int>> value, JsonSerializerOptions options)
    {
        var sb = new StringBuilder();
        C3dListLayout.AppendLines(sb, writer, 0, [.. value.Select(l => C3dListLayout.Inline(l.Select(i => (long)i)))]);
        writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
    }
}

/// <summary><c>[x, y, z]</c>, on one line. Always a property's value (a list of them is
/// <see cref="C3dPoint3ListJsonConverter"/>'s).</summary>
public sealed class C3dPoint3JsonConverter : JsonConverter<C3dPoint3>
{
    public override C3dPoint3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => C3dListLayout.ReadPoint3(ref reader);

    public override void Write(Utf8JsonWriter writer, C3dPoint3 value, JsonSerializerOptions options)
        => C3dJson.WriteInline(writer, [value.X, value.Y, value.Z]);
}

/// <summary><c>[u, v]</c>.</summary>
public sealed class C3dPoint2JsonConverter : JsonConverter<C3dPoint2>
{
    public override C3dPoint2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => C3dListLayout.ReadPoint2(ref reader);

    public override void Write(Utf8JsonWriter writer, C3dPoint2 value, JsonSerializerOptions options)
        => C3dJson.WriteInline(writer, [value.U, value.V]);
}

/// <summary>A face's outer loop, and an array's counts: one line. Always a property's value.</summary>
internal sealed class C3dIndexListJsonConverter : JsonConverter<List<int>>
{
    public override List<int> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => C3dListLayout.ReadIndices(ref reader);

    public override void Write(Utf8JsonWriter writer, List<int> value, JsonSerializerOptions options)
        => C3dJson.WriteInline(writer, value.Select(v => (long)v));
}

/// <summary>Every scalar integer field: refuses a string by name.</summary>
internal sealed class C3dInt64JsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => C3dJson.ReadInt64(ref reader);

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

internal sealed class C3dInt32JsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => C3dJson.ReadInt32(ref reader);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>Every scalar real field (a rotation's degrees, a sheet's thickness): refuses a string by
/// name, for the same reason.</summary>
internal sealed class C3dDoubleJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            throw new JsonException(C3dDiagnostics.ExpressionNotYet(reader.GetString() ?? "").Render());
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException(C3dDiagnostics.NumberExpected(reader.TokenType.ToString()).Render());
        return reader.GetDouble();
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
