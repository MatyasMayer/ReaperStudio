namespace ReaperStudio.Core.Lighting;

/// <summary>One colour per keyboard LED slot (126 slots, 100 wired to keys).</summary>
public sealed class LedFrame
{
    public const int LedCount = 126;

    private readonly Rgb[] _leds = new Rgb[LedCount];

    public Rgb this[int led]
    {
        get => _leds[led];
        set => _leds[led] = value;
    }

    public ReadOnlySpan<Rgb> Leds => _leds;

    public void Fill(Rgb color) => Array.Fill(_leds, color);

    public void Clear() => Array.Clear(_leds);

    public void CopyFrom(LedFrame other) => other._leds.CopyTo(_leds, 0);

    public void Scale(double factor)
    {
        if (factor >= 0.999) return;
        for (int i = 0; i < LedCount; i++) _leds[i] = _leds[i].Scale(factor);
    }

    public bool ContentEquals(LedFrame other) => _leds.AsSpan().SequenceEqual(other._leds);
}
