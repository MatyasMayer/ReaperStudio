using ReaperStudio.Core.Lighting;

namespace ReaperStudio.Core.Protocol;

/// <summary>Built-in effects stored in the keyboard firmware (message 02 29, byte 2).</summary>
public enum EffectMode : byte
{
    NeonStream = 0x01,
    RipplesShining = 0x02,
    RotatingWindmill = 0x03,
    SineWave = 0x04,
    RainbowRoulette = 0x05,
    StarsTwinkle = 0x06,
    LayerUponLayer = 0x07,
    RichAndHonored = 0x08,
    Marquee = 0x09,
    RotatingStorm = 0x0A,
    SerpentineHorseRace = 0x0B,
    RetroSnake = 0x0C,
    DiagonalTransformation = 0x0D,
    Customize = 0x0E,
    Ambilight = 0x0F,
    Streamer = 0x10,
    Steady = 0x11,
    Breathing = 0x12,
    Neon = 0x13,
    ShadowDisappear = 0x14,
    FlashAway = 0x15,
}

public sealed record HardwareEffect
{
    public EffectMode Mode { get; init; } = EffectMode.Steady;
    /// <summary>1 (slow) .. 5 (fast).</summary>
    public int Speed { get; init; } = 3;
    /// <summary>0 (off) .. 5 (max).</summary>
    public int Brightness { get; init; } = 5;
    public Rgb Color { get; init; } = new(255, 0, 0);
    /// <summary>Use rainbow colours instead of <see cref="Color"/>.</summary>
    public bool Colorful { get; init; }
    public int Direction { get; init; } = 1;

    /// <summary>Which controls make sense for each mode (vendor KB.ini LedOpt table).</summary>
    public static bool SupportsSpeed(EffectMode m) => m is not (EffectMode.Steady or EffectMode.Customize);
    public static bool SupportsDirection(EffectMode m) =>
        m is not (EffectMode.RotatingWindmill or EffectMode.RainbowRoulette or EffectMode.Streamer
            or EffectMode.Neon or EffectMode.Steady or EffectMode.Customize or EffectMode.Breathing);
    public static bool SupportsColor(EffectMode m) =>
        m is not (EffectMode.RotatingWindmill or EffectMode.RainbowRoulette or EffectMode.Streamer
            or EffectMode.Neon or EffectMode.Customize);
}

public static class Messages
{
    public const int LedBytes = LedFrame.LedCount * 3;

    /// <summary>02 29: select a built-in effect.</summary>
    public static byte[] Effect(HardwareEffect e)
    {
        var m = new byte[60];
        m[0] = 0x02; m[1] = 0x29;
        m[2] = (byte)e.Mode;
        m[4] = (byte)Math.Clamp(e.Speed, 1, 5);
        m[5] = (byte)Math.Clamp(e.Brightness, 0, 5);
        m[6] = e.Color.R; m[7] = e.Color.G; m[8] = e.Color.B;
        m[9] = (byte)(e.Colorful ? 1 : 0);
        m[10] = (byte)e.Direction;
        return m;
    }

    /// <summary>06 00: volatile real-time frame.</summary>
    public static byte[] RealtimeFrame(LedFrame frame)
    {
        var m = new byte[2 + LedBytes];
        m[0] = 0x06;
        WriteLeds(frame, m.AsSpan(2));
        return m;
    }

    /// <summary>03 7E: per-key colours stored in flash for the Customize effect.</summary>
    public static byte[] CustomColors(int slot, LedFrame frame)
    {
        var m = new byte[3 + LedBytes];
        m[0] = 0x03; m[1] = 0x7E; m[2] = (byte)slot;
        WriteLeds(frame, m.AsSpan(3));
        return m;
    }

    private static void WriteLeds(LedFrame frame, Span<byte> dst)
    {
        var leds = frame.Leds;
        for (int i = 0; i < leds.Length; i++)
        {
            dst[i * 3] = leds[i].R;
            dst[i * 3 + 1] = leds[i].G;
            dst[i * 3 + 2] = leds[i].B;
        }
    }
}
