using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ReaperStudio.Controls;
using ReaperStudio.Core.Engine;
using ReaperStudio.Core.Keyboard;
using ReaperStudio.Core.Lighting;
using ReaperStudio.Core.Mouse;
using ReaperStudio.Core.Profiles;
using ReaperStudio.Core.Rules;
using ReaperStudio.Core.Storage;

namespace ReaperStudio;

public sealed record ProfileItem(Profile Profile, bool IsDefault);
public sealed record RuleItem(Rule Rule, bool IsActive, string ProfileName);

public partial class MainWindow : Window
{
    private static readonly (EffectMode Mode, string Label)[] HardwareModes =
    [
        (EffectMode.Steady, "Steady"), (EffectMode.Breathing, "Breathing"), (EffectMode.NeonStream, "Neon stream"),
        (EffectMode.RipplesShining, "Ripples shining"), (EffectMode.RotatingWindmill, "Rotating windmill"),
        (EffectMode.SineWave, "Sine wave"), (EffectMode.RainbowRoulette, "Rainbow roulette"), (EffectMode.StarsTwinkle, "Stars twinkle"),
        (EffectMode.LayerUponLayer, "Layer upon layer"), (EffectMode.RichAndHonored, "Rich and honored"), (EffectMode.Marquee, "Marquee"),
        (EffectMode.RotatingStorm, "Rotating storm"), (EffectMode.SerpentineHorseRace, "Serpentine horse race"),
        (EffectMode.RetroSnake, "Retro snake"), (EffectMode.DiagonalTransformation, "Diagonal transformation"),
        (EffectMode.Streamer, "Streamer"), (EffectMode.Neon, "Neon"), (EffectMode.ShadowDisappear, "Shadow disappear (reactive)"),
        (EffectMode.FlashAway, "Flash away (reactive)"), (EffectMode.Ambilight, "Ambilight"), (EffectMode.Customize, "Customize (per-key, stored)"),
    ];

    private App AppRef => App.Current;
    private AppConfig Cfg => AppRef.Config;
    private LightingEngine Engine => AppRef.Engine;

    private Profile? _profile;
    private Rule? _rule;
    private bool _loading = true; // suppress control events until the constructor has populated everything
    private EngineStatus? _status;
    private int _frameQueued;
    private readonly ToggleButton[] _dayButtons = new ToggleButton[7];
    private readonly DispatcherTimer _vendorCheck = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();

        BaseEffectBox.ItemsSource = new[]
        {
            new Option<BaseEffect>(BaseEffect.RainbowWave, "Rainbow wave"), new Option<BaseEffect>(BaseEffect.GradientWave, "Two-colour wave"),
            new Option<BaseEffect>(BaseEffect.Solid, "Solid colour"), new Option<BaseEffect>(BaseEffect.Breathing, "Breathing"),
            new Option<BaseEffect>(BaseEffect.ColorCycle, "Colour cycle"), new Option<BaseEffect>(BaseEffect.Starfield, "Starfield"),
            new Option<BaseEffect>(BaseEffect.Off, "Off (painted keys only)"),
        };
        ReactiveBox.ItemsSource = new[]
        {
            new Option<ReactiveStyle>(ReactiveStyle.None, "Off"), new Option<ReactiveStyle>(ReactiveStyle.Glow, "Key glow"),
            new Option<ReactiveStyle>(ReactiveStyle.Ripple, "Ripple"),
        };
        HwModeBox.ItemsSource = HardwareModes.Select(m => new Option<EffectMode>(m.Mode, m.Label)).ToArray();

        string[] dayNames = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
        for (int i = 0; i < 7; i++)
        {
            var b = new ToggleButton { Content = dayNames[i], Style = (Style)FindResource("DayToggle"), Tag = (Days)(1 << i) };
            b.Click += (_, _) => RuleField_Changed(b, new RoutedEventArgs());
            _dayButtons[i] = b;
            DayToggles.Children.Add(b);
        }

        BaseColor.ColorChanged += c =>
        {
            if (_loading || _profile is null) return;
            if (Color1Slot.IsChecked == true) _profile.Software.Color1 = c; else _profile.Software.Color2 = c;
            ProfileEdited();
        };
        ReactiveColor.ColorChanged += c => { if (!_loading && _profile is not null) { _profile.Software.ReactiveColor = c; ProfileEdited(); } };
        HwColor.ColorChanged += _ => HardwareParam_Changed(this, new RoutedEventArgs());
        PaintColor.Color = Rgb.White;
        KeyboardPreview.KeyPainted += OnKeyPainted;

        Engine.FrameRendered += OnFrame;
        Engine.StatusChanged += s => Dispatcher.BeginInvoke(() => OnStatus(s));
        Engine.MouseStatusChanged += s => Dispatcher.BeginInvoke(() => OnMouseStatus(s));
        if (Engine.MouseStatus is { } ms) OnMouseStatus(ms);
        MouseEditor.Edited += ProfileEdited;

        ConfigPathText.Text = ConfigStore.FilePath;
        var version = typeof(App).Assembly.GetName().Version!;
        VersionText.Text = $"Reaper Studio v{version.Major}.{version.Minor}.{version.Build}";
        _vendorCheck.Tick += (_, _) =>
        {
            VendorWarning.Visibility = App.IsVendorAppRunning() ? Visibility.Visible : Visibility.Collapsed;
            GloriousWarning.Visibility = Cfg.MouseEnabled && App.IsGloriousCoreRunning() ? Visibility.Visible : Visibility.Collapsed;
        };
        _vendorCheck.Start();

        IsVisibleChanged += (_, _) => UpdatePreview();
        LoadSettings();
        RefreshProfiles(Cfg.FindProfile(Cfg.DefaultProfileId) ?? Cfg.Profiles.FirstOrDefault());
        RefreshRules(Cfg.Rules.FirstOrDefault());
    }

    // ------------------------------------------------------------------ shared

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (LightingPage is null) return;
        LightingPage.Visibility = NavLighting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RulesPage.Visibility = NavRules.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavRules.IsChecked == true) { RefreshRules(_rule); RefreshRunningApps(); }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (LightingPage is null || LivePreview is null) return; // still inside InitializeComponent
        bool preview = IsVisible && LightingPage.Visibility == Visibility.Visible && LivePreview.IsChecked == true && _profile is not null;
        Engine.SetPreview(preview ? _profile : null);
    }

    private void OnFrame(LedFrame frame)
    {
        // Engine thread. Coalesce: only one pending UI update at a time.
        if (Interlocked.Exchange(ref _frameQueued, 1) == 1) return;
        var copy = new LedFrame();
        copy.CopyFrom(frame);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _frameQueued = 0;
            if (IsVisible && LightingPage.Visibility == Visibility.Visible) KeyboardPreview.ShowFrame(copy);
        });
    }

    private void OnStatus(EngineStatus s)
    {
        _status = s;
        StatusDot.Fill = (Brush)FindResource(s.Connected ? "Good" : "Warn");
        StatusText.Text = s.Connected ? "Keyboard connected" : "Keyboard not found";
        StatusProfile.Text = s.Paused ? "Lighting paused" : s.Profile is { } p ? $"Showing: {p.Name}" : "";
        StatusRule.Text = s.Previewing ? "(editor preview)" : s.Rule is { } r ? $"Rule: {r.Name}" : "No rule active · default";
        StatusFps.Text = s.Fps > 0 ? $"Streaming {s.Fps:0} fps" : "";
        PauseButton.Content = s.Paused ? "Resume lighting" : "Pause lighting";
        if (RulesPage.Visibility == Visibility.Visible) RefreshRuleMarkers();
    }

    private void OnMouseStatus(MouseStatus s)
    {
        MouseDot.Fill = (Brush)FindResource(s.Connected && !s.Waiting ? "Good" : "Warn");
        MouseStatusText.Text = !s.Connected ? "Mouse not found" : s.Waiting ? "Mouse asleep" : "Mouse connected";
        MouseStatusDetail.Text = !s.Connected ? ""
            : (s.Link == MouseLink.Wired ? "Cable" : "Wireless") + (s.Battery is { } b ? $" · battery {b} %" + (s.Charging ? ", charging" : "") : "");
        MouseEditor.SetStatus(s);
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => Engine.Paused = !Engine.Paused;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AppRef.IsExiting)
        {
            e.Cancel = true; // keep running in the tray
            Hide();
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ profiles

    private void RefreshProfiles(Profile? select)
    {
        _loading = true;
        ProfileList.ItemsSource = Cfg.Profiles.Select(p => new ProfileItem(p, p.Id == Cfg.DefaultProfileId)).ToList();
        ProfileList.SelectedItem = ((List<ProfileItem>)ProfileList.ItemsSource).FirstOrDefault(i => i.Profile == select);
        _loading = false;
        ShowProfile(select);
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowProfile((ProfileList.SelectedItem as ProfileItem)?.Profile);
    }

    private void ShowProfile(Profile? p)
    {
        _profile = p;
        Editor.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
        if (p is null) { UpdatePreview(); return; }

        _loading = true;
        ProfileName.Text = p.Name;
        KindSoftware.IsChecked = p.Kind == ProfileKind.Software;
        KindHardware.IsChecked = p.Kind == ProfileKind.Hardware;
        SetDefaultButton.IsEnabled = p.Id != Cfg.DefaultProfileId;

        var fx = p.Software;
        BaseEffectBox.SelectedItem = BaseEffectBox.Items.Cast<Option<BaseEffect>>().First(o => o.Value == fx.Base);
        Color1Slot.IsChecked = true;
        BaseColor.Color = fx.Color1;
        SpeedSlider.Value = fx.Speed;
        AngleSlider.Value = fx.Angle;
        BrightnessSlider.Value = p.Brightness;
        ReactiveBox.SelectedItem = ReactiveBox.Items.Cast<Option<ReactiveStyle>>().First(o => o.Value == fx.Reactive);
        ReactiveRainbow.IsChecked = fx.ReactiveRainbow;
        ReactiveColor.Color = fx.ReactiveColor;

        var hw = p.Hardware;
        HwModeBox.SelectedItem = HwModeBox.Items.Cast<Option<EffectMode>>().FirstOrDefault(o => o.Value == hw.Mode);
        HwSpeed.Value = hw.Speed;
        HwBrightness.Value = hw.Brightness;
        HwDirection.Value = hw.Direction;
        HwColorful.IsChecked = hw.Colorful;
        HwColor.Color = hw.Color;
        SaveToKeyboardResult.Text = "";
        MouseEditor.Show(p);
        _loading = false;

        UpdateEditorVisibility();
        UpdatePreview();
    }

    private void UpdateEditorVisibility()
    {
        if (_profile is null) return;
        bool sw = _profile.Kind == ProfileKind.Software;
        SoftwareEditor.Visibility = sw ? Visibility.Visible : Visibility.Collapsed;
        HardwareEditor.Visibility = sw ? Visibility.Collapsed : Visibility.Visible;
        KeyboardPreview.IsPaintable = sw;
        KeyboardPreview.SetMarked(sw ? _profile.Software.Painted.Keys : []);
        KindHint.Text = sw
            ? "Rendered by Reaper Studio and streamed live: wave effects, painted keys and reactive typing. Needs the app running in the tray."
            : "Uses an effect built into the keyboard's firmware. Keeps running even when Reaper Studio is closed.";

        var b = _profile.Software.Base;
        ColorSlotRow.Visibility = b is BaseEffect.GradientWave or BaseEffect.Breathing or BaseEffect.Starfield ? Visibility.Visible : Visibility.Collapsed;
        BaseColor.Visibility = b is BaseEffect.RainbowWave or BaseEffect.ColorCycle or BaseEffect.Off ? Visibility.Collapsed : Visibility.Visible;
        bool directional = b is BaseEffect.RainbowWave or BaseEffect.GradientWave;
        AngleLabel.Visibility = AngleSlider.Visibility = directional ? Visibility.Visible : Visibility.Collapsed;
        ReactiveColor.Visibility = ReactiveRainbow.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;

        var mode = _profile.Hardware.Mode;
        HwSpeed.IsEnabled = HardwareEffect.SupportsSpeed(mode);
        HwDirection.IsEnabled = HardwareEffect.SupportsDirection(mode);
        HwColorPanel.IsEnabled = HardwareEffect.SupportsColor(mode);
        HwColor.Visibility = HwColorful.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ProfileEdited()
    {
        Engine.Invalidate();
        AppRef.ConfigChanged();
        if (ProfileList.ItemsSource is not null)
        {
            _loading = true;
            var sel = ProfileList.SelectedItem;
            ProfileList.Items.Refresh();
            ProfileList.SelectedItem = sel;
            _loading = false;
        }
    }

    private void ProfileName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _profile is null) return;
        _profile.Name = ProfileName.Text;
        ProfileEdited();
    }

    private void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _profile is null) return;
        _profile.Kind = KindHardware.IsChecked == true ? ProfileKind.Hardware : ProfileKind.Software;
        UpdateEditorVisibility();
        ProfileEdited();
    }

    private void SoftwareParam_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _profile is null) return;
        var fx = _profile.Software;
        if (BaseEffectBox.SelectedItem is Option<BaseEffect> b) fx.Base = b.Value;
        fx.Speed = Math.Round(SpeedSlider.Value, 2);
        fx.Angle = Math.Round(AngleSlider.Value);
        _profile.Brightness = (int)BrightnessSlider.Value;
        if (ReactiveBox.SelectedItem is Option<ReactiveStyle> r) fx.Reactive = r.Value;
        fx.ReactiveRainbow = ReactiveRainbow.IsChecked == true;
        UpdateEditorVisibility();
        ProfileEdited();
    }

    private void ColorSlot_Checked(object sender, RoutedEventArgs e)
    {
        if (_profile is null || BaseColor is null) return;
        bool wasLoading = _loading;
        _loading = true;
        BaseColor.Color = Color1Slot.IsChecked == true ? _profile.Software.Color1 : _profile.Software.Color2;
        _loading = wasLoading;
    }

    private void HardwareParam_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _profile is null) return;
        _profile.Hardware = _profile.Hardware with
        {
            Mode = (HwModeBox.SelectedItem as Option<EffectMode>)?.Value ?? EffectMode.Steady,
            Speed = (int)HwSpeed.Value,
            Brightness = (int)HwBrightness.Value,
            Direction = (int)HwDirection.Value,
            Colorful = HwColorful.IsChecked == true,
            Color = HwColor.Color,
        };
        UpdateEditorVisibility();
        ProfileEdited();
    }

    private void OnKeyPainted(KeyInfo key, MouseButton button)
    {
        if (_profile is not { Kind: ProfileKind.Software }) return;
        var painted = _profile.Software.Painted;
        if (button == MouseButton.Left) painted[key.Led] = PaintColor.Color;
        else if (button == MouseButton.Right) painted.Remove(key.Led);
        else return;
        KeyboardPreview.SetMarked(painted.Keys);
        ProfileEdited();
    }

    private void ClearPainted_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        _profile.Software.Painted.Clear();
        KeyboardPreview.SetMarked([]);
        ProfileEdited();
    }

    private void PaintAll_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        foreach (var k in KeyboardLayout.Reaper.Keys) _profile.Software.Painted[k.Led] = PaintColor.Color;
        KeyboardPreview.SetMarked(_profile.Software.Painted.Keys);
        ProfileEdited();
    }

    private void SaveToKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        try
        {
            SaveToKeyboardResult.Text = Engine.StoreOnKeyboard(_profile)
                ? "Saved. The keyboard shows this (as a still image) whenever Reaper Studio isn't streaming."
                : "Keyboard not connected.";
        }
        catch (Win32Exception ex) { SaveToKeyboardResult.Text = "Failed: " + ex.Message; }
    }

    private void LivePreview_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        Cfg.DefaultProfileId = _profile.Id;
        AppRef.ConfigChanged();
        RefreshProfiles(_profile);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var p = new Profile { Name = $"Profile {Cfg.Profiles.Count + 1}" };
        Cfg.Profiles.Add(p);
        AppRef.ConfigChanged();
        RefreshProfiles(p);
        ProfileName.Focus();
        ProfileName.SelectAll();
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        var copy = _profile.Clone();
        copy.Name = _profile.Name + " (copy)";
        Cfg.Profiles.Insert(Cfg.Profiles.IndexOf(_profile) + 1, copy);
        AppRef.ConfigChanged();
        RefreshProfiles(copy);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        if (Cfg.Profiles.Count == 1) { MessageBox.Show(this, "You need at least one profile.", "Reaper Studio"); return; }
        int usedBy = Cfg.Rules.Count(r => r.ProfileId == _profile.Id);
        string msg = $"Delete \"{_profile.Name}\"?" + (usedBy > 0 ? $"\n{usedBy} rule(s) use it and will be switched to the default profile." : "");
        if (MessageBox.Show(this, msg, "Delete profile", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        int idx = Cfg.Profiles.IndexOf(_profile);
        Cfg.Profiles.Remove(_profile);
        if (Cfg.DefaultProfileId == _profile.Id) Cfg.DefaultProfileId = Cfg.Profiles[0].Id;
        foreach (var r in Cfg.Rules.Where(r => r.ProfileId == _profile.Id)) r.ProfileId = Cfg.DefaultProfileId;
        AppRef.ConfigChanged();
        RefreshProfiles(Cfg.Profiles[Math.Min(idx, Cfg.Profiles.Count - 1)]);
    }

    // ------------------------------------------------------------------ rules

    private void RefreshRules(Rule? select)
    {
        _loading = true;
        RuleProfile.ItemsSource = Cfg.Profiles.ToList();
        RuleList.ItemsSource = BuildRuleItems();
        RuleList.SelectedItem = ((List<RuleItem>)RuleList.ItemsSource).FirstOrDefault(i => i.Rule == select);
        _loading = false;
        ShowRule(select);
    }

    private List<RuleItem> BuildRuleItems() => Cfg.Rules.Select(r => new RuleItem(r,
        _status is { Previewing: false } s && s.Rule?.Id == r.Id,
        "→ " + (Cfg.FindProfile(r.ProfileId)?.Name ?? "(missing profile)"))).ToList();

    private void RefreshRuleMarkers()
    {
        var sel = (RuleList.SelectedItem as RuleItem)?.Rule;
        _loading = true;
        RuleList.ItemsSource = BuildRuleItems();
        RuleList.SelectedItem = ((List<RuleItem>)RuleList.ItemsSource).FirstOrDefault(i => i.Rule == sel);
        _loading = false;
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowRule((RuleList.SelectedItem as RuleItem)?.Rule);
    }

    private void ShowRule(Rule? r)
    {
        _rule = r;
        RuleEditor.Visibility = r is null ? Visibility.Collapsed : Visibility.Visible;
        NoRuleHint.Visibility = r is null ? Visibility.Visible : Visibility.Collapsed;
        if (r is null) return;

        _loading = true;
        RuleName.Text = r.Name;
        TrigSchedule.IsChecked = r.Trigger == RuleTrigger.Schedule;
        TrigFocused.IsChecked = r.Trigger == RuleTrigger.AppFocused;
        TrigRunning.IsChecked = r.Trigger == RuleTrigger.AppRunning;
        RuleFrom.Text = r.From.ToString("HH:mm", CultureInfo.InvariantCulture);
        RuleTo.Text = r.To.ToString("HH:mm", CultureInfo.InvariantCulture);
        foreach (var b in _dayButtons) b.IsChecked = r.Days.HasFlag((Days)b.Tag);
        RuleApps.Text = string.Join(", ", r.Apps);
        RuleProfile.SelectedItem = Cfg.FindProfile(r.ProfileId);
        _loading = false;
        UpdateRulePanels();
    }

    private void UpdateRulePanels()
    {
        bool schedule = TrigSchedule.IsChecked == true;
        SchedulePanel.Visibility = schedule ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.Visibility = schedule ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RuleEdited()
    {
        AppRef.ConfigChanged();
        RefreshRuleMarkers();
    }

    private void RuleField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _rule is null) return;
        _rule.Name = RuleName.Text;
        _rule.Trigger = TrigSchedule.IsChecked == true ? RuleTrigger.Schedule
            : TrigRunning.IsChecked == true ? RuleTrigger.AppRunning : RuleTrigger.AppFocused;
        _rule.Days = _dayButtons.Where(b => b.IsChecked == true).Aggregate(Days.None, (d, b) => d | (Days)b.Tag);
        if (RuleProfile.SelectedItem is Profile p) _rule.ProfileId = p.Id;
        UpdateRulePanels();
        RuleEdited();
    }

    private void RuleTime_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_rule is null) return;
        if (TimeOnly.TryParse(RuleFrom.Text, CultureInfo.InvariantCulture, out var from)) _rule.From = from;
        if (TimeOnly.TryParse(RuleTo.Text, CultureInfo.InvariantCulture, out var to)) _rule.To = to;
        RuleFrom.Text = _rule.From.ToString("HH:mm", CultureInfo.InvariantCulture);
        RuleTo.Text = _rule.To.ToString("HH:mm", CultureInfo.InvariantCulture);
        RuleEdited();
    }

    private void RuleApps_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_rule is null) return;
        _rule.Apps = RuleApps.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RuleEngine.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        RuleApps.Text = string.Join(", ", _rule.Apps);
        RuleEdited();
    }

    private void RuleEnabled_Click(object sender, RoutedEventArgs e) => RuleEdited();

    private void RefreshRunningApps()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != 0 && p.Id != Environment.ProcessId) set.Add(p.ProcessName);
                }
                catch (InvalidOperationException) { } // process exited meanwhile
            }
        }
        var names = set.ToList();
        RunningApps.ItemsSource = names;
        if (names.Count > 0) RunningApps.SelectedIndex = 0;
    }

    private void RefreshApps_Click(object sender, RoutedEventArgs e) => RefreshRunningApps();

    private void AddRunningApp_Click(object sender, RoutedEventArgs e)
    {
        if (_rule is null || RunningApps.SelectedItem is not string name) return;
        if (!_rule.Apps.Contains(name, StringComparer.OrdinalIgnoreCase)) _rule.Apps.Add(name);
        RuleApps.Text = string.Join(", ", _rule.Apps);
        RuleEdited();
    }

    private void NewRule_Click(object sender, RoutedEventArgs e)
    {
        var r = new Rule { Name = "New rule", ProfileId = Cfg.DefaultProfileId };
        Cfg.Rules.Insert(0, r);
        AppRef.ConfigChanged();
        RefreshRules(r);
        RuleName.Focus();
        RuleName.SelectAll();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (_rule is null) return;
        int idx = Cfg.Rules.IndexOf(_rule);
        Cfg.Rules.Remove(_rule);
        AppRef.ConfigChanged();
        RefreshRules(Cfg.Rules.Count > 0 ? Cfg.Rules[Math.Min(idx, Cfg.Rules.Count - 1)] : null);
    }

    private void RuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void RuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(+1);

    private void MoveRule(int delta)
    {
        if (_rule is null) return;
        int i = Cfg.Rules.IndexOf(_rule), j = i + delta;
        if (j < 0 || j >= Cfg.Rules.Count) return;
        (Cfg.Rules[i], Cfg.Rules[j]) = (Cfg.Rules[j], Cfg.Rules[i]);
        AppRef.ConfigChanged();
        RefreshRules(_rule);
    }

    // ------------------------------------------------------------------ settings

    private void LoadSettings()
    {
        _loading = true;
        StartWithWindows.IsChecked = App.IsStartWithWindowsEnabled();
        StartMinimized.IsChecked = Cfg.StartMinimized;
        FrameRate.Value = Cfg.FrameRate;
        MouseEnabled.IsChecked = Cfg.MouseEnabled;
        FrameRateText.Text = $"{Cfg.FrameRate} fps";
        _loading = false;
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.SetStartWithWindows(StartWithWindows.IsChecked == true);
        Cfg.StartWithWindows = StartWithWindows.IsChecked == true;
        Cfg.StartMinimized = StartMinimized.IsChecked == true;
        Cfg.MouseEnabled = MouseEnabled.IsChecked == true;
        AppRef.ConfigChanged();
    }

    private void FrameRate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FrameRateText is null) return;
        FrameRateText.Text = $"{(int)FrameRate.Value} fps";
        if (_loading) return;
        Cfg.FrameRate = (int)FrameRate.Value;
        AppRef.ConfigChanged();
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConfigStore.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", ConfigStore.Directory) { UseShellExecute = true });
    }

    private void ResetConfig_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Replace all profiles and rules with the examples?", "Reset", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK) return;
        AppRef.ReplaceConfig(AppConfig.CreateDefault());
        RefreshProfiles(Cfg.Profiles[0]);
        RefreshRules(Cfg.Rules.FirstOrDefault());
    }

    // ------------------------------------------------------------------ dev: screenshots

    /// <summary>Renders each page to PNG (used for visual checks: ReaperStudio.exe --screenshot dir).</summary>
    public async void CaptureScreenshots(string dir)
    {
        Directory.CreateDirectory(dir);
        Show();
        foreach (var (nav, name) in new[] { (NavLighting, "lighting"), (NavLighting, "mouse"), (NavRules, "rules"), (NavSettings, "settings") })
        {
            nav.IsChecked = true;
            if (name == "mouse") EditorScroll.ScrollToEnd();
            await Task.Delay(1200);
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var content = (FrameworkElement)Content;
            var bmp = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(content);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        AppRef.Quit();
    }
}
