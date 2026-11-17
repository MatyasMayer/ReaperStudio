using System.Windows;
using System.Windows.Controls;
using ReaperStudio.Controls;
using ReaperStudio.Core.Lighting;
using ReaperStudio.Core.Mouse;
using ReaperStudio.Core.Profiles;

namespace ReaperStudio.Views.Mouse;

/// <summary>Mouse part of the profile editor: source, per-zone or whole-mouse look, per-link brightness.</summary>
public partial class MouseEditor : UserControl
{
    private static readonly Option<MouseEffectMode>[] WholeEffects =
    [
        new(MouseEffectMode.Glorious, "Rainbow (Glorious mode)"), new(MouseEffectMode.Wave, "Wave"),
        new(MouseEffectMode.SeamlessBreathing, "Seamless breathing (rainbow)"), new(MouseEffectMode.Breathing, "Breathing, two colours"),
        new(MouseEffectMode.BreathingSingleColor, "Breathing, one colour"), new(MouseEffectMode.SingleColor, "Solid colour"),
        new(MouseEffectMode.Tail, "Tail"), new(MouseEffectMode.Rave, "Rave, two colours"), new(MouseEffectMode.Off, "Off"),
    ];
    private static readonly Option<ZoneEffect>[] ZoneEffects =
        [new(ZoneEffect.Solid, "Solid colour"), new(ZoneEffect.Breathing, "Breathing"), new(ZoneEffect.Off, "Off")];

    private Profile? _profile;
    private bool _loading = true;
    private MouseStatus? _status;

    /// <summary>Raised after the user changed the profile's mouse settings.</summary>
    public event Action? Edited;

    public MouseEditor()
    {
        InitializeComponent();
        WholeEffectBox.ItemsSource = WholeEffects;
        ZoneEffectBox.ItemsSource = ZoneEffects;
        LookColor.ColorChanged += OnColor;
        Preview.Look = PreviewLook;
        _loading = false;
    }

    private MouseSettings? Settings => _profile?.Mouse;
    private MouseLink EditLink => Settings is { SeparateWireless: true } && EditWireless.IsChecked == true ? MouseLink.Wireless : MouseLink.Wired;
    private MouseLook? EditedLook => Settings?.LookFor(EditLink);
    private MouseZone EditZone => ZoneLeft.IsChecked == true ? MouseZone.Left : ZoneRight.IsChecked == true ? MouseZone.Right : MouseZone.Wheel;

    public void Show(Profile? profile)
    {
        _profile = profile;
        if (profile is null) return;
        var m = profile.Mouse;
        _loading = true;
        SourceFollow.IsChecked = m.Source == MouseSource.FollowKeyboard;
        SourceCustom.IsChecked = m.Source == MouseSource.Custom;
        SourceUnchanged.IsChecked = m.Source == MouseSource.Unchanged;
        SeparateWireless.IsChecked = m.SeparateWireless;
        if (!m.SeparateWireless) EditWired.IsChecked = true;
        WiredBrightness.Value = m.WiredBrightness;
        WirelessBrightness.Value = m.WirelessBrightness;
        _loading = false;
        ShowLook();
    }

    /// <summary>Connection info from the engine, shown under the preview.</summary>
    public void SetStatus(MouseStatus status)
    {
        _status = status;
        UpdateCaption();
    }

    private void ShowLook()
    {
        if (EditedLook is not { } look) return;
        _loading = true;
        ModePerZone.IsChecked = look.Mode == MouseLookMode.PerZone;
        ModeWhole.IsChecked = look.Mode == MouseLookMode.WholeMouse;
        WholeEffectBox.SelectedItem = WholeEffects.FirstOrDefault(o => o.Value == look.WholeEffect);
        ZoneEffectBox.SelectedItem = ZoneEffects.First(o => o.Value == look[EditZone].Effect);
        SpeedSlider.Value = look.Speed;
        LookColor.Color = CurrentColor(look);
        _loading = false;
        UpdateVisibility();
    }

    private Rgb CurrentColor(MouseLook look) => look.Mode == MouseLookMode.PerZone
        ? look[EditZone].Color
        : Color2Slot.IsChecked == true ? look.Color2 : look.Color1;

    private void UpdateVisibility()
    {
        if (Settings is not { } m) return;
        var source = m.Source;
        CustomPanel.Visibility = source == MouseSource.Custom ? Visibility.Visible : Visibility.Collapsed;
        BrightnessPanel.Visibility = source == MouseSource.Unchanged ? Visibility.Collapsed : Visibility.Visible;
        LinkTabs.Visibility = m.SeparateWireless ? Visibility.Visible : Visibility.Collapsed;
        SourceHint.Text = source switch
        {
            MouseSource.FollowKeyboard => "Matches the keyboard: same colour, breathing or rainbow. Updated whenever the profile changes, not every frame.",
            MouseSource.Custom => "Set the mouse yourself.",
            _ => "Reaper Studio won't touch the mouse while this profile is active.",
        };

        if (EditedLook is { } look)
        {
            bool perZone = look.Mode == MouseLookMode.PerZone;
            PerZonePanel.Visibility = perZone ? Visibility.Visible : Visibility.Collapsed;
            WholePanel.Visibility = perZone ? Visibility.Collapsed : Visibility.Visible;
            bool color = perZone ? look[EditZone].Effect != ZoneEffect.Off : MouseLook.UsesColor1(look.WholeEffect);
            LookColor.Visibility = color ? Visibility.Visible : Visibility.Collapsed;
            ColorSlotRow.Visibility = !perZone && MouseLook.UsesColor2(look.WholeEffect) ? Visibility.Visible : Visibility.Collapsed;
            if (ColorSlotRow.Visibility != Visibility.Visible && Color2Slot.IsChecked == true) Color1Slot.IsChecked = true;
            bool speed = perZone ? MouseTarget.Zones.Any(z => look[z].Effect == ZoneEffect.Breathing) : MouseLook.UsesSpeed(look.WholeEffect);
            SpeedLabel.Visibility = SpeedSlider.Visibility = speed ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateCaption();
    }

    private void UpdateCaption()
    {
        string conn = _status switch
        {
            null or { Connected: false } => "Mouse not found",
            { Waiting: true } => "Mouse asleep · will update when it wakes",
            { Link: MouseLink.Wired } s => "On the cable" + (s.Battery is { } b ? $" · {b} %" + (s.Charging ? " charging" : "") : ""),
            { } s => "Wireless" + (s.Battery is { } b ? $" · {b} %" : ""),
        };
        string what = Settings switch
        {
            { Source: MouseSource.Unchanged } => "Not controlled",
            { SeparateWireless: true } => EditLink == MouseLink.Wired ? "Preview: on the cable" : "Preview: on wireless",
            _ => "Preview",
        };
        PreviewCaption.Text = $"{what}\n{conn}";
    }

    /// <summary>Look drawn by the preview: the edited look, or what Follow keyboard derives from the profile.</summary>
    private MouseLook? PreviewLook() => _profile switch
    {
        { Mouse.Source: MouseSource.Custom } => EditedLook,
        { Mouse.Source: MouseSource.FollowKeyboard } p => MouseTarget.FromKeyboard(p).Look,
        _ => null,
    };

    private void Changed()
    {
        UpdateVisibility();
        Edited?.Invoke();
    }

    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings is not { } m) return;
        m.Source = SourceCustom.IsChecked == true ? MouseSource.Custom
            : SourceUnchanged.IsChecked == true ? MouseSource.Unchanged : MouseSource.FollowKeyboard;
        ShowLook();
        Changed();
    }

    private void Separate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Settings is not { } m) return;
        bool separate = SeparateWireless.IsChecked == true;
        // Start the wireless look as a copy of the cable look, so ticking the box changes nothing yet.
        if (separate && !m.SeparateWireless) m.Wireless = Copy(m.Wired);
        m.SeparateWireless = separate;
        if (!separate) EditWired.IsChecked = true;
        ShowLook();
        Changed();
    }

    private void EditLink_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ShowLook();
    }

    private void Zone_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ShowLook();
    }

    private void ColorSlot_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || EditedLook is not { } look) return;
        _loading = true;
        LookColor.Color = CurrentColor(look);
        _loading = false;
    }

    private void LookParam_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || EditedLook is not { } look) return;
        look.Mode = ModeWhole.IsChecked == true ? MouseLookMode.WholeMouse : MouseLookMode.PerZone;
        if (WholeEffectBox.SelectedItem is Option<MouseEffectMode> w) look.WholeEffect = w.Value;
        if (ReferenceEquals(sender, ZoneEffectBox) && ZoneEffectBox.SelectedItem is Option<ZoneEffect> z) look[EditZone].Effect = z.Value;
        look.Speed = (int)SpeedSlider.Value;
        if (ReferenceEquals(sender, ModePerZone) || ReferenceEquals(sender, ModeWhole)) { ShowLook(); }
        Changed();
    }

    private void OnColor(Rgb c)
    {
        if (_loading || EditedLook is not { } look) return;
        if (look.Mode == MouseLookMode.PerZone) look[EditZone].Color = c;
        else if (Color2Slot.IsChecked == true) look.Color2 = c;
        else look.Color1 = c;
        Changed();
    }

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || Settings is not { } m) return;
        m.WiredBrightness = (int)WiredBrightness.Value;
        m.WirelessBrightness = (int)WirelessBrightness.Value;
        Changed();
    }

    private static MouseLook Copy(MouseLook l) => new()
    {
        Mode = l.Mode, WholeEffect = l.WholeEffect, Color1 = l.Color1, Color2 = l.Color2, Speed = l.Speed,
        Wheel = new() { Effect = l.Wheel.Effect, Color = l.Wheel.Color },
        Left = new() { Effect = l.Left.Effect, Color = l.Left.Color },
        Right = new() { Effect = l.Right.Effect, Color = l.Right.Color },
    };
}
