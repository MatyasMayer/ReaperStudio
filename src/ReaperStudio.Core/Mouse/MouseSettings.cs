using ReaperStudio.Core.Lighting;

namespace ReaperStudio.Core.Mouse;

public enum MouseSource
{
    /// <summary>Derive the mouse colours from the profile's keyboard lighting.</summary>
    FollowKeyboard,
    /// <summary>Use <see cref="MouseSettings.Wired"/> / <see cref="MouseSettings.Wireless"/>.</summary>
    Custom,
    /// <summary>Leave whatever the mouse shows.</summary>
    Unchanged,
}

public enum MouseLookMode
{
    /// <summary>Each zone has its own effect (solid or breathing; verified per zone).</summary>
    PerZone,
    /// <summary>One firmware effect over all zones (the vendor app's only mode).</summary>
    WholeMouse,
}

/// <summary>Effects verified to work on a single zone.</summary>
public enum ZoneEffect
{
    Off,
    Solid,
    Breathing,
}

public sealed class MouseZoneLook
{
    public ZoneEffect Effect { get; set; } = ZoneEffect.Solid;
    public Rgb Color { get; set; } = new(255, 0, 0);
}

/// <summary>What the mouse shows on one link.</summary>
public sealed class MouseLook
{
    public MouseLookMode Mode { get; set; } = MouseLookMode.PerZone;
    public MouseZoneLook Wheel { get; set; } = new();
    public MouseZoneLook Left { get; set; } = new();
    public MouseZoneLook Right { get; set; } = new();

    public MouseEffectMode WholeEffect { get; set; } = MouseEffectMode.Glorious;
    public Rgb Color1 { get; set; } = new(255, 0, 80);
    public Rgb Color2 { get; set; } = new(0, 120, 255);

    /// <summary>0 (slow) .. 100 (fast), like the vendor app's slider.</summary>
    public int Speed { get; set; } = 60;

    public MouseZoneLook this[MouseZone zone] => zone switch
    {
        MouseZone.Wheel => Wheel,
        MouseZone.Left => Left,
        MouseZone.Right => Right,
        _ => throw new ArgumentOutOfRangeException(nameof(zone)),
    };

    public static bool UsesColor1(MouseEffectMode m) => m is MouseEffectMode.Breathing or MouseEffectMode.SingleColor
        or MouseEffectMode.BreathingSingleColor or MouseEffectMode.Rave;
    public static bool UsesColor2(MouseEffectMode m) => m is MouseEffectMode.Breathing or MouseEffectMode.Rave;
    public static bool UsesSpeed(MouseEffectMode m) => m is not (MouseEffectMode.Off or MouseEffectMode.SingleColor);
}

/// <summary>Mouse part of a profile.</summary>
public sealed class MouseSettings
{
    public MouseSource Source { get; set; } = MouseSource.FollowKeyboard;
    public MouseLook Wired { get; set; } = new();
    public MouseLook Wireless { get; set; } = new();
    /// <summary>False: <see cref="Wired"/> is used on both links.</summary>
    public bool SeparateWireless { get; set; }
    public int WiredBrightness { get; set; } = 100;
    public int WirelessBrightness { get; set; } = 60;

    public MouseLook LookFor(MouseLink link) => link == MouseLink.Wireless && SeparateWireless ? Wireless : Wired;
}
