using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ReaperStudio.Core.Lighting;

namespace ReaperStudio.Controls;

/// <summary>Compact HSV colour picker with swatches and a hex field.</summary>
public sealed class ColorPicker : UserControl
{
    private static readonly string[] Swatches =
        ["#FF0000", "#FF5A00", "#FFB400", "#FFFF00", "#00FF40", "#00FFC8", "#00A0FF", "#0030FF", "#8000FF", "#FF00C8", "#FFFFFF", "#000000"];

    private readonly Slider _h = Make(0, 359), _s = Make(0, 100), _v = Make(0, 100);
    private readonly TextBox _hex = new() { Width = 90 };
    private readonly Border _preview = new() { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
    private readonly LinearGradientBrush _satBrush = new() { StartPoint = new(0, 0), EndPoint = new(1, 0) };
    private readonly LinearGradientBrush _valBrush = new() { StartPoint = new(0, 0), EndPoint = new(1, 0) };
    private bool _updating;
    private Rgb _color = new(255, 0, 0);

    public event Action<Rgb>? ColorChanged;

    public Rgb Color
    {
        get => _color;
        set { _color = value; SyncFromColor(); }
    }

    public ColorPicker()
    {
        _preview.BorderBrush = (Brush)Application.Current.Resources["Border"];
        var hueBrush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        for (int i = 0; i <= 6; i++)
        {
            var c = Rgb.FromHsv(i * 60, 1, 1);
            hueBrush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B), i / 6.0));
        }

        var root = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(_preview);
        _hex.Margin = new Thickness(10, 0, 0, 0);
        _hex.VerticalContentAlignment = VerticalAlignment.Center;
        top.Children.Add(_hex);
        root.Children.Add(top);

        root.Children.Add(Track(_h, hueBrush));
        root.Children.Add(Track(_s, _satBrush));
        root.Children.Add(Track(_v, _valBrush));

        var sw = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var hex in Swatches)
        {
            var rgb = Rgb.Parse(hex);
            var r = new Rectangle
            {
                Width = 20, Height = 20, RadiusX = 4, RadiusY = 4, Margin = new Thickness(0, 0, 5, 5), Cursor = Cursors.Hand,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(rgb.R, rgb.G, rgb.B)),
                Stroke = (Brush)Application.Current.Resources["Border"], ToolTip = hex,
            };
            r.MouseLeftButtonDown += (_, _) => SetAndRaise(rgb);
            sw.Children.Add(r);
        }
        root.Children.Add(sw);
        Content = root;

        _h.ValueChanged += (_, _) => FromSliders();
        _s.ValueChanged += (_, _) => FromSliders();
        _v.ValueChanged += (_, _) => FromSliders();
        _hex.LostKeyboardFocus += (_, _) => FromHex();
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) FromHex(); };
        SyncFromColor();
    }

    private static Slider Make(double min, double max) => new() { Minimum = min, Maximum = max, Margin = new Thickness(0, 3, 0, 3) };

    private static Grid Track(Slider slider, Brush background)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = background, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(slider);
        return g;
    }

    private void FromSliders()
    {
        if (_updating) return;
        SetAndRaise(Rgb.FromHsv(_h.Value, _s.Value / 100, _v.Value / 100), keepSliders: true);
    }

    private void FromHex()
    {
        if (_updating) return;
        if (Rgb.TryParse(_hex.Text, out var c)) SetAndRaise(c);
        else _hex.Text = _color.Hex;
    }

    private void SetAndRaise(Rgb c, bool keepSliders = false)
    {
        if (c == _color && keepSliders) { UpdateVisuals(); return; }
        _color = c;
        if (keepSliders) UpdateVisuals(); else SyncFromColor();
        ColorChanged?.Invoke(c);
    }

    private void SyncFromColor()
    {
        _updating = true;
        var (h, s, v) = _color.ToHsv();
        if (s > 0 && v > 0) _h.Value = h; // keep hue when grey/black
        if (v > 0) _s.Value = s * 100;
        _v.Value = v * 100;
        _updating = false;
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        _updating = true;
        _hex.Text = _color.Hex;
        _updating = false;
        _preview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(_color.R, _color.G, _color.B));
        var full = Rgb.FromHsv(_h.Value, 1, 1);
        var bright = Rgb.FromHsv(_h.Value, _s.Value / 100, 1);
        _satBrush.GradientStops = [new(Colors.White, 0), new(System.Windows.Media.Color.FromRgb(full.R, full.G, full.B), 1)];
        _valBrush.GradientStops = [new(Colors.Black, 0), new(System.Windows.Media.Color.FromRgb(bright.R, bright.G, bright.B), 1)];
    }
}
