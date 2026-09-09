using Hindsight.App.Controls;
using Hindsight.Core.Analysis;
using Hindsight.Core.Context;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Settings;

namespace Hindsight.App.Services;

/// <summary>Runs <see cref="AnomalyDetector.DetectAndExplain"/> and the whole-source aggregation on the thread pool so
/// the UI stays responsive. A new <see cref="AnalyzeAsync"/> cancels the previous run; the cancelled call returns null.</summary>
public sealed class AnalysisRunner : IDisposable
{
    private CancellationTokenSource? _current;
    private int _running;
    private bool _disposed;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>Detects and explains anomalies and aggregates the whole source in one background pass. Returns null
    /// when the run was superseded or cancelled. Nothing on the thread pool touches a WPF object: the result is plain
    /// data the caller binds on the UI thread.</summary>
    public async Task<AnalysisSource?> AnalyzeAsync(IReadOnlyList<Tick> ticks, IIdentitySource ids, int coreCount, IReadOnlyList<Marker> markers, AppSettings settings, IContextSource? context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelCurrent();
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _current, cts);
        SetRunning(true);
        try
        {
            var token = cts.Token;
            var result = await Task.Run(() =>
            {
                var anomalies = AnomalyDetector.DetectAndExplain(ticks, ids, markers, settings, token);
                token.ThrowIfCancellationRequested();
                return AnalysisSource.Build(new AnalysisContext(ticks, ids, coreCount, markers, anomalies, context), settings);
            }, token).ConfigureAwait(true);
            // Cancellation is cooperative and checked once per metric, so a superseded run can still complete
            // normally after its successor has already started (or finished). Only hand back the result when
            // this run is still the current one; otherwise a stale result could overwrite a newer one.
            return Volatile.Read(ref _current) == cts ? result : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Analysis", ex);
            throw;
        }
        finally
        {
            // Only the run that is still the current one clears the busy flag; a superseded run leaves it set for its successor.
            if (Interlocked.CompareExchange(ref _current, null, cts) == cts) SetRunning(false);
            cts.Dispose();
        }
    }

    /// <summary>Cancels the run in flight, if any.</summary>
    public void Cancel()
    {
        if (CancelCurrent()) SetRunning(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
    }

    private bool CancelCurrent()
    {
        var old = Interlocked.Exchange(ref _current, null);
        if (old is null) return false;
        try { old.Cancel(); }
        catch (ObjectDisposedException) { /* the run finished first */ }
        return true;
    }

    private void SetRunning(bool value) => Interlocked.Exchange(ref _running, value ? 1 : 0);
}
