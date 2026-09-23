// Writes a .clay coordinate array as ONE VERTEX PER LINE — "x, y" — instead of one number per line.
//
// ── WHY, AND WHY NOT FURTHER ─────────────────────────────────────────────────────────────────────
//
// Field report, 2026-09-23: a Gerber-imported board's .clay was 57 MB from a 359 kB zip. Measured on
// a smaller board of the same shape, 52 % of the file was the indented writer putting every
// coordinate on a line of its own; the geometry itself is ordinary. Collapsing a whole array onto one
// line would have saved the most, and is exactly what must NOT happen: a workspace's history is kept
// with git, so a line is the unit a diff shows, and one moved vertex has to stay a one-line change
// rather than rewriting a pour of ninety thousand points. A vertex per line keeps that and halves the
// line count.
//
// READING IS UNCHANGED — any JSON array of integers reads, whatever its layout, so every .clay ever
// written still opens, and this only changes what the next save writes.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Layout;

/// <summary>
/// A <c>long[]</c> of interleaved x, y database units — every coordinate array a <c>.clay</c> holds —
/// written one vertex per line when the writer is indented.
/// </summary>
public sealed class CoordinatePairsJsonConverter : JsonConverter<long[]>
{
    public override long[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("A coordinate list is a JSON array of integers.");

        var values = new List<long>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return [.. values];
            values.Add(reader.GetInt64());
        }
        throw new JsonException("A coordinate list ended before its closing bracket.");
    }

    public override void Write(Utf8JsonWriter writer, long[] value, JsonSerializerOptions options)
    {
        // Compact output, an empty list and an odd count (not a list of vertices) take the ordinary
        // spelling — the last so nothing here ever pairs a number with the wrong partner.
        if (!writer.Options.Indented || value.Length == 0 || value.Length % 2 != 0)
        {
            writer.WriteStartArray();
            foreach (long v in value) writer.WriteNumberValue(v);
            writer.WriteEndArray();
            return;
        }

        string newline = writer.Options.NewLine;
        string outer = new(writer.Options.IndentCharacter, writer.CurrentDepth * writer.Options.IndentSize);
        string inner = outer + new string(writer.Options.IndentCharacter, writer.Options.IndentSize);

        var text = new StringBuilder(value.Length * 12 + 8);
        text.Append('[');
        for (int i = 0; i < value.Length; i += 2)
        {
            text.Append(newline).Append(inner)
                .Append(value[i]).Append(", ").Append(value[i + 1]);
            if (i + 2 < value.Length) text.Append(',');
        }
        text.Append(newline).Append(outer).Append(']');

        writer.WriteRawValue(text.ToString(), skipInputValidation: true);
    }
}
