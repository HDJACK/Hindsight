using Hindsight.Core.Context;
using Hindsight.Core.Etw;
using Hindsight.Core.Model;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Sources;

/// <summary>How often the recorder samples.</summary>
public sealed record RecorderOptions(int IntervalSeconds)
{
    public static RecorderOptions Default => new(1);
}

/// <summary>Drives the sample sources on a timer and writes each aggregated tick to the ring buffer.</summary>
public sealed class Recorder : IDisposable
{
    public const string EtwSourceName = "etw";

    private readonly RingBuffer _ring;
    private readonly Aggregator _aggregator;
    private readonly List<ISampleSource> _sources;
    private readonly ContextAccumulator? _context;
    private readonly ContextStore? _contextStore;
    private readonly Func<int, long>? _startTimeOf;
    private long _lastUnix;
    private long _currentMinute;

    public RecorderOptions Options { get; }
    public bool EtwActive { get; private set; }
    public string? DegradedReason { get; private set; }
    public Exception? LastError { get; private set; }
    public event Action<Tick>? TickWritten;

    public Recorder(RingBuffer ring, Aggregator aggregator, IReadOnlyList<ISampleSource> sources, RecorderOptions? options = null,
        ContextAccumulator? context = null, ContextStore? contextStore = null, Func<int, long>? startTimeOf = null)
    {
        _ring = ring;
        _aggregator = aggregator;
        _sources = sources.ToList();
        Options = options ?? RecorderOptions.Default;
        _context = context;
        _contextStore = contextStore;
        _startTimeOf = startTimeOf;
        _lastUnix = ring.Latest?.UnixTime ?? 0;
    }

    public void Start()
    {
        foreach (var s in _sources.ToArray())
        {
            try
            {
                s.Start();
                if (s.Name == EtwSourceName) EtwActive = true;
            }
            catch (EtwUnavailableException ex) when (s.Name == EtwSourceName)
            {
                DegradedReason = ex.Message;
                EtwActive = false;
                _sources.Remove(s);
                s.Dispose();
            }
        }
    }

    public Tick Step(long nowUnix)
    {
        double interval = 1;
        if (_lastUnix != 0)
        {
            long gapThreshold = 5L * Options.IntervalSeconds;
            if (nowUnix - _lastUnix > gapThreshold)
            {
                long step = Options.IntervalSeconds;
                long from = Math.Max(_lastUnix + step, nowUnix - (long)_ring.Capacity * step);
                for (long t = from; t < nowUnix; t += step)
                    _ring.Write(new Tick { UnixTime = t, Flags = TickFlags.Gap, IntervalSeconds = (byte)step });
            }
            // The real elapsed time is the correct CPU divisor whether or not gap ticks
            // were written for the missing seconds - a source's delta still covers the
            // whole real interval, not just the last second.
            if (nowUnix > _lastUnix)
            {
                interval = nowUnix - _lastUnix;
            }
        }

        // Context is accumulated per wall-clock minute: as soon as a tick lands in a new
        // minute, the previous minute's top keys are flushed to the store.
        long minute = ContextEntry.MinuteOf(nowUnix);
        if (_context != null && _currentMinute != 0 && minute != _currentMinute) FlushMinute(_currentMinute);
        _currentMinute = minute;

        var snapshots = new List<SourceSnapshot>(_sources.Count);
        foreach (var s in _sources) snapshots.Add(s.Read());

        var tick = _aggregator.Advance(nowUnix, interval, snapshots, EtwActive);
        _ring.Write(tick);
        _lastUnix = nowUnix;
        TickWritten?.Invoke(tick);
        return tick;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Options.IntervalSeconds));
        try
        {
            // Never continue on the caller's SynchronizationContext: the app starts this from the UI thread,
            // and Step must run on a worker, or shutdown waits on a dispatcher that is already gone.
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { Step(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
                catch (Exception ex) when (ex is not OperationCanceledException) { LastError = ex; }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Flushes the minute currently being accumulated (on shutdown, or before saving a recording).</summary>
    public void FlushContext()
    {
        if (_currentMinute != 0) FlushMinute(_currentMinute);
    }

    private void FlushMinute(long minute)
    {
        if (_context is null || _contextStore is null || _context.IsEmpty) return;
        var entries = _context.Flush(minute, _startTimeOf ?? (_ => 0));
        if (entries.Count > 0) _contextStore.Append(entries);
    }

    public void Dispose()
    {
        FlushContext();
        foreach (var s in _sources) s.Dispose();
    }
}
