namespace DeskBuddy.App;

// A local-only interaction model. No client, transport, configuration or disk access.
public sealed class CompanionInteractionDemo(Func<long>? clock = null)
{
    readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    long lastActivity, happyUntil, heartUntil;
    public bool Enabled { get; private set; }
    public uint Interactions { get; private set; }
    public uint Taps { get; private set; }
    public bool Sleeping => Enabled && now() - lastActivity >= 30_000;
    public bool Heart => Enabled && !Sleeping && now() < heartUntil;
    public bool Active => Enabled && !Sleeping && now() < happyUntil;
    public int Motion => Active ? 1 : 0;
    public void Tap() { Wake(); Taps++; heartUntil = now() + 1200; }
    public void Wake()
    {
        Enabled = true; lastActivity = now(); happyUntil = lastActivity + 1800;
        heartUntil = 0; Interactions++;
    }
    public void ShowSleep() { Enabled = true; lastActivity = now() - 30_000; happyUntil = heartUntil = 0; }
    public void Reset() { Enabled = false; Interactions = Taps = 0; happyUntil = heartUntil = 0; }
}
