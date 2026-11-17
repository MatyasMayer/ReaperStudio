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
    private static readonly Brush BodyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1B)));
    private static readonly Pen BodyPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x3A)), 1.2));
    private static readonly Pen SeamPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x3A)), 1));
    private static readonly Rgb Unlit = new(0x30, 0x30, 0x3C);

    private static readonly Rect Body = new(8, 0, 84, 160);
    private static readonly Rect Wheel = new(44, 16, 12, 28);
    private static readonly Rect LeftStripe = new(11, 62, 6, 76);
    private static readonly Rect RightStripe = new(83, 62, 6, 76);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame;

    /// <summary>Look to draw each frame; null draws the zones unlit (mouse left unchanged).</summary>
    public Func<MouseLook?>? Look { get; set; }

    public MouseView()
    {
        IsVisibleChanged += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            if (IsVisible) CompositionTarget.Rendering += OnRendering;
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalSeconds;
        if (t - _lastFrame < 1 / 30.0) return;
        _lastFrame = t;
        InvalidateVisual();
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
        double t = _clock.Elapsed.TotalSeconds;
        Rgb Zone(MouseZone z) => look is null ? Unlit : MouseTarget.PreviewColor(look, z, t);

        var (wheel, left, right) = (Zone(MouseZone.Wheel), Zone(MouseZone.Left), Zone(MouseZone.Right));
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

    private static void Glow(DrawingContext dc, Rect r, Rgb c, double radius)
    {
        if (c.IsBlack || c == Unlit) return;
        var brush = new RadialGradientBrush(Color.FromArgb(150, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
        var area = r;
        area.Inflate(radius, radius);
        dc.DrawRoundedRectangle(brush, null, area, radius, radius);
    }

    private static Brush Fill(Rgb c) => new SolidColorBrush(c.IsBlack ? Color.FromRgb(0x22, 0x22, 0x2C) : Color.FromRgb(c.R, c.G, c.B));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
