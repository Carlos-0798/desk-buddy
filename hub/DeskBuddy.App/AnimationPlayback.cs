using DeskBuddy.Core;

namespace DeskBuddy.App;

public sealed record AnimationSnapshot(AnimationPreviewAction? Action, int Remaining, AnimationPose? Pose);

public sealed record AnimationPose(string Frame, int X = 0, int Y = 0, bool Sleeping = false, bool Heart = false, int ZCount = 3, int HeartY = 0);

// The R3C six-second timeline. Explicit preview ignores the daily Animation flag.
public sealed class AnimationPlayback(Func<long>? clock = null)
{
    readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    long started;
    AnimationPreviewAction? action;
    public int Elapsed => (int)Math.Clamp(now() - started, 0, 6000);
    public AnimationSnapshot Snapshot
    {
        get { int elapsed = Elapsed; return action is { } a && elapsed < 6000 ? new(a, 6000 - elapsed, At(a, elapsed)) : new(null, 0, null); }
    }
    public bool Running => Snapshot.Action != null;
    public int Remaining => Snapshot.Remaining;
    public AnimationPreviewAction? Action => Snapshot.Action;
    public AnimationPose? Pose => Snapshot.Pose;
    public void Start(AnimationPreviewAction value, int remaining = 6000) { action = value; started = now() - (6000 - Math.Clamp(remaining, 0, 6000)); }
    public void Stop() => action = null;
    static string Idle(int t) => t % 4400 >= 4220 ? "blink" : "idle";
    public static AnimationPose At(AnimationPreviewAction a, int t)
    {
        int bob = t / 600 % 2 == 0 ? -1 : 1;
        return a switch
        {
            AnimationPreviewAction.Idle => new(Idle(t), Y: bob),
            AnimationPreviewAction.Blink => new(t % 900 >= 600 ? "blink" : "idle"),
            AnimationPreviewAction.Happy => new("happy", Y: t / 300 % 2 == 0 ? -1 : 1),
            AnimationPreviewAction.Walk => new(t / 180 % 2 == 0 ? "walk_a" : "walk_b", new[] { -3, -1, 1, 3, 1, -1 }[t / 180 % 6], t / 180 % 2 == 0 ? -1 : 1),
            AnimationPreviewAction.Tap => new(t % 2400 < 1800 ? "happy" : "idle", Y: t / 300 % 2 == 0 ? -1 : 1, Heart: t % 2400 < 1200, HeartY: t / 180 % 2 == 0 ? 0 : -3),
            AnimationPreviewAction.Sleep => new("blink", Sleeping: true, ZCount: 1 + t / 600 % 3),
            AnimationPreviewAction.Wake when t < 600 => new("blink", Sleeping: true, ZCount: 1 + t / 200 % 3),
            AnimationPreviewAction.Wake when t < 2400 => new("happy", Y: (t - 600) / 300 % 2 == 0 ? -1 : 1),
            _ => new((t - 2400) % 2000 >= 1800 ? "blink" : "idle", Y: (t - 2400) / 600 % 2 == 0 ? -1 : 1)
        };
    }
}
