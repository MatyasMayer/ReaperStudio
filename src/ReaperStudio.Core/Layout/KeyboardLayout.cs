using System.Text.Json;

namespace ReaperStudio.Core.Layout;

/// <summary>A physical key: position in the vendor's 850x300 key image space, Windows VK code and LED slot.</summary>
public sealed record KeyInfo(int Id, string Name, int X, int Y, int W, int H, int Vk, int Led)
{
    public double CenterX => X + W / 2.0;
    public double CenterY => Y + H / 2.0;
}

public sealed class KeyboardLayout
{
    public required IReadOnlyList<KeyInfo> Keys { get; init; }
    public double Width { get; private init; }
    public double Height { get; private init; }

    private Dictionary<int, KeyInfo> _byVk = [];
    private Dictionary<int, KeyInfo> _byLed = [];

    public KeyInfo? ByVk(int vk) => _byVk.GetValueOrDefault(vk);
    public KeyInfo? ByLed(int led) => _byLed.GetValueOrDefault(led);

    private static readonly Lazy<KeyboardLayout> _reaper = new(LoadEmbedded);
    public static KeyboardLayout Reaper => _reaper.Value;

    private static KeyboardLayout LoadEmbedded()
    {
        using var s = typeof(KeyboardLayout).Assembly.GetManifestResourceStream("ReaperStudio.Core.layout.json")
            ?? throw new InvalidOperationException("layout.json resource missing");
        using var doc = JsonDocument.Parse(s);
        var keys = doc.RootElement.GetProperty("keys").EnumerateArray().Select(k => new KeyInfo(
            k.GetProperty("id").GetInt32(), k.GetProperty("name").GetString() ?? "",
            k.GetProperty("x").GetInt32(), k.GetProperty("y").GetInt32(),
            k.GetProperty("w").GetInt32(), k.GetProperty("h").GetInt32(),
            k.GetProperty("vk").GetInt32(), k.GetProperty("led").GetInt32())).ToList();

        // Both Enter keys report VK 0x0D on Windows; the layout stores numpad Enter as 0xFD.
        return new KeyboardLayout
        {
            Keys = keys,
            Width = keys.Max(k => k.X + k.W) + 16,
            Height = keys.Max(k => k.Y + k.H) + 16,
            _byVk = keys.GroupBy(k => k.Vk).ToDictionary(g => g.Key, g => g.First()),
            _byLed = keys.ToDictionary(k => k.Led),
        };
    }
}
