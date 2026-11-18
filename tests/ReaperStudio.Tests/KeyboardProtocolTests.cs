using ReaperStudio.Core.Keyboard;
using ReaperStudio.Core.Lighting;

namespace ReaperStudio.Tests;

/// <summary>Expected bytes come from tools/keyboard/captures of the official CZC app.</summary>
public class KeyboardProtocolTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void Effect_SteadyGreen_MatchesVendorCapture()
    {
        // 18:06:47.750  02 29 11 00 04 05 00 ff 00 00 02
        var msg = Messages.Effect(new HardwareEffect
        {
            Mode = EffectMode.Steady, Speed = 4, Brightness = 5, Color = new Rgb(0, 255, 0), Colorful = false, Direction = 2,
        });
        Assert.Equal(60, msg.Length);
        Assert.Equal(Hex("02 29 11 00 04 05 00 ff 00 00 02"), msg[..11]);
        Assert.All(msg[11..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Effect_BreathingSlowRainbow_MatchesVendorCapture()
    {
        // 18:07:35.862  02 29 12 00 01 05 ff 00 00 01 02
        var msg = Messages.Effect(new HardwareEffect
        {
            Mode = EffectMode.Breathing, Speed = 1, Brightness = 5, Color = new Rgb(255, 0, 0), Colorful = true, Direction = 2,
        });
        Assert.Equal(Hex("02 29 12 00 01 05 ff 00 00 01 02"), msg[..11]);
    }

    [Fact]
    public void Effect_ClampsOutOfRangeValues()
    {
        var msg = Messages.Effect(new HardwareEffect { Speed = 99, Brightness = -3 });
        Assert.Equal(5, msg[4]);
        Assert.Equal(0, msg[5]);
    }

    [Fact]
    public void RealtimeFrame_HasHeaderAndRgbPerLed()
    {
        var f = new LedFrame();
        f[0] = new Rgb(0, 255, 0);      // Esc green, as captured at 18:09:04
        f[125] = new Rgb(1, 2, 3);
        var msg = Messages.RealtimeFrame(f);
        Assert.Equal(380, msg.Length);   // 7 packets of 62 bytes, as on the wire
        Assert.Equal(Hex("06 00 00 ff 00"), msg[..5]);
        Assert.Equal(Hex("01 02 03"), msg[^3..]);
    }

    [Fact]
    public void CustomColors_HasSlotHeader()
    {
        var f = new LedFrame();
        f[0] = Rgb.White;
        var msg = Messages.CustomColors(1, f);
        Assert.Equal(381, msg.Length);
        Assert.Equal(Hex("03 7e 01 ff ff ff"), msg[..6]);
    }

    [Fact]
    public void Layout_HasAllHundredKeysWithUniqueLeds()
    {
        var keys = KeyboardLayout.Reaper.Keys;
        Assert.Equal(100, keys.Count);
        Assert.Equal(100, keys.Select(k => k.Led).Distinct().Count());
        Assert.All(keys, k => Assert.InRange(k.Led, 0, LedFrame.LedCount - 1));
        Assert.Equal(0, KeyboardLayout.Reaper.ByVk(0x1B)!.Led);   // Esc
        Assert.Equal(14, KeyboardLayout.Reaper.ByVk('W')!.Led);
    }

    [Theory]
    [InlineData("#FF0080", 255, 0, 128)]
    [InlineData("00a0ff", 0, 160, 255)]
    public void Rgb_ParsesHex(string hex, byte r, byte g, byte b) => Assert.Equal(new Rgb(r, g, b), Rgb.Parse(hex));

    [Fact]
    public void Rgb_HsvRoundTrip()
    {
        var c = new Rgb(30, 144, 255);
        var (h, s, v) = c.ToHsv();
        var back = Rgb.FromHsv(h, s, v);
        Assert.InRange(Math.Abs(back.R - c.R) + Math.Abs(back.G - c.G) + Math.Abs(back.B - c.B), 0, 3);
    }
}
