using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReaperStudio.Core.Layout;
using ReaperStudio.Core.Lighting;

namespace ReaperStudio.Controls;

/// <summary>Draws the Reaper's keys with their current LED colours; supports click/drag painting.</summary>
public sealed class KeyboardView : FrameworkElement
{
    private static readonly Dictionary<string, string> ShortLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = "⌫", ["CapsLock"] = "Caps", ["LShift"] = "Shift", ["RShift"] = "Shift",
        ["LCtrl"] = "Ctrl", ["RCtrl"] = "Ctrl", ["LWin"] = "Win", ["LAlt"] = "Alt", ["RAlt"] = "Alt",
        ["Space"] = "", ["Enter"] = "Enter", ["Delete"] = "Del", ["Insert"] = "Ins", ["PrtSc"] = "Prt",
        ["NumLock"] = "Num", ["Num /"] = "/", ["Num*"] = "*", ["Num-"] = "−", ["Num+"] = "+", ["Num ."] = ".",
        ["Up"] = "↑", ["Down"] = "↓", ["Left"] = "←", ["Right"] = "→", ["PgUp"] = "PgUp", ["PgDn"] = "PgDn",
        ["Num0"] = "0", ["Num1"] = "1", ["Num2"] = "2", ["Num3"] = "3", ["Num4"] = "4",
        ["Num5"] = "5", ["Num6"] = "6", ["Num7"] = "7", ["Num8"] = "8", ["Num9"] = "9",
    };

    private readonly KeyboardLayout _layout = KeyboardLayout.Reaper;
    private readonly LedFrame _frame = new();
    private readonly Typeface _font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush PlateBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x18)));
    private static readonly Pen PlatePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x3A)), 1));
    private static readonly Color KeyBase = Color.FromRgb(0x1B, 0x1B, 0x24);
    private static readonly Pen HoverPen = Freeze(new Pen(Brushes.White, 2));

    private KeyInfo? _hover;
    private MouseButton? _dragButton;
    private readonly HashSet<int> _selected = [];

    /// <summary>Raised when a key is clicked or dragged over with a mouse button held.</summary>
    public event Action<KeyInfo, MouseButton>? KeyPainted;

    public static readonly DependencyProperty IsPaintableProperty = DependencyProperty.Register(
        nameof(IsPaintable), typeof(bool), typeof(KeyboardView), new PropertyMetadata(false));

    public bool IsPaintable
    {
        get => (bool)GetValue(IsPaintableProperty);
        set => SetValue(IsPaintableProperty, value);
    }

    /// <summary>LED indices drawn with a selection outline (e.g. painted keys).</summary>
    public void SetMarked(IEnumerable<int> leds)
    {
        _selected.Clear();
        _selected.UnionWith(leds);
        InvalidateVisual();
    }

    public KeyboardView()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public void ShowFrame(LedFrame frame)
    {
        _frame.CopyFrom(frame);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? _layout.Width : available.Width;
        return new Size(w, w * (_layout.Height + 8) / (_layout.Width + 8));
    }

    private (double Scale, double Ox, double Oy) Transform()
    {
        double s = Math.Min(ActualWidth / (_layout.Width + 8), ActualHeight / (_layout.Height + 8));
        double ox = (ActualWidth - _layout.Width * s) / 2, oy = (ActualHeight - _layout.Height * s) / 2;
        return (s, ox, oy);
    }

    /// <summary>Per-key geometry that only changes with the control size (labels are the expensive part).</summary>
    private sealed record KeyVisual(KeyInfo Key, Rect Rect, Rect Glow, Geometry? Label);

    private KeyVisual[] _visuals = [];
    private Size _visualsFor;
    private double _scale = 1;
    private Rect _plate;

    private static readonly Pen DarkBorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x3E)), 1.3));
    private static readonly Brush DarkFill = Freeze(new SolidColorBrush(KeyBase));
    private static readonly Brush DarkText = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x90)));

    private void BuildVisuals()
    {
        var (s, ox, oy) = Transform();
        _scale = s;
        _plate = new Rect(ox, oy, _layout.Width * s, _layout.Height * s);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _visuals = _layout.Keys.Select(k =>
        {
            var rect = new Rect(ox + (k.X + 2) * s, oy + (k.Y + 2) * s, (k.W - 1) * s, (k.H - 1) * s);
            var glow = rect; glow.Inflate(3 * s, 3 * s);
            string label = ShortLabels.TryGetValue(k.Name, out var sl) ? sl : k.Name;
            Geometry? geo = null;
            if (label.Length > 0)
            {
                double size = (label.Length > 3 ? 8.5 : label.Length > 1 ? 10 : 11.5) * s;
                var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _font, Math.Max(6, size), Brushes.White, dpi);
                if (ft.Width > rect.Width - 3) ft.SetFontSize(Math.Max(5, size * (rect.Width - 3) / ft.Width));
                geo = ft.BuildGeometry(new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
                geo.Freeze();
            }
            return new KeyVisual(k, rect, glow, geo);
        }).ToArray();
        _visualsFor = RenderSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_visualsFor != RenderSize) BuildVisuals();
        double s = _scale, r = 5 * s;
        dc.DrawRoundedRectangle(PlateBrush, PlatePen, _plate, 14 * s, 14 * s);

        foreach (var v in _visuals)
        {
            var led = _frame[v.Key.Led];
            bool marked = _selected.Contains(v.Key.Led);
            if (led.IsBlack)
            {
                dc.DrawRoundedRectangle(DarkFill, marked ? new Pen(DarkBorderPen.Brush, 2.5) : DarkBorderPen, v.Rect, r, r);
                if (v.Label is not null) dc.DrawGeometry(DarkText, null, v.Label);
            }
            else
            {
                var c = Color.FromRgb(led.R, led.G, led.B);
                dc.DrawRoundedRectangle(Freeze(new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B))), null, v.Glow, r * 1.6, r * 1.6);
                dc.DrawRoundedRectangle(Freeze(new SolidColorBrush(Blend(KeyBase, c, 0.28))),
                    Freeze(new Pen(Freeze(new SolidColorBrush(c)), marked ? 2.5 : 1.3)), v.Rect, r, r);
                if (v.Label is not null) dc.DrawGeometry(Freeze(new SolidColorBrush(Blend(c, Colors.White, 0.55))), null, v.Label);
            }
            if (ReferenceEquals(v.Key, _hover) && IsPaintable)
                dc.DrawRoundedRectangle(null, HoverPen, v.Rect, r, r);
        }
    }

    private KeyInfo? HitTest(Point p)
    {
        var (s, ox, oy) = Transform();
        double x = (p.X - ox) / s, y = (p.Y - oy) / s;
        return _layout.Keys.FirstOrDefault(k => x >= k.X && x <= k.X + k.W && y >= k.Y && y <= k.Y + k.H);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var k = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(k, _hover)) { _hover = k; InvalidateVisual(); }
        Cursor = IsPaintable && k is not null ? Cursors.Pen : null;
        if (_dragButton is { } b && k is not null && IsPaintable) KeyPainted?.Invoke(k, b);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (!IsPaintable || HitTest(e.GetPosition(this)) is not { } k) return;
        _dragButton = e.ChangedButton;
        CaptureMouse();
        KeyPainted?.Invoke(k, e.ChangedButton);
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        _dragButton = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
