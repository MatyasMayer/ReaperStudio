using ReaperStudio.Core.Mouse;

namespace ReaperStudio.Tests;

/// <summary>Which commands <see cref="MouseSync.SendChanges"/> sends and remembers, with a fake mouse.</summary>
public class MouseSyncTests
{
    private static MouseCommand Cmd(string slot, byte value) => new(slot, [value]);

    private static List<MouseCommand> WholeMouse(byte fx = 1, byte brightness = 50) =>
        [Cmd("fx", fx), Cmd("brightness-wired", brightness), Cmd("brightness-wireless", brightness)];

    private static List<MouseCommand> PerZone(byte fx = 1, byte brightness = 50) =>
        [Cmd("fx0", fx), Cmd("fx1", fx), Cmd("fx2", fx), Cmd("brightness-wired", brightness), Cmd("brightness-wireless", brightness)];

    /// <summary>Answers <paramref name="reply"/> for every slot and records the slots it was sent.</summary>
    private static Func<MouseCommand, MouseReply> Mouse(List<string> log, Func<string, MouseReply>? reply = null) =>
        c => { log.Add(c.Slot); return reply?.Invoke(c.Slot) ?? MouseReply.Ok; };

    [Fact]
    public void FirstSync_SendsEverything()
    {
        var sent = new Dictionary<string, MouseCommand>();
        var log = new List<string>();
        Assert.Equal(MouseSync.SendResult.Done, MouseSync.SendChanges(sent, PerZone(), Mouse(log)));
        Assert.Equal(["fx0", "fx1", "fx2", "brightness-wired", "brightness-wireless"], log);
    }

    [Fact]
    public void Unchanged_SendsNothing_ChangedSlot_SendsOnlyThat()
    {
        var sent = new Dictionary<string, MouseCommand>();
        MouseSync.SendChanges(sent, PerZone(), Mouse([]));

        var log = new List<string>();
        MouseSync.SendChanges(sent, PerZone(), Mouse(log));
        Assert.Empty(log);

        var next = PerZone();
        next[1] = Cmd("fx1", 9);
        MouseSync.SendChanges(sent, next, Mouse(log));
        Assert.Equal(["fx1"], log);
    }

    [Fact]
    public void SwitchingLayout_SendsAllEffectSlotsOfTheNewLayout()
    {
        var sent = new Dictionary<string, MouseCommand>();
        MouseSync.SendChanges(sent, PerZone(), Mouse([]));

        var log = new List<string>();
        MouseSync.SendChanges(sent, WholeMouse(), Mouse(log));
        Assert.Equal(["fx"], log);
        Assert.DoesNotContain("fx0", sent.Keys);

        // Back to the same per-zone look: the mouse now shows the whole-mouse effect, so all zones go again.
        log.Clear();
        MouseSync.SendChanges(sent, PerZone(), Mouse(log));
        Assert.Equal(["fx0", "fx1", "fx2"], log);
        Assert.DoesNotContain("fx", sent.Keys);
    }

    [Theory]
    [InlineData(MouseReply.Failed)]
    [InlineData(MouseReply.Unsupported)]
    [InlineData(MouseReply.Busy)]
    public void RefusedCommand_IsNotRecorded_AndRetriedAlone(MouseReply refusal)
    {
        var sent = new Dictionary<string, MouseCommand>();
        var log = new List<string>();
        var result = MouseSync.SendChanges(sent, PerZone(), Mouse(log, s => s == "fx1" ? refusal : MouseReply.Ok));

        Assert.Equal(MouseSync.SendResult.Incomplete, result);
        Assert.Equal(5, log.Count); // the rest is still sent
        Assert.DoesNotContain("fx1", sent.Keys);

        log.Clear();
        Assert.Equal(MouseSync.SendResult.Done, MouseSync.SendChanges(sent, PerZone(), Mouse(log)));
        Assert.Equal(["fx1"], log); // not the zones that already took, despite the missing slot
    }

    [Fact]
    public void NotReachable_StopsAndKeepsTheRestPending()
    {
        var sent = new Dictionary<string, MouseCommand>();
        var log = new List<string>();
        var result = MouseSync.SendChanges(sent, WholeMouse(), Mouse(log, s => s == "brightness-wired" ? MouseReply.NotReachable : MouseReply.Ok));

        Assert.Equal(MouseSync.SendResult.NotReachable, result);
        Assert.Equal(["fx", "brightness-wired"], log);
        Assert.Equal(["fx"], sent.Keys);

        log.Clear();
        MouseSync.SendChanges(sent, WholeMouse(), Mouse(log));
        Assert.Equal(["brightness-wired", "brightness-wireless"], log);
    }
}
