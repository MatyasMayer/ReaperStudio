using System.Diagnostics;
using ReaperStudio.Core.Effects;
using ReaperStudio.Core.Keyboard;
using ReaperStudio.Core.Lighting;
using ReaperStudio.Core.Mouse;
using ReaperStudio.Core.Profiles;
using ReaperStudio.Core.Rules;
using ReaperStudio.Core.Storage;

namespace ReaperStudio.Core.Engine;

public sealed record EngineStatus(bool Connected, Profile? Profile, Rule? Rule, bool Previewing, bool Paused, double Fps);

/// <summary>
/// Background loop: picks the active profile from the rules, then either selects a built-in
/// keyboard effect once, or renders and streams real-time frames.
/// </summary>
public sealed class LightingEngine : IDisposable
{
    private readonly EffectRenderer _renderer = new(KeyboardLayout.Reaper);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly MouseSync _mouse = new();

    private volatile AppConfig _config;
    private volatile Profile? _preview;
    private volatile bool _paused;
    private volatile bool _forceResend;

    private ReaperKeyboard? _keyboard;
    private HardwareEffect? _lastHardware;
    private Profile? _active;
    private Rule? _activeRule;
    private EngineStatus? _lastStatus;
    private double _lastFrameAt;

    /// <summary>Set REAPERSTUDIO_TRACE to a file path to log every keyboard and mouse write (diagnostics).</summary>
    private readonly TextWriter? _trace = Environment.GetEnvironmentVariable("REAPERSTUDIO_TRACE") is { Length: > 0 } p
        ? TextWriter.Synchronized(new StreamWriter(p) { AutoFlush = true }) // written by the engine and mouse threads
        : null;

    /// <summary>Raised on the engine thread with the frame just shown (software) or an approximation (hardware).</summary>
    public event Action<LedFrame>? FrameRendered;
    /// <summary>Raised on the engine thread whenever connection, active profile or rule changes.</summary>
    public event Action<EngineStatus>? StatusChanged;
    /// <summary>Raised on the mouse thread whenever the mouse connection, link or battery changes.</summary>
    public event Action<MouseStatus>? MouseStatusChanged
    {
        add => _mouse.StatusChanged += value;
        remove => _mouse.StatusChanged -= value;
    }
    public MouseStatus? MouseStatus => _mouse.Status;

    public LightingEngine(AppConfig config)
    {
        _config = config;
        _thread = new Thread(Run) { IsBackground = true, Name = "ReaperStudio lighting", Priority = ThreadPriority.AboveNormal };
        if (_trace is { } t) _mouse.Trace = line => t.WriteLine($"{Now:F3} {line}");
    }

    public void Start()
    {
        _thread.Start();
        _mouse.Start();
    }

    /// <summary>Swap in an edited configuration. Re-applies the hardware effect if it changed.</summary>
    public void UpdateConfig(AppConfig config) { _config = config; _forceResend = true; _mouse.Invalidate(); }

    /// <summary>Show <paramref name="profile"/> on the keyboard regardless of rules (while editing). Null to stop.</summary>
    public void SetPreview(Profile? profile) { _preview = profile; _forceResend = true; }

    /// <summary>Call after mutating the previewed profile so hardware effects are re-sent.</summary>
    public void Invalidate() { _forceResend = true; _mouse.Invalidate(); }

    public bool Paused
    {
        get => _paused;
        set { _paused = value; _forceResend = true; }
    }

    public void KeyPressed(int vk)
    {
        if (_active is { Kind: ProfileKind.Software } p) _renderer.KeyPressed(vk, Now, p.Software);
    }

    /// <summary>Writes per-key colours to the keyboard's flash and selects the Customize effect.</summary>
    public bool StoreOnKeyboard(Profile profile)
    {
        var kb = _keyboard;
        if (kb is null) return false;
        var frame = new LedFrame();
        _renderer.Render(profile, 0, frame);
        lock (this)
        {
            kb.StoreCustomColors(1, frame);
            kb.SetEffect(new HardwareEffect { Mode = EffectMode.Customize, Brightness = 5 });
        }
        return true;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private void Run()
    {
        var frame = new LedFrame();
        double nextRuleCheck = 0, nextConnect = 0, fpsWindowStart = Now;
        int framesInWindow = 0;
        double fps = 0;

        while (!_cts.IsCancellationRequested)
        {
            double tickStart = Now;
            var cfg = _config;

            if (_keyboard is null && tickStart >= nextConnect)
            {
                _keyboard = ReaperKeyboard.TryOpen();
                _lastHardware = null;
                nextConnect = tickStart + 2;
            }

            if (tickStart >= nextRuleCheck || _forceResend)
            {
                bool needRunning = cfg.Rules.Any(r => r.Enabled && r.Trigger == RuleTrigger.AppRunning);
                _activeRule = RuleEngine.FirstMatch(cfg.Rules, RuleContext.Capture(needRunning));
                nextRuleCheck = tickStart + (needRunning ? 2.0 : 0.5);
            }

            var profile = _preview
                ?? (_activeRule is { } r ? cfg.FindProfile(r.ProfileId) : null)
                ?? cfg.FindProfile(cfg.DefaultProfileId)
                ?? cfg.Profiles.FirstOrDefault();
            _active = profile;
            _mouse.SetProfile(_paused ? null : profile, cfg.MouseEnabled);

            bool resend = _forceResend;
            _forceResend = false;
            bool streaming = false;

            try
            {
                if (_paused || profile is null)
                {
                    frame.Clear();
                }
                else if (profile.Kind == ProfileKind.Hardware)
                {
                    if (_keyboard is { } kb && (resend || _lastHardware != profile.Hardware))
                    {
                        lock (this) kb.SetEffect(profile.Hardware);
                        _lastHardware = profile.Hardware;
                        _trace?.WriteLine($"{Now:F3} EFFECT {profile.Name} {profile.Hardware.Mode}");
                    }
                    ApproximateHardware(profile.Hardware, frame);
                }
                else
                {
                    _renderer.Render(profile, Now, frame);
                    if (_keyboard is { } kb)
                    {
                        double sendStart = Now;
                        lock (this) kb.SendFrame(frame);
                        _lastHardware = null; // keyboard shows our stream; re-send effect when we switch back
                        streaming = true;
                        _trace?.WriteLine($"{sendStart:F3} FRAME {profile.Name} send={(Now - sendStart) * 1000:F1}ms gap={(sendStart - _lastFrameAt) * 1000:F1}ms stalls={kb.Stalls}");
                        _lastFrameAt = sendStart;
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ObjectDisposedException)
            {
                _trace?.WriteLine($"{Now:F3} DISCONNECT {ex.Message}");
                _keyboard?.Dispose();
                _keyboard = null;
                nextConnect = Now + 1;
            }

            framesInWindow++;
            if (Now - fpsWindowStart >= 1)
            {
                fps = framesInWindow / (Now - fpsWindowStart);
                framesInWindow = 0;
                fpsWindowStart = Now;
            }

            FrameRendered?.Invoke(frame);
            var status = new EngineStatus(_keyboard is not null, profile, _preview is null ? _activeRule : null,
                _preview is not null, _paused, streaming ? Math.Round(fps) : 0);
            if (_lastStatus is null || status with { Fps = 0 } != _lastStatus with { Fps = 0 }
                || Math.Abs(status.Fps - _lastStatus.Fps) >= 2)
            {
                _lastStatus = status;
                StatusChanged?.Invoke(status);
            }

            double period = streaming ? 1.0 / Math.Clamp(cfg.FrameRate, 5, 30) : 0.1;
            double remaining = period - (Now - tickStart);
            if (remaining > 0) _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(remaining));
        }
    }

    /// <summary>Rough on-screen stand-in for firmware effects we cannot render exactly.</summary>
    private void ApproximateHardware(HardwareEffect e, LedFrame frame)
    {
        double level = e.Brightness / 5.0;
        if (e.Mode == EffectMode.Customize) { frame.Fill(new Rgb(80, 80, 80)); return; }
        double t = Now;
        foreach (var k in KeyboardLayout.Reaper.Keys)
        {
            Rgb c = e.Colorful || !HardwareEffect.SupportsColor(e.Mode)
                ? Rgb.FromHsv(k.CenterX * 0.4 - t * 60 * e.Speed, 1, 1)
                : e.Color;
            if (e.Mode == EffectMode.Breathing) c = c.Scale((Math.Sin(t * e.Speed * 1.5) + 1) / 2);
            frame[k.Led] = c.Scale(level);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_thread.IsAlive) _thread.Join(1000);
        _keyboard?.Dispose();
        _mouse.Dispose();
        _cts.Dispose();
    }
}
