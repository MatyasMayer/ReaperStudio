using System.Text.Json.Serialization;
using ReaperStudio.Core.Lighting;
using ReaperStudio.Core.Keyboard;
using ReaperStudio.Core.Mouse;

namespace ReaperStudio.Core.Profiles;

public enum ProfileKind
{
    /// <summary>Uses an effect built into the keyboard firmware. Keeps running without the app.</summary>
    Hardware,
    /// <summary>Rendered by the app and streamed to the keyboard as real-time frames.</summary>
    Software,
}

public enum BaseEffect
{
    Solid,
    RainbowWave,
    GradientWave,
    Breathing,
    ColorCycle,
    Starfield,
    Off,
}

public enum ReactiveStyle
{
    None,
    /// <summary>Pressed key lights up and fades out.</summary>
    Glow,
    /// <summary>A ring expands outward from the pressed key.</summary>
    Ripple,
}

public sealed class SoftwareEffect
{
    public BaseEffect Base { get; set; } = BaseEffect.RainbowWave;
    public Rgb Color1 { get; set; } = new(255, 0, 80);
    public Rgb Color2 { get; set; } = new(0, 120, 255);
    /// <summary>Animation speed multiplier, 0.1 .. 3.</summary>
    public double Speed { get; set; } = 1.0;
    /// <summary>Wave direction in degrees: 0 = left→right, 90 = top→bottom.</summary>
    public double Angle { get; set; }
    /// <summary>Key-specific colours drawn on top of the base effect. Key: LED index.</summary>
    public Dictionary<int, Rgb> Painted { get; set; } = [];
    public ReactiveStyle Reactive { get; set; } = ReactiveStyle.None;
    public Rgb ReactiveColor { get; set; } = Rgb.White;
    /// <summary>Use a random hue for each key press instead of <see cref="ReactiveColor"/>.</summary>
    public bool ReactiveRainbow { get; set; }
}

public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public ProfileKind Kind { get; set; } = ProfileKind.Software;
    public HardwareEffect Hardware { get; set; } = new();
    public SoftwareEffect Software { get; set; } = new();
    /// <summary>Overall brightness for software profiles, 0 .. 100 %.</summary>
    public int Brightness { get; set; } = 100;
    /// <summary>What the Glorious Model D shows with this profile.</summary>
    public MouseSettings Mouse { get; set; } = new();

    [JsonIgnore]
    public string Summary => Kind == ProfileKind.Hardware
        ? $"Keyboard effect · {Hardware.Mode}"
        : $"App effect · {Software.Base}" + (Software.Painted.Count > 0 ? $" + {Software.Painted.Count} painted keys" : "")
          + (Software.Reactive != ReactiveStyle.None ? $" + {Software.Reactive}" : "");

    public override string ToString() => Name;

    public Profile Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Storage.ConfigStore.JsonOptions);
        var copy = System.Text.Json.JsonSerializer.Deserialize<Profile>(json, Storage.ConfigStore.JsonOptions)!;
        copy.Id = Guid.NewGuid();
        return copy;
    }
}
