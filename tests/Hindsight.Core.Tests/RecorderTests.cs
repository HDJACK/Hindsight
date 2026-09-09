using Hindsight.Core.Analysis;
using Hindsight.Core.Context;
using Hindsight.Core.Etw;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Settings;
using Hindsight.Core.Sources;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Tests;

public class RecorderTests
{
    private sealed class FakeSource(string name, Func<SourceSnapshot>? read = null, Exception? startError = null) : ISampleSource
    {
        public string Name => name;
        public int Reads;
        public void Start() { if (startError != null) throw startError; }
        public SourceSnapshot Read() { Reads++; return read?.Invoke() ?? new SourceSnapshot(); }
        public void Dispose() { }
    }

    private static Aggregator Agg() =>
        new(new ProcessTable(), new IdentityStore(null), new WeightedProcessRanker(() => AppSettings.Default), new ThresholdSpikeDetector(() => AppSettings.Default), 4);

    [Fact]
    public void Step_ReadsAllSources_WritesTick_RaisesEvent()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        var a = new FakeSource("snapshot", () => { var s = new SourceSnapshot { Totals = new SystemTotals(12, 1, 1) }; return s; });
        var b = new FakeSource("etw");
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { a, b });
        Tick? seen = null;
        rec.TickWritten += t => seen = t;
        rec.Start();
        var tick = rec.Step(1000);
        Assert.Equal(1, a.Reads);
        Assert.Equal(1, b.Reads);
        Assert.Equal(1000, tick.UnixTime);
        Assert.Equal(12f, tick.CpuTotalPercent);
        Assert.True(tick.Has(TickFlags.EtwActive));
        Assert.Equal(1, ring.Count);
        Assert.Same(tick, seen);
    }

    [Fact]
    public void EtwStartFailure_DegradesInsteadOfCrashing()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        var snap = new FakeSource("snapshot");
        var etw = new FakeSource("etw", startError: new EtwUnavailableException("insufficient privileges"));
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { snap, etw });
        rec.Start();
        Assert.False(rec.EtwActive);
        Assert.Equal("insufficient privileges", rec.DegradedReason);
        var tick = rec.Step(1);
        Assert.False(tick.Has(TickFlags.EtwActive));
        Assert.Equal(0, etw.Reads);
    }

    [Fact]
    public void OtherStartFailure_Propagates()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { new FakeSource("snapshot", startError: new InvalidOperationException("boom")) });
        Assert.Throws<InvalidOperationException>(() => rec.Start());
    }

    [Fact]
    public void LargeTimeJump_FillsGapTicks()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { new FakeSource("snapshot") });
        rec.Start();
        rec.Step(100);
        rec.Step(110);
        var all = ring.ReadAll();
        Assert.Equal(11, all.Count);
        Assert.Equal(new long[] { 100, 101, 109, 110 }, new[] { all[0].UnixTime, all[1].UnixTime, all[9].UnixTime, all[10].UnixTime });
        Assert.True(all[1].Has(TickFlags.Gap));
        Assert.False(all[10].Has(TickFlags.Gap));
    }

    [Fact]
    public void GapFill_StepsByInterval_AndIsBoundedByCapacityTimesInterval()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, 10);
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { new FakeSource("snapshot") }, new RecorderOptions(5));
        rec.Start();
        rec.Step(100);
        rec.Step(160);   // 60 s gap at 5 s interval -> gap ticks 105,110,...,155 (11), then 160
        var all = ring.ReadAll();
        Assert.Equal(10, all.Count);                       // bounded by capacity
        Assert.Equal(160, all[^1].UnixTime);
        Assert.True(all[^2].Has(TickFlags.Gap));
        Assert.Equal(155, all[^2].UnixTime);
        Assert.Equal(5, all[^2].IntervalSeconds);
        rec.Step(1000);  // huge gap: only capacity*interval = 50 s of gap ticks are written (950..995)
        all = ring.ReadAll();
        Assert.Equal(10, all.Count);
        Assert.Equal(1000, all[^1].UnixTime);
        Assert.Equal(955, all[0].UnixTime);
    }

    [Fact]
    public void GapPath_UsesRealIntervalForCpu()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        var snap = new FakeSource("snapshot", () => { var s = new SourceSnapshot(); s.Deltas.Add(new ProcessDelta(1, 5, 10.0, 0, 0, 0, 0)); return s; });
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { snap });
        rec.Start();
        rec.Step(100);
        var tick = rec.Step(110);
        var sample = Assert.Single(tick.Samples);
        Assert.Equal(25f, sample.CpuPercent);
    }

    [Fact]
    public void SmallJitter_DoesNotFillGaps()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { new FakeSource("snapshot") });
        rec.Start();
        rec.Step(100);
        rec.Step(103);
        Assert.Equal(2, ring.Count);
    }

    /// <summary>A context that, unlike the default one, makes itself Current on the thread that runs posted callbacks (like WPF's).</summary>
    private sealed class TrackingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ => { SetSynchronizationContext(this); d(state); });
    }

    [Fact]
    public async Task RunAsync_DoesNotRunStepsOnTheCallersSynchronizationContext()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        bool? sawContext = null;
        var src = new FakeSource("snapshot", () => { sawContext = SynchronizationContext.Current is not null; return new SourceSnapshot(); });
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { src });
        rec.Start();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new TrackingContext());
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            await rec.RunAsync(cts.Token);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.NotNull(sawContext);
        Assert.False(sawContext, "Step ran on the caller's SynchronizationContext (e.g. the WPF UI thread)");
    }

    [Fact]
    public async Task RunAsync_SurvivesReadException_AndRecordsLastError()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        int calls = 0;
        var flaky = new FakeSource("snapshot", () =>
        {
            if (++calls == 1) throw new InvalidOperationException("transient");
            return new SourceSnapshot();
        });
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { flaky });
        rec.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
        await rec.RunAsync(cts.Token);
        Assert.IsType<InvalidOperationException>(rec.LastError);
        Assert.True(calls >= 2, $"calls = {calls}");
        Assert.True(ring.Count >= 1, $"ring.Count = {ring.Count}");
    }

    [Fact]
    public void Options_Interval_ScalesGapThresholdAndTimerPeriod()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { new FakeSource("snapshot") }, new RecorderOptions(2));
        rec.Start();
        rec.Step(100);
        rec.Step(110);   // 10 s gap == 5 * 2 s -> no gap ticks
        Assert.Equal(2, ring.Count);
        rec.Step(121);   // 11 s > 10 s -> gap ticks step by the 2 s interval: 112,114,...,120
        Assert.Equal(8, ring.Count);
        Assert.True(ring.ReadAt(2).Has(TickFlags.Gap));
        Assert.Equal(2, rec.Options.IntervalSeconds);
    }

    [Fact]
    public void Step_FlushesContextWhenMinuteChanges()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        var acc = new ContextAccumulator();
        using var store = new ContextStore(null, 0);
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { new FakeSource("snapshot") }, null, acc, store, _ => 42);
        rec.Start();

        acc.Add(7, ContextKind.File, "x", 3);
        rec.Step(1259);
        Assert.Equal(0, store.Count);

        rec.Step(1260);
        var entries = store.ForMinute(1200, 7);
        var e = Assert.Single(entries);
        Assert.Equal(42, e.StartTime);
        Assert.Equal(3, e.Value);
        Assert.Equal("x", e.Key);

        acc.Add(7, ContextKind.Dns, "example.com", 1);
        rec.Dispose();
        var later = store.ForMinute(1260, 7);
        var d = Assert.Single(later);
        Assert.Equal("example.com", d.Key);
    }

    [Fact]
    public void FlushContext_MidMinute_ThenRollover_MergesIntoOneRow()
    {
        using var f = new TempFile();
        using var ring = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        var acc = new ContextAccumulator();
        using var store = new ContextStore(null, 0);
        using var rec = new Recorder(ring, Agg(), new ISampleSource[] { new FakeSource("snapshot") }, null, acc, store, _ => 42);
        rec.Start();

        acc.Add(7, ContextKind.File, "x", 3);
        rec.Step(1210);
        rec.FlushContext();

        acc.Add(7, ContextKind.File, "x", 4);
        rec.Step(1260);

        var entries = store.ForMinute(1200, 7);
        var e = Assert.Single(entries);
        Assert.Equal("x", e.Key);
        Assert.Equal(7, e.Value);
    }
}
