using ReaperStudio.Core.Keyboard;
using ReaperStudio.Core.Lighting;
using ReaperStudio.Core.Profiles;

namespace ReaperStudio.Core.Mouse;

/// <summary>One command with a slot name; a slot is only re-sent when its bytes change.</summary>
public sealed record MouseCommand(string Slot, byte[] Bytes)
{
    public bool SameAs(MouseCommand? other) => other is not null && Slot == other.Slot && Bytes.AsSpan().SequenceEqual(other.Bytes);
}

/// <summary>What a profile wants the mouse to show.</summary>
public sealed record MouseTargetLook(MouseLook Look, int WiredBrightness, int WirelessBrightness);

/// <summary>Turns a profile into mouse commands. Pure, so it is unit-tested.</summary>
public static class MouseTarget
{
    public static readonly MouseZone[] Zones = [MouseZone.Wheel, MouseZone.Left, MouseZone.Right];

    /// <summary>
    /// Speed encoded for effects that do not move (solid, off), so a speed change does not alter their
    /// bytes and rewrite the mouse's flash for nothing. The vendor app's default slider value.
    /// </summary>
    public const int FixedSpeed = 60;

    /// <summary>The look for <paramref name="link"/>, or null when the profile leaves the mouse alone.</summary>
    public static MouseTargetLook? Resolve(Profile profile, MouseLink link)
    {
        var m = profile.Mouse;
        switch (m.Source)
        {
            case MouseSource.Unchanged:
                return null;
            case MouseSource.Custom:
                return new(m.LookFor(link), m.WiredBrightness, m.WirelessBrightness);
            default:
                if (FromKeyboard(profile) is not (var look, var level)) return null;
                return new(look, m.WiredBrightness * level / 100, m.WirelessBrightness * level / 100);
        }
    }

    /// <summary>Commands for hardware profile <paramref name="hwProfile"/> (1..3): effects first, then brightness.</summary>
    public static List<MouseCommand> Commands(MouseTargetLook target, int hwProfile)
    {
        var look = target.Look;
        var list = new List<MouseCommand>();
        if (look.Mode == MouseLookMode.WholeMouse)
        {
            Rgb[] colors = look.WholeEffect switch
            {
                MouseEffectMode.Breathing or MouseEffectMode.Rave => [look.Color1, look.Color2],
                MouseEffectMode.SingleColor or MouseEffectMode.BreathingSingleColor => [look.Color1],
                MouseEffectMode.SeamlessBreathing => [new Rgb(255, 0, 0)], // what the vendor app sends
                _ => [],
            };
            int speed = MouseLook.UsesSpeed(look.WholeEffect) ? look.Speed : FixedSpeed;
            list.Add(new("fx", MouseMessages.Effect(hwProfile, MouseZone.All, look.WholeEffect, colors, speed)));
        }
        else
        {
            foreach (var zone in Zones)
            {
                var z = look[zone];
                var (mode, colors, speed) = z.Effect switch
                {
                    ZoneEffect.Solid => (MouseEffectMode.SingleColor, new[] { z.Color }, FixedSpeed),
                    ZoneEffect.Breathing => (MouseEffectMode.BreathingSingleColor, new[] { z.Color }, look.Speed),
                    _ => (MouseEffectMode.Off, Array.Empty<Rgb>(), FixedSpeed),
                };
                list.Add(new($"fx{(int)zone}", MouseMessages.Effect(hwProfile, zone, mode, colors, speed)));
            }
        }
        list.Add(new("brightness-wired", MouseMessages.Brightness(MouseLink.Wired, target.WiredBrightness)));
        list.Add(new("brightness-wireless", MouseMessages.Brightness(MouseLink.Wireless, target.WirelessBrightness)));
        return list;
    }

    /// <summary>Approximate colour of each zone, for the on-screen preview.</summary>
    public static Rgb PreviewColor(MouseLook look, MouseZone zone, double time)
    {
        if (look.Mode == MouseLookMode.PerZone)
        {
            var z = look[zone];
            return z.Effect switch
            {
                ZoneEffect.Solid => z.Color,
                ZoneEffect.Breathing => z.Color.Scale(Breath(time, look.Speed)),
                _ => Rgb.Black,
            };
        }
        double phase = time * (0.2 + look.Speed / 50.0);
        return look.WholeEffect switch
        {
            MouseEffectMode.Off => Rgb.Black,
            MouseEffectMode.SingleColor => look.Color1,
            MouseEffectMode.BreathingSingleColor => look.Color1.Scale(Breath(time, look.Speed)),
            MouseEffectMode.Breathing => (phase % 2 < 1 ? look.Color1 : look.Color2).Scale(Breath(time, look.Speed)),
            MouseEffectMode.Rave => phase % 1 < 0.5 ? look.Color1 : look.Color2,
            MouseEffectMode.SeamlessBreathing => Rgb.FromHsv(phase * 60, 1, 1).Scale(Breath(time, look.Speed)),
            _ => Rgb.FromHsv(phase * 90 + (int)zone * 40, 1, 1), // Glorious, wave, tail: moving rainbow
        };
    }

    private static double Breath(double time, int speed)
    {
        double c = (Math.Sin(time * (0.5 + speed / 25.0)) + 1) / 2;
        return 0.08 + 0.92 * c * c;
    }

    /// <summary>
    /// A mouse look that matches the keyboard lighting, plus a brightness factor 0..100 %; or null when
    /// there is no match, so the mouse is left alone. That is the keyboard's hardware Customize mode:
    /// its per-key colours live in the keyboard's flash and the profile does not know them.
    /// </summary>
    public static (MouseLook Look, int Level)? FromKeyboard(Profile p)
    {
        if (p.Kind == ProfileKind.Hardware)
        {
            var hw = p.Hardware;
            if (hw.Mode == EffectMode.Customize) return null;
            int level = Math.Clamp(hw.Brightness, 0, 5) * 20;
            int speed = Math.Clamp(hw.Speed, 1, 5) * 20;
            if (hw.Colorful || !HardwareEffect.SupportsColor(hw.Mode))
                return (Whole(MouseEffectMode.Glorious, speed), level);
            return (AllZones(hw.Mode == EffectMode.Breathing ? ZoneEffect.Breathing : ZoneEffect.Solid, hw.Color, speed), level);
        }

        var fx = p.Software;
        int swSpeed = (int)Math.Clamp(fx.Speed / 3 * 100, 0, 100);
        int swLevel = Math.Clamp(p.Brightness, 0, 100);
        return fx.Base switch
        {
            BaseEffect.Solid => (AllZones(ZoneEffect.Solid, fx.Color1, swSpeed), swLevel),
            BaseEffect.Breathing => (Whole(MouseEffectMode.Breathing, swSpeed, fx.Color1, fx.Color2), swLevel),
            BaseEffect.GradientWave => (new MouseLook
            {
                Mode = MouseLookMode.PerZone, Speed = swSpeed,
                Left = new() { Color = fx.Color1 },
                Wheel = new() { Color = Rgb.Lerp(fx.Color1, fx.Color2, 0.5) },
                Right = new() { Color = fx.Color2 },
            }, swLevel),
            BaseEffect.Starfield => (AllZones(ZoneEffect.Breathing, fx.Color1, swSpeed), swLevel),
            BaseEffect.Off => (AllZones(ZoneEffect.Off, Rgb.Black, swSpeed), swLevel),
            _ => (Whole(MouseEffectMode.Glorious, swSpeed), swLevel), // rainbow wave, colour cycle
        };
    }

    private static MouseLook AllZones(ZoneEffect effect, Rgb color, int speed) => new()
    {
        Mode = MouseLookMode.PerZone, Speed = speed,
        Wheel = new() { Effect = effect, Color = color },
        Left = new() { Effect = effect, Color = color },
        Right = new() { Effect = effect, Color = color },
    };

    private static MouseLook Whole(MouseEffectMode mode, int speed, Rgb? c1 = null, Rgb? c2 = null) => new()
    {
        Mode = MouseLookMode.WholeMouse, WholeEffect = mode, Speed = speed,
        Color1 = c1 ?? new(255, 0, 0), Color2 = c2 ?? new(0, 0, 255),
    };
}
