using System.Text.Json;
using System.Text.Json.Serialization;
using ReaperStudio.Core.Lighting;
using ReaperStudio.Core.Profiles;
using ReaperStudio.Core.Protocol;
using ReaperStudio.Core.Rules;

namespace ReaperStudio.Core.Storage;

public sealed class AppConfig
{
    public List<Profile> Profiles { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];
    /// <summary>Profile used when no rule matches.</summary>
    public Guid DefaultProfileId { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public int FrameRate { get; set; } = 30;

    public Profile? FindProfile(Guid id) => Profiles.FirstOrDefault(p => p.Id == id);

    public static AppConfig CreateDefault()
    {
        var rainbow = new Profile
        {
            Name = "Rainbow wave",
            Software = new SoftwareEffect { Base = BaseEffect.RainbowWave, Reactive = ReactiveStyle.Ripple },
        };
        var night = new Profile
        {
            Name = "Night (dim warm)",
            Brightness = 35,
            Software = new SoftwareEffect { Base = BaseEffect.Breathing, Color1 = new(255, 90, 20), Color2 = new(255, 40, 0), Speed = 0.4 },
        };
        var fps = new Profile
        {
            Name = "FPS – WASD",
            Software = new SoftwareEffect
            {
                Base = BaseEffect.Solid, Color1 = new(20, 0, 40), Reactive = ReactiveStyle.Glow, ReactiveColor = Rgb.White,
                Painted = new() { [14] = new(255, 0, 0), [9] = new(255, 0, 0), [15] = new(255, 0, 0), [21] = new(255, 0, 0) },
            },
        };
        var steady = new Profile
        {
            Name = "Keyboard: steady red",
            Kind = ProfileKind.Hardware,
            Hardware = new HardwareEffect { Mode = EffectMode.Steady, Color = new(255, 0, 0) },
        };
        return new AppConfig
        {
            Profiles = [rainbow, night, fps, steady],
            DefaultProfileId = rainbow.Id,
            Rules =
            [
                new Rule { Name = "Night time", Trigger = RuleTrigger.Schedule, From = new(23, 0), To = new(7, 0), ProfileId = night.Id },
                new Rule { Name = "Counter-Strike 2", Trigger = RuleTrigger.AppFocused, Apps = ["cs2"], ProfileId = fps.Id, Enabled = true },
            ],
        };
    }
}

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%APPDATA%\ReaperStudio, or REAPERSTUDIO_CONFIG_DIR when set (testing).</summary>
    public static string Directory { get; } =
        Environment.GetEnvironmentVariable("REAPERSTUDIO_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReaperStudio");

    public static string FilePath => Path.Combine(Directory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions);
                if (cfg is { Profiles.Count: > 0 }) return cfg;
            }
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection and start fresh.
            File.Copy(FilePath, FilePath + ".broken", overwrite: true);
        }
        var fresh = AppConfig.CreateDefault();
        Save(fresh);
        return fresh;
    }

    public static void Save(AppConfig cfg)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
