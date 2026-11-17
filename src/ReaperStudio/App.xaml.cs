using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ReaperStudio.Core.Engine;
using ReaperStudio.Core.Input;
using ReaperStudio.Core.Storage;
using WinForms = System.Windows.Forms;

namespace ReaperStudio;

public partial class App : Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ReaperStudio";

    private Mutex? _singleInstance;
    private WinForms.NotifyIcon? _tray;
    private KeyboardHook? _hook;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public static new App Current => (App)Application.Current;
    public AppConfig Config { get; private set; } = null!;
    public LightingEngine Engine { get; private set; } = null!;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\ReaperStudio.SingleInstance", out bool first);
        if (!first && !e.Args.Contains("--screenshot")) // screenshot mode may run next to the tray instance
        {
            MessageBox.Show("Reaper Studio is already running – look for its icon in the system tray.", "Reaper Studio");
            Shutdown();
            return;
        }

        Config = ConfigStore.Load();
        Engine = new LightingEngine(Config);
        Engine.Start();

        _hook = new KeyboardHook();
        _hook.KeyDown += Engine.KeyPressed;

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); ConfigStore.Save(Config); };

        CreateTray();
        MainWindow = new MainWindow();
        bool minimized = e.Args.Contains("--minimized") || Config.StartMinimized && e.Args.Contains("--autostart");
        if (e.Args.Contains("--screenshot"))
            ((MainWindow)MainWindow).CaptureScreenshots(e.Args.SkipWhile(a => a != "--screenshot").Skip(1).FirstOrDefault() ?? "shot");
        else if (!minimized) MainWindow.Show();
    }

    /// <summary>Persist the config shortly after the last change and hand it to the engine.</summary>
    public void ConfigChanged()
    {
        Engine.UpdateConfig(Config);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void ReplaceConfig(AppConfig cfg)
    {
        Config = cfg;
        ConfigChanged();
    }

    public static bool IsStartWithWindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --autostart");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Reaper Studio", null, (_, _) => ShowMainWindow());
        var pause = new WinForms.ToolStripMenuItem("Pause lighting") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => Engine.Paused = pause.Checked;
        menu.Items.Add(pause);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _tray = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Reaper Studio",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowMainWindow(); };
    }

    public void ShowMainWindow()
    {
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    public void Quit()
    {
        IsExiting = true;
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); ConfigStore.Save(Config); }
        _hook?.Dispose();
        Engine.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    public static bool IsVendorAppRunning()
    {
        var procs = Process.GetProcessesByName("DeviceDriver");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}
