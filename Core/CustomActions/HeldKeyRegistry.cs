using System.Threading;

namespace Switchboard.Core.CustomActions;

/// <summary>
/// Tracks synthetic keys currently held down via the "hold-key" action step,
/// keyed by the key itself rather than by which action put it there — so
/// several independently-triggered actions (e.g. one per knob-turn direction,
/// a third for the press) can coordinate through a shared key instead of each
/// owning private state. Every hold is time-boxed: unless something re-arms
/// it (another "hold-key" step for the same key) before the timeout elapses,
/// it's released automatically. That's a hard invariant, not an option — a
/// "hold-key" step can never be used to leave a modifier stuck down forever.
/// </summary>
internal static class HeldKeyRegistry
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<ushort, System.Threading.Timer> Held = [];

    public static void Hold(ushort vk, TimeSpan timeout)
    {
        lock (Gate)
        {
            if (Held.TryGetValue(vk, out var oldTimer))
                oldTimer.Dispose();
            else
                KeystrokeSender.KeyDown(vk);
            Held[vk] = new System.Threading.Timer(_ => Release(vk), null, timeout, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    public static void Release(ushort vk)
    {
        lock (Gate)
        {
            if (!Held.Remove(vk, out var timer)) return;
            timer.Dispose();
            KeystrokeSender.KeyUp(vk);
        }
    }
}
