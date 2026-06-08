using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Theming;

/// <summary>
/// Framework-free RGBA color value. Used only inside the theme model — no SKColor or Avalonia types here.
/// </summary>
public readonly record struct Rgba
{
    [JsonPropertyName("r")] [JsonPropertyOrder(0)] public byte R { get; init; }
    [JsonPropertyName("g")] [JsonPropertyOrder(1)] public byte G { get; init; }
    [JsonPropertyName("b")] [JsonPropertyOrder(2)] public byte B { get; init; }
    [JsonPropertyName("a")] [JsonPropertyOrder(3)] public byte A { get; init; }

    public Rgba(byte r, byte g, byte b, byte a = 255)
    {
        R = r; G = g; B = b; A = a;
    }
}
