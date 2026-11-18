using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ReaperStudio.Core.Lighting;
using ReaperStudio.Core.Mouse;

namespace ReaperStudio.Views.Mouse;

/// <summary>Top-down drawing of the Model D with its three LED zones, animated roughly like the firmware.</summary>
public sealed class MouseView : FrameworkElement
{
    private const double W = 100, H = 160; // drawing space
    private const double FrameInterval = 1 / 30.0;  // redraw rate while the look animates
    private const double PollInterval = 0.25;       // how often a static look is re-checked for changes
    private const int MaxCachedBrushes = 64;

    private static readonly Brush BodyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1B)));
    private static readonly Pen BodyPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x3A)), 1.2));
    private static readonly Pen SeamPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x3A)), 1));
    private static readonly Rgb Unlit = new(0x30, 0x30, 0x3C);

    private static readonly Rect Body = new(8, 0, 84, 160);
    private static readonly Rect Wheel = new(44, 16, 12, 28);
    private static readonly Rect LeftStripe = new(11, 62, 6, 76);
    private static readonly Rect RightStripe = new(83, 62, 6, 76);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<Rgb, Brush> _fills = [];
    private readonly Dictionary<Rgb, Brush> _glows = [];
    private double _lastTick;
    private bool _animated;
    private (Rgb Wheel, Rgb Left, Rgb Right)? _drawn;

    /// <summary>
    /// Look to draw; null draws the zones unlit (mouse left unchanged). It may change at any time without
    /// the view being told: an animated look is redrawn every frame, a static one is re-read a few times
    /// a second and redrawn only when its colours differ from what is on screen.
    /// </summary>
    public Func<MouseLook?>? Look { get; set; }

    public MouseView()
    {
        IsVisibleChanged += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            if (!IsVisible) return;
            CompositionTarget.Rendering += OnRendering;
            InvalidateVisual(); // the look may have changed while hidden
        };
    }

    /// <summary>True when the preview of <paramref name="look"/> changes over time; null (unlit) is static.</summary>
    public static bool IsAnimated(MouseLook? look) => look switch
    {
        null => false,
        { Mode: MouseLookMode.PerZone } => look.Wheel.Effect == ZoneEffect.Breathing
            || look.Left.Effect == ZoneEffect.Breathing || look.Right.Effect == ZoneEffect.Breathing,
        _ => look.WholeEffect is not (MouseEffectMode.Off or MouseEffectMode.SingleColor),
    };

    private void OnRendering(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalSeconds;
        if (t - _lastTick < (_animated ? FrameInterval : PollInterval)) return;
        _lastTick = t;
        if (_animated) { InvalidateVisual(); return; }

        // Static: only redraw when the look now animates or its colours changed.
        var look = Look?.Invoke();
        if (IsAnimated(look) || ZoneColors(look, t) != _drawn) InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double h = double.IsInfinity(available.Height) ? 200 : available.Height;
        return new Size(h * W / H, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth / W, ActualHeight / H);
        dc.PushTransform(new TranslateTransform((ActualWidth - W * s) / 2, (ActualHeight - H * s) / 2));
        dc.PushTransform(new ScaleTransform(s, s));

        var look = Look?.Invoke();
        _animated = IsAnimated(look);
        var colors = ZoneColors(look, _clock.Elapsed.TotalSeconds);
        _drawn = colors;
        var (wheel, left, right) = colors;

        dc.DrawRoundedRectangle(BodyBrush, BodyPen, Body, 42, 52);
        dc.DrawLine(SeamPen, new Point(50, 4), new Point(50, 56));
        dc.DrawLine(SeamPen, new Point(14, 56), new Point(86, 56));

        Glow(dc, Wheel, wheel, 14);
        Glow(dc, LeftStripe, left, 10);
        Glow(dc, RightStripe, right, 10);

        dc.DrawRoundedRectangle(Fill(wheel), null, Wheel, 6, 6);
        dc.DrawRoundedRectangle(Fill(left), null, LeftStripe, 3, 3);
        dc.DrawRoundedRectangle(Fill(right), null, RightStripe, 3, 3);

        dc.Pop();
        dc.Pop();
    }

    private static (Rgb Wheel, Rgb Left, Rgb Right) ZoneColors(MouseLook? look, double t) => look is null
        ? (Unlit, Unlit, Unlit)
        : (MouseTarget.PreviewColor(look, MouseZone.Wheel, t),
           MouseTarget.PreviewColor(look, MouseZone.Left, t),
           MouseTarget.PreviewColor(look, MouseZone.Right, t));

    private void Glow(DrawingContext dc, Rect r, Rgb c, double radius)
    {
        if (c.IsBlack || c == Unlit) return;
        var brush = Cached(_glows, c, x => new RadialGradientBrush(Color.FromArgb(150, x.R, x.G, x.B), Color.FromArgb(0, x.R, x.G, x.B)));
        var area = r;
        area.Inflate(radius, radius);
        dc.DrawRoundedRectangle(brush, null, area, radius, radius);
    }

    private Brush Fill(Rgb c) => Cached(_fills, c,
        x => new SolidColorBrush(x.IsBlack ? Color.FromRgb(0x22, 0x22, 0x2C) : Color.FromRgb(x.R, x.G, x.B)));

    /// <summary>
    /// Frozen brush for <paramref name="c"/>, reused across frames. Animations produce many colours, so the
    /// cache is simply emptied once it gets big; a static look keeps hitting the same few entries.
    /// </summary>
    private static Brush Cached(Dictionary<Rgb, Brush> cache, Rgb c, Func<Rgb, Brush> create)
    {
        if (cache.TryGetValue(c, out var brush)) return brush;
        if (cache.Count >= MaxCachedBrushes) cache.Clear();
        return cache[c] = Freeze(create(c));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
