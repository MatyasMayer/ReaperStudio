using ReaperStudio.Core.Keyboard;
using ReaperStudio.Core.Lighting;
using ReaperStudio.Core.Profiles;

namespace ReaperStudio.Core.Effects;

/// <summary>Renders software profiles into LED frames. Stateful only for reactive key presses and starfield.</summary>
public sealed class EffectRenderer
{
    private readonly KeyboardLayout _layout;
    private readonly List<(KeyInfo Key, double Time, Rgb Color)> _presses = [];
    private readonly Lock _pressLock = new();
    private readonly Random _rng = new();
    private readonly double[] _starPhase = new double[LedFrame.LedCount];
    private readonly double[] _starRate = new double[LedFrame.LedCount];

    private const double GlowSeconds = 0.9, RippleSeconds = 1.1, RippleSpeed = 520; // px/s in layout space

    public EffectRenderer(KeyboardLayout layout)
    {
        _layout = layout;
        for (int i = 0; i < _starPhase.Length; i++) { _starPhase[i] = _rng.NextDouble() * 10; _starRate[i] = 0.3 + _rng.NextDouble(); }
    }

    /// <summary>Record a key press for reactive effects (thread-safe).</summary>
    public void KeyPressed(int vk, double time, SoftwareEffect fx)
    {
        if (fx.Reactive == ReactiveStyle.None || _layout.ByVk(vk) is not { } key) return;
        var color = fx.ReactiveRainbow ? Rgb.FromHsv(_rng.NextDouble() * 360, 1, 1) : fx.ReactiveColor;
        lock (_pressLock)
        {
            _presses.Add((key, time, color));
            if (_presses.Count > 64) _presses.RemoveAt(0);
        }
    }

    public void Render(Profile profile, double t, LedFrame frame)
    {
        var fx = profile.Software;
        frame.Clear();
        foreach (var key in _layout.Keys)
            frame[key.Led] = BaseColor(fx, key, t);

        foreach (var (led, color) in fx.Painted)
            if (led is >= 0 and < LedFrame.LedCount) frame[led] = color;

        if (fx.Reactive != ReactiveStyle.None) DrawReactive(fx, t, frame);
        frame.Scale(profile.Brightness / 100.0);
    }

    private Rgb BaseColor(SoftwareEffect fx, KeyInfo k, double t)
    {
        double s = fx.Speed;
        switch (fx.Base)
        {
            case BaseEffect.Off: return Rgb.Black;
            case BaseEffect.Solid: return fx.Color1;
            case BaseEffect.RainbowWave:
                return Rgb.FromHsv(Project(fx, k) * 0.45 - t * 90 * s, 1, 1);
            case BaseEffect.GradientWave:
            {
                double phase = Project(fx, k) / _layout.Width - t * 0.35 * s;
                double tri = Math.Abs(phase - Math.Floor(phase) - 0.5) * 2; // 0..1..0 triangle wave
                return Rgb.Lerp(fx.Color1, fx.Color2, Smooth(tri));
            }
            case BaseEffect.Breathing:
            {
                double cycle = (Math.Sin(t * Math.PI * 0.5 * s) + 1) / 2; // 0..1
                double t2 = (Math.Sin(t * Math.PI * 0.125 * s) + 1) / 2;
                return Rgb.Lerp(fx.Color1, fx.Color2, t2).Scale(0.08 + 0.92 * cycle * cycle);
            }
            case BaseEffect.ColorCycle:
                return Rgb.FromHsv(t * 40 * s, 1, 1);
            case BaseEffect.Starfield:
            {
                double v = Math.Sin(_starPhase[k.Led] + t * _starRate[k.Led] * 2.2 * s);
                double glow = Math.Pow(Math.Max(0, v), 6);
                return Rgb.Lerp(fx.Color2.Scale(0.15), fx.Color1, glow);
            }
            default: return Rgb.Black;
        }
    }

    private void DrawReactive(SoftwareEffect fx, double t, LedFrame frame)
    {
        lock (_pressLock)
        {
            double life = fx.Reactive == ReactiveStyle.Glow ? GlowSeconds : RippleSeconds;
            _presses.RemoveAll(p => t - p.Time > life);
            foreach (var (pk, time, color) in _presses)
            {
                double age = t - time;
                if (fx.Reactive == ReactiveStyle.Glow)
                {
                    frame[pk.Led] = Rgb.Max(frame[pk.Led], color.Scale(1 - age / GlowSeconds));
                    continue;
                }
                double radius = age * RippleSpeed, fade = 1 - age / RippleSeconds;
                foreach (var k in _layout.Keys)
                {
                    double d = Math.Abs(Math.Sqrt(Sq(k.CenterX - pk.CenterX) + Sq(k.CenterY - pk.CenterY)) - radius);
                    if (d < 45) frame[k.Led] = Rgb.Max(frame[k.Led], color.Scale(fade * (1 - d / 45)));
                }
            }
        }
    }

    private static double Project(SoftwareEffect fx, KeyInfo k)
    {
        double a = fx.Angle * Math.PI / 180;
        return k.CenterX * Math.Cos(a) + k.CenterY * Math.Sin(a);
    }

    private static double Smooth(double x) => x * x * (3 - 2 * x);
    private static double Sq(double x) => x * x;
}
