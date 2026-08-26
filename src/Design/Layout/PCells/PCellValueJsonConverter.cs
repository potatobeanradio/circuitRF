using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// On-disk form of a <see cref="PCellValue"/> in a <c>.clay</c>.
///
/// <para><b>A Real is a bare JSON number, and that is what makes this additive rather than a format
/// change.</b> Every PCell parameter that can exist in a workspace written before contract version 2
/// is a Real — there was no way to author anything else — so an existing file reads back with every
/// value identical, and a file holding only Reals writes back byte-identical. No
/// <c>FormatVersion</c> bump: an older build reading a newer file sees numbers where it expects
/// numbers, and only a genuinely new kind (which it could not have produced) is unreadable to it.</para>
///
/// <para><b>Int is the one kind that needs a tag, and skipping it would be a silent corruption
/// rather than a cosmetic loss.</b> JSON has no integer/real distinction — <c>4</c> and <c>4.0</c>
/// are one token — but the two are DIFFERENT inputs to the content hash that names a generated cell
/// folder. Writing an Int bare would reload it as a Real, hash to a different folder name, and leave
/// every instance whose <c>CellRef</c> names the old one dangling. Bool and String need no tag: JSON
/// distinguishes them from a number natively.</para>
/// </summary>
public sealed class PCellValueJsonConverter : JsonConverter<PCellValue>
{
    /// <summary>The single property name marking the tagged Int form. Changing it is a file-format
    /// change.</summary>
    private const string IntTag = "int";

    public override PCellValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number: return PCellValue.Real(reader.GetDouble());
            case JsonTokenType.String: return PCellValue.Text(reader.GetString() ?? "");
            case JsonTokenType.True:   return PCellValue.Bool(true);
            case JsonTokenType.False:  return PCellValue.Bool(false);
            case JsonTokenType.Null:   return PCellValue.Real(0.0);

            case JsonTokenType.StartObject:
            {
                long? value = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    bool isInt = reader.ValueTextEquals(IntTag);
                    reader.Read();
                    if (isInt && reader.TokenType == JsonTokenType.Number) value = reader.GetInt64();
                    else reader.Skip();
                }
                // An object that is not the Int form is a value this build does not understand.
                // Refusing is deliberate: guessing a kind would put the wrong value into the content
                // hash, which reads as a cell that silently regenerated rather than as a bad file.
                return value is { } v
                    ? PCellValue.Int(v)
                    : throw new JsonException($"Unrecognised PCell parameter value — expected a number, string, boolean, or {{\"{IntTag}\": n}}.");
            }

            default:
                throw new JsonException($"Unrecognised PCell parameter value token '{reader.TokenType}'.");
        }
    }

    public override void Write(Utf8JsonWriter writer, PCellValue value, JsonSerializerOptions options)
    {
        switch (value.Kind)
        {
            case PCellValueKind.Real:   writer.WriteNumberValue(value.AsReal()); break;
            case PCellValueKind.Bool:   writer.WriteBooleanValue(value.AsBool()); break;
            case PCellValueKind.String: writer.WriteStringValue(value.AsText()); break;
            case PCellValueKind.Int:
                writer.WriteStartObject();
                writer.WriteNumber(IntTag, value.AsInt());
                writer.WriteEndObject();
                break;
            default: throw new JsonException($"Unhandled PCell value kind '{value.Kind}'.");
        }
    }
}
