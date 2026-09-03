// GDSII's 8-byte "real" number: excess-64, base-16 floating point — NOT IEEE 754. This is the single
// most common GDSII implementation bug (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §2.1 item 1).
// Byte 0: sign bit (MSB) + 7-bit exponent, biased by 64 (excess-64). Bytes 1-7: a 56-bit mantissa
// fraction, value = mantissaFraction * 16^(exponent-64). Zero is all-zero bytes.

namespace CircuitRF.Design.Layout.Interchange;

public static class GdsiiReal8
{
    private const double TwoPow56 = 72057594037927936.0; // 2^56
    private const int ExcessBias = 64;

    public static double ToDouble(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 8)
            throw new ArgumentException("GDSII 8-byte real requires exactly 8 bytes.", nameof(bytes));

        byte b0 = bytes[0];
        bool negative = (b0 & 0x80) != 0;
        int exponent = b0 & 0x7F;

        ulong mantissaBits = 0;
        for (int i = 1; i < 8; i++)
            mantissaBits = (mantissaBits << 8) | bytes[i];

        if (mantissaBits == 0) return 0.0;

        double mantissaFraction = mantissaBits / TwoPow56;
        double value = mantissaFraction * Math.Pow(16.0, exponent - ExcessBias);
        return negative ? -value : value;
    }

    public static void WriteTo(Span<byte> dest, double value)
    {
        if (dest.Length != 8)
            throw new ArgumentException("GDSII 8-byte real requires exactly 8 bytes.", nameof(dest));

        if (value == 0.0)
        {
            dest.Clear();
            return;
        }

        bool negative = value < 0;
        double mag = Math.Abs(value);
        int exponent = ExcessBias;

        // Normalize mag into [1/16, 1) — the base-16 equivalent of a normalized IEEE mantissa.
        while (mag >= 1.0) { mag /= 16.0; exponent++; }
        while (mag < 1.0 / 16.0) { mag *= 16.0; exponent--; }

        ulong mantissaBits = (ulong)Math.Round(mag * TwoPow56, MidpointRounding.AwayFromZero);

        // Rounding can push the mantissa up to exactly 2^56 (one past the normalized range) —
        // renormalize rather than let it overflow into the sign/exponent byte.
        if (mantissaBits >= 1UL << 56)
        {
            mantissaBits >>= 4;
            exponent++;
        }

        dest[0] = (byte)((negative ? 0x80 : 0x00) | (exponent & 0x7F));
        for (int i = 7; i >= 1; i--)
        {
            dest[i] = (byte)(mantissaBits & 0xFF);
            mantissaBits >>= 8;
        }
    }

    public static byte[] FromDouble(double value)
    {
        var bytes = new byte[8];
        WriteTo(bytes, value);
        return bytes;
    }
}
