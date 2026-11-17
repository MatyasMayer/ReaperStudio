using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReaperStudio.Core.Lighting;

[JsonConverter(typeof(RgbJsonConverter))]
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    public bool IsBlack => R == 0 && G == 0 && B == 0;

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public static Rgb Parse(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6) throw new FormatException($"Bad colour '{hex}'");
        int v = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static bool TryParse(string? hex, out Rgb rgb)
    {
        try { rgb = Parse(hex ?? ""); return true; }
        catch (FormatException) { rgb = Black; return false; }
    }

    /// <summary>h in degrees [0,360), s and v in [0,1].</summary>
    public static Rgb FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x),
            3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x),
        };
        return new(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    public (double H, double S, double V) ToHsv()
    {
        double r = R / 255d, g = G / 255d, b = B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    /// <summary>Multiplies every channel by <paramref name="factor"/> in [0,1].</summary>
    public Rgb Scale(double factor) => new(ToByte(R / 255d * factor), ToByte(G / 255d * factor), ToByte(B / 255d * factor));

    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Per-channel max: lets overlay effects brighten keys without hiding the base layer.</summary>
    public static Rgb Max(Rgb a, Rgb b) => new(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));

    private static byte ToByte(double unit) => (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);

    public override string ToString() => Hex;
}

public sealed class RgbJsonConverter : JsonConverter<Rgb>
{
    public override Rgb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Rgb.Parse(reader.GetString() ?? "#000000");

    public override void Write(Utf8JsonWriter writer, Rgb value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Hex);
}
