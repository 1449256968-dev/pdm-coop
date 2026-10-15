using System.Collections.Generic;

// Personal control preferences travel with authenticated input, never room settings.
// Original AI code still reads Settings inside the actor's scoped simulation tick.
public static class CoopPlayerControl
{
    private static readonly Dictionary<int, byte> remote = new Dictionary<int, byte>();
    public static byte Capture()
    {
        var settings = GameSystem.settings;
        return (byte)((settings.autopilotHuman ? 1 : 0) | (settings.autopilotActive ? 2 : 0) |
            (settings.autopilotPassive ? 4 : 0) | (settings.autopilotAutoFacing ? 8 : 0));
    }
    public static void Apply(byte flags)
    {
        var settings = GameSystem.settings;
        settings.autopilotHuman = (flags & 1) != 0;
        settings.autopilotActive = (flags & 2) != 0;
        settings.autopilotPassive = (flags & 4) != 0;
        settings.autopilotAutoFacing = (flags & 8) != 0;
    }
    public static void Receive(int id, byte flags) { remote[id] = (byte)(flags & 15); }
    public static byte For(int id) => remote.TryGetValue(id, out byte flags) ? flags : Capture();
    public static void Release(int id) { remote.Remove(id); }
    public static void Reset() { remote.Clear(); }
    public static void Toggle(PlayerScript player)
    {
        var settings = GameSystem.settings;
        if (player.currentAI.UsingHumanAutopilot()) settings.autopilotHuman = !settings.autopilotHuman;
        if (player.currentAI.UsingActiveMonsterAutopilot()) settings.autopilotActive = !settings.autopilotActive;
        if (player.currentAI.UsingPassiveMonsterAutopilot()) settings.autopilotPassive = !settings.autopilotPassive;
    }
}
