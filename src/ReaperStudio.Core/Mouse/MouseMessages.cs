using ReaperStudio.Core.Lighting;

namespace ReaperStudio.Core.Mouse;

/// <summary>Lighting effects in the Model D firmware (effect command, byte 8).</summary>
public enum MouseEffectMode : byte
{
    Off = 0,
    /// <summary>Rainbow. On one zone it also takes over the wheel, so only use it for the whole mouse.</summary>
    Glorious = 1,
    SeamlessBreathing = 2,
    /// <summary>Breathing through up to 6 colours.</summary>
    Breathing = 3,
    SingleColor = 4,
    BreathingSingleColor = 5,
    Tail = 6,
    /// <summary>Two colours.</summary>
    Rave = 7,
    Wave = 8,
}

/// <summary>Zone byte of the effect command. Verified on the mouse: 0 wheel, 1 left stripe, 2 right stripe.</summary>
public enum MouseZone : byte
{
    Wheel = 0,
    Left = 1,
    Right = 2,
    All = 0xFF,
}

public enum MouseLink
{
    Wired,
    Wireless,
}

/// <summary>First byte of every reply.</summary>
public enum MouseReply : byte
{
    Busy = 0xA0,
    Ok = 0xA1,
    Failed = 0xA2,
    Unsupported = 0xA3,
    /// <summary>The dongle cannot reach the mouse (asleep, switched off, out of range).</summary>
    NotReachable = 0xA4,
}

/// <summary>
/// Commands for the Glorious Model D Wireless, as the 64 bytes that follow report ID 0.
/// See docs/mouse/PROTOCOL.md.
/// </summary>
public static class MouseMessages
{
    public const int CommandLength = 64;
    public const int MaxColors = 6;
    private const byte TargetMouse = 0x02;

    /// <summary>02 00: effect for one zone (or all) of hardware profile 1..3. Speed is 0 (slow) .. 100 (fast).</summary>
    public static byte[] Effect(int profile, MouseZone zone, MouseEffectMode mode, IReadOnlyList<Rgb> colors, int speed)
    {
        int count = Math.Min(colors.Count, MaxColors);
        var m = Command(size: count * 3 + 5, group: 0x02, op: 0x00);
        m[6] = (byte)Math.Clamp(profile, 1, 3);
        m[7] = (byte)zone;
        m[8] = (byte)mode;
        m[9] = 0x00; // direction, always 0 in the vendor app
        m[10] = RawSpeed(mode, speed);
        for (int i = 0; i < count; i++)
        {
            // The vendor app sends black as 1,0,0; 0,0,0 marks an unused colour slot.
            var c = colors[i].IsBlack ? new Rgb(1, 0, 0) : colors[i];
            m[11 + i * 3] = c.R;
            m[12 + i * 3] = c.G;
            m[13 + i * 3] = c.B;
        }
        return m;
    }

    /// <summary>02 02: brightness 0..100 % for one link. The firmware keeps wired and wireless values apart.</summary>
    public static byte[] Brightness(MouseLink link, int percent)
    {
        var m = Command(size: 2, group: 0x02, op: 0x02);
        m[6] = (byte)(link == MouseLink.Wired ? 1 : 0);
        m[7] = (byte)(Math.Clamp(percent, 0, 100) * 255 / 100);
        return m;
    }

    /// <summary>00 85: ask for the active hardware profile (reply byte 6).</summary>
    public static byte[] QueryProfile() => Command(size: 1, group: 0x00, op: 0x85);

    /// <summary>00 83: ask for the battery (reply byte 6 charging, byte 7 percent).</summary>
    public static byte[] QueryBattery() => Command(size: 2, group: 0x00, op: 0x83);

    /// <summary>The vendor app's mapping of its 0..100 rate slider. Lower raw values are faster.</summary>
    public static byte RawSpeed(MouseEffectMode mode, int speed)
    {
        int s = Math.Clamp(speed, 0, 100);
        return (byte)(mode is MouseEffectMode.Rave or MouseEffectMode.Wave ? (105 - s) * 2 : (105 - s) / 5);
    }

    private static byte[] Command(int size, byte group, byte op)
    {
        var m = new byte[CommandLength];
        m[2] = TargetMouse;
        m[3] = (byte)size;
        m[4] = group;
        m[5] = op;
        return m;
    }
}
