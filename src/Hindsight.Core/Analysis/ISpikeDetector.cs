using Hindsight.Core.Model;

namespace Hindsight.Core.Analysis;

/// <summary>Decides whether a second on the timeline is marked as a spike. Implement it to detect spikes differently.</summary>
public interface ISpikeDetector
{
    /// <param name="history">Previous ticks, oldest first, at most 300.</param>
    bool IsSpike(Tick current, ReadOnlySpan<Tick> history);
}
