namespace Hindsight.Core.Context;

/// <summary>Trips once ETW reports too many lost events for too many consecutive reads in a row.</summary>
public sealed class OverloadGuard
{
    public const int LostPerSecondLimit = 1000, ConsecutiveSeconds = 10;

    private int _streak;

    public bool Tripped { get; private set; }

    /// <summary>Feed the lost-event delta of one read; returns true exactly once, on the read that trips the guard.</summary>
    public bool Observe(int lostDelta)
    {
        if (lostDelta > LostPerSecondLimit) _streak++;
        else _streak = 0;

        if (!Tripped && _streak >= ConsecutiveSeconds)
        {
            Tripped = true;
            return true;
        }
        return false;
    }
}
