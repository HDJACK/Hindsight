using Hindsight.Core.Model;

namespace Hindsight.Core.Analysis;

/// <summary>Scores how interesting a process is in a given second. The aggregator keeps the highest scores
/// in the tick and drops the rest. Implement it to rank processes differently.</summary>
public interface IProcessRanker
{
    double Score(in ProcessSample sample, in SystemTotals totals);
}
