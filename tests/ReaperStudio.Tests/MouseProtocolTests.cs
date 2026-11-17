using ReaperStudio.Core.Keyboard;
using ReaperStudio.Core.Lighting;
using ReaperStudio.Core.Mouse;
using ReaperStudio.Core.Profiles;

namespace ReaperStudio.Tests;

/// <summary>
/// Expected bytes follow ModelOSeries.js in the Glorious CORE app and were sent to a real
/// Model D Wireless (see docs/mouse/PROTOCOL.md). Arrays are the bytes after report ID 0.
/// </summary>
public class MouseProtocolTests
{
    private static byte[] Head(byte[] m, int n) => m[..n];

    [Fact]
    public void SingleColourAllZones_MatchesVendorApp()
    {
        // The command that turned the test mouse green: profile 1, all zones, rate slider 60.
        var m = MouseMessages.Effect(1, MouseZone.All, MouseEffectMode.SingleColor, [new Rgb(0, 255, 0)], 60);
        Assert.Equal(64, m.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x02, 0x08, 0x02, 0x00, 0x01, 0xFF, 0x04, 0x00, 0x09, 0x00, 0xFF, 0x00, 0x00 }, Head(m, 15));
    }

    [Fact]
    public void SingleZone_WritesZoneByte()
    {
        // Wheel red, as in the zone test (wheel = 0).
        var m = MouseMessages.Effect(1, MouseZone.Wheel, MouseEffectMode.SingleColor, [new Rgb(255, 0, 0)], 0);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x02, 0x08, 0x02, 0x00, 0x01, 0x00, 0x04, 0x00, 0x15, 0xFF, 0x00, 0x00 }, Head(m, 14));
    }

    [Fact]
    public void Black_IsSentAsOneZeroZero()
    {
        var m = MouseMessages.Effect(2, MouseZone.Left, MouseEffectMode.SingleColor, [Rgb.Black], 60);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00 }, m[11..14]);
        Assert.Equal(2, m[6]);
    }

    [Fact]
    public void Off_HasNoColours()
    {
        var m = MouseMessages.Effect(1, MouseZone.All, MouseEffectMode.Off, [], 60);
        Assert.Equal(0x05, m[3]);
        Assert.All(m[11..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(MouseEffectMode.SingleColor, 60, 9)]
    [InlineData(MouseEffectMode.Breathing, 62, 8)] // vendor app truncates 8.6
    [InlineData(MouseEffectMode.Rave, 60, 90)]
    [InlineData(MouseEffectMode.Wave, 100, 10)]
    public void Speed_UsesVendorMapping(MouseEffectMode mode, int speed, int raw)
        => Assert.Equal(raw, MouseMessages.RawSpeed(mode, speed));

    [Fact]
    public void Brightness_WiredAndWireless()
    {
        Assert.Equal(new byte[] { 0x00, 0x00, 0x02, 0x02, 0x02, 0x02, 0x01, 0x99 }, Head(MouseMessages.Brightness(MouseLink.Wired, 60), 8));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0xFF }, Head(MouseMessages.Brightness(MouseLink.Wireless, 100), 8));
    }

    [Fact]
    public void Queries()
    {
        Assert.Equal(new byte[] { 0x00, 0x00, 0x02, 0x01, 0x00, 0x85 }, Head(MouseMessages.QueryProfile(), 6));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x02, 0x02, 0x00, 0x83 }, Head(MouseMessages.QueryBattery(), 6));
    }
}

public class MouseTargetTests
{
    private static Profile Custom(MouseLook look) => new() { Mouse = new MouseSettings { Source = MouseSource.Custom, Wired = look } };

    [Fact]
    public void PerZone_SendsEachZoneThenBrightness()
    {
        var look = new MouseLook
        {
            Wheel = new() { Effect = ZoneEffect.Breathing, Color = new(255, 0, 0) },
            Left = new() { Color = new(0, 0, 255) },
            Right = new() { Effect = ZoneEffect.Off },
        };
        var cmds = MouseTarget.Commands(MouseTarget.Resolve(Custom(look), MouseLink.Wired)!, hwProfile: 1);

        Assert.Equal(["fx0", "fx1", "fx2", "brightness-wired", "brightness-wireless"], cmds.Select(c => c.Slot));
        Assert.Equal((byte)MouseEffectMode.BreathingSingleColor, cmds[0].Bytes[8]);
        Assert.Equal((byte)MouseZone.Left, cmds[1].Bytes[7]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF }, cmds[1].Bytes[11..14]);
        Assert.Equal((byte)MouseEffectMode.Off, cmds[2].Bytes[8]);
    }

    [Fact]
    public void WholeMouse_UsesAllZones()
    {
        var look = new MouseLook { Mode = MouseLookMode.WholeMouse, WholeEffect = MouseEffectMode.Rave };
        var fx = MouseTarget.Commands(MouseTarget.Resolve(Custom(look), MouseLink.Wired)!, 3)[0];
        Assert.Equal("fx", fx.Slot);
        Assert.Equal(0xFF, fx.Bytes[7]);
        Assert.Equal(3 * 2 + 5, fx.Bytes[3]); // two colours
        Assert.Equal(3, fx.Bytes[6]);
    }

    [Fact]
    public void Unchanged_SendsNothing()
        => Assert.Null(MouseTarget.Resolve(new Profile { Mouse = new() { Source = MouseSource.Unchanged } }, MouseLink.Wired));

    [Fact]
    public void Wireless_UsesItsOwnLookOnlyWhenSeparate()
    {
        var p = new Profile { Mouse = new() { Source = MouseSource.Custom } };
        p.Mouse.Wireless.Mode = MouseLookMode.WholeMouse;
        Assert.Same(p.Mouse.Wired, MouseTarget.Resolve(p, MouseLink.Wireless)!.Look);
        p.Mouse.SeparateWireless = true;
        Assert.Same(p.Mouse.Wireless, MouseTarget.Resolve(p, MouseLink.Wireless)!.Look);
        Assert.Same(p.Mouse.Wired, MouseTarget.Resolve(p, MouseLink.Wired)!.Look);
    }

    [Fact]
    public void Follow_GradientWave_SpreadsColoursLeftToRight()
    {
        var p = new Profile { Software = new() { Base = BaseEffect.GradientWave, Color1 = new(255, 0, 0), Color2 = new(0, 0, 255) } };
        var look = MouseTarget.Resolve(p, MouseLink.Wired)!.Look;
        Assert.Equal(MouseLookMode.PerZone, look.Mode);
        Assert.Equal(new Rgb(255, 0, 0), look.Left.Color);
        Assert.Equal(new Rgb(0, 0, 255), look.Right.Color);
    }

    [Fact]
    public void Follow_RainbowKeyboard_IsRainbowMouse()
    {
        var hw = new Profile { Kind = ProfileKind.Hardware, Hardware = new HardwareEffect { Mode = EffectMode.NeonStream, Colorful = true } };
        var sw = new Profile { Software = new() { Base = BaseEffect.RainbowWave } };
        Assert.Equal(MouseEffectMode.Glorious, MouseTarget.Resolve(hw, MouseLink.Wired)!.Look.WholeEffect);
        Assert.Equal(MouseLookMode.WholeMouse, MouseTarget.Resolve(sw, MouseLink.Wired)!.Look.Mode);
    }

    [Fact]
    public void Follow_ScalesBrightnessWithProfile()
    {
        var p = new Profile { Brightness = 50, Software = new() { Base = BaseEffect.Solid } };
        p.Mouse.WiredBrightness = 100;
        p.Mouse.WirelessBrightness = 60;
        var t = MouseTarget.Resolve(p, MouseLink.Wireless)!;
        Assert.Equal(50, t.WiredBrightness);
        Assert.Equal(30, t.WirelessBrightness);
    }
}
