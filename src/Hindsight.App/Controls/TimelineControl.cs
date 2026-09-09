using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Hindsight.Core.Analysis;
using Hindsight.Core.Model;

namespace Hindsight.App.Controls;

/// <summary>Lane-driven timeline (CPU, GPU, File I/O, disk, network, RAM, etc.) over the last window, spikes, gaps, markers, selection, and an optional foreground/idle context strip.</summary>
public sealed partial class TimelineControl : FrameworkElement
{
    private const double LabelWidth = 70;
    private const double AxisHeight = 22;
    private const double ContextStripHeight = 14;

    /// <summary>Height of the mini-map strip below the axis when <see cref="ShowMiniMap"/> is on.</summary>
    private const double MiniMapHeight = 24;

    /// <summary>How close to a range edge (in pixels) a press must be to drag that edge instead of panning.</summary>
    private const double RangeEdgeGrabPx = 5;

    /// <summary>Extra slack (in pixels) applied on each side of an anomaly hit-test, matching the minimum-width widening used when drawing.</summary>
    private const double AnomalyHitTolerancePx = 2;

    /// <summary>Smallest view window, in slots, the user can zoom to (a shorter buffer clamps to its own slot count).</summary>
    public const int MinViewSlots = 30;

    private const double DragThresholdPx = 4;
    private const double MinAxisLabelSpacingPx = 80;

    /// <summary>One lane's data source, scaling, and formatting.</summary>
    public sealed record LaneSpec(LaneKind Kind, string Label, Func<Tick, double> Value, bool FixedMax100, Func<double, string> FormatMax, Pen Pen);

    private IReadOnlyList<Tick> _ticks = Array.Empty<Tick>();
    private IReadOnlyList<Marker> _markers = Array.Empty<Marker>();
    private double[] _diskPhys = Array.Empty<double>();

    private IReadOnlyList<LaneSpec> _lanes = DefaultLanes();
    private bool _showForeground;
    private bool _hasForeground;
    private Func<int, string?>? _nameForPid;

    private static readonly Brush SpikeBrush = new SolidColorBrush(Color.FromArgb(0x2C, 0xff, 0x8c, 0x00));
    private static readonly Brush GapBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80));
    private static readonly Pen SelectionPen = new(Brushes.White, 1.5);

    private static readonly Pen CpuPen = new(Brushes.OrangeRed, 1.2);
    private static readonly Pen GpuPen = new(Brushes.MediumPurple, 1.2);
    private static readonly Pen FileIoPen = new(Brushes.MediumSeaGreen, 1.2);
    private static readonly Pen DiskPhysPen = new(Brushes.Goldenrod, 1.2);
    private static readonly Pen NetworkPen = new(Brushes.CornflowerBlue, 1.2);
    private static readonly Pen RamPen = new(Brushes.Plum, 1.2);
    private static readonly Pen DiskLatencyPen = new(Brushes.Tomato, 1.2);
    private static readonly Pen CpuMhzPen = new(Brushes.LightSkyBlue, 1.2);
    private static readonly Pen CommitPen = new(Brushes.Khaki, 1.2);

    private static readonly Brush[] ContextPalette =
    {
        new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75)), new SolidColorBrush(Color.FromRgb(0x98, 0xC3, 0x79)),
        new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x7B)), new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF)),
        new SolidColorBrush(Color.FromRgb(0xC6, 0x78, 0xDD)), new SolidColorBrush(Color.FromRgb(0x56, 0xB6, 0xC2)),
        new SolidColorBrush(Color.FromRgb(0xD1, 0x9A, 0x66)), new SolidColorBrush(Color.FromRgb(0xAB, 0xB2, 0xBF)),
        new SolidColorBrush(Color.FromRgb(0xBE, 0x50, 0x46)), new SolidColorBrush(Color.FromRgb(0x7F, 0x9C, 0x4A)),
        new SolidColorBrush(Color.FromRgb(0x4A, 0x7F, 0x9C)), new SolidColorBrush(Color.FromRgb(0x9C, 0x4A, 0x7F)),
    };

    /// <summary>All lane specs, in canonical order.</summary>
    public static readonly IReadOnlyList<LaneSpec> AllLanes = new[]
    {
        new LaneSpec(LaneKind.Cpu, "CPU %", t => t.CpuTotalPercent, true, _ => "100 %", CpuPen),
        new LaneSpec(LaneKind.Gpu, "GPU %", t => t.GpuTotalPercent, true, _ => "100 %", GpuPen),
        new LaneSpec(LaneKind.FileIo, "File I/O", t => t.DiskBytes, false, RateLabel, FileIoPen),
        new LaneSpec(LaneKind.DiskPhysical, "Disk (phys)", t => t.Samples.Sum(s => (double)(s.DiskReadBytes + s.DiskWriteBytes)), false, RateLabel, DiskPhysPen),
        new LaneSpec(LaneKind.Network, "Network", t => t.NetBytes, false, RateLabel, NetworkPen),
        new LaneSpec(LaneKind.RamFree, "RAM free", t => t.RamAvailableBytes, false, max => Format.Bytes((long)max), RamPen),
        new LaneSpec(LaneKind.DiskLatency, "Disk latency", t => t.DiskLatencyMs, false, max => $"{max:0} ms", DiskLatencyPen),
        new LaneSpec(LaneKind.CpuMhz, "CPU MHz", t => t.CpuMhz, false, max => $"{max:0} MHz", CpuMhzPen),
        new LaneSpec(LaneKind.Commit, "Commit %", t => t.CommitPercent, true, _ => "100 %", CommitPen),
    };

    /// <summary>Formats a rate-lane max label as bytes/sec. The value passed in is already per-second (divided by the tick interval in OnRender); the plotted lane values themselves stay per-tick bytes.</summary>
    private static string RateLabel(double perSecond) => Format.Bytes((long)perSecond) + "/s";

    private static readonly HashSet<LaneKind> RateLanes = new() { LaneKind.FileIo, LaneKind.DiskPhysical, LaneKind.Network };

    private static IReadOnlyList<LaneSpec> DefaultLanes() =>
        new[] { LaneKind.Cpu, LaneKind.FileIo, LaneKind.Network, LaneKind.RamFree }
            .Select(k => AllLanes.First(l => l.Kind == k)).ToArray();

    // ---- slot space -------------------------------------------------------
    // A "slot" is one tick-interval-wide column of the x axis. Slot s holds tick index s - SlotOffset,
    // so a partially filled live buffer keeps its newest tick at the right edge. The view window
    // [ViewFrom, ViewFrom + ViewSlots) selects which slots the plot area shows.

    private int _windowSlots;   // 0 = fit the available ticks; otherwise the buffer capacity (newest at the right edge)
    /// <summary>Anchors the x axis to a fixed number of slots (the buffer capacity) so a filling buffer does not stretch.</summary>
    public void SetWindow(int slots)
    {
        int value = Math.Max(0, slots);
        bool modeChanged = value != _windowSlots;
        _windowSlots = value;
        // The meaning of a slot index changed, so a stale zoom would point at unrelated data: refit.
        // Live refreshes pass the same capacity every second, so this does not disturb a zoomed view.
        if (modeChanged) Fit(); else NormalizeView();
        InvalidateVisual();
    }
    private int Slots => _windowSlots > 0 ? Math.Max(_windowSlots, _ticks.Count) : Math.Max(1, _ticks.Count);
    private int SlotOffset => Slots - _ticks.Count;

    private int _viewFrom;
    private int _viewSlots;     // 0 = "fit": the view tracks Slots as the buffer grows
    private bool _follow = true;
    private long? _timeOrigin;

    // Live, not following: the view is pinned to the data under it rather than to slot indices. A rolling ring
    // moves every tick one slot to the left, so a fixed ViewFrom would walk off whatever the user zoomed to.
    private long? _anchorUnix;   // unix time of the first visible tick, captured before the data is replaced
    private int _anchorLead;     // signed slot distance from ViewFrom to that tick, negative while the view
                                 // reaches into the empty left part of a buffer that is still filling

    /// <summary>First visible slot.</summary>
    public int ViewFrom => Math.Clamp(_viewFrom, 0, Slots - ViewSlots);

    /// <summary>Number of visible slots (at least <see cref="MinViewSlots"/> unless the buffer itself is shorter).</summary>
    public int ViewSlots
    {
        get
        {
            int total = Slots;
            return _viewSlots <= 0 ? total : Math.Clamp(_viewSlots, Math.Min(MinViewSlots, total), total);
        }
    }

    /// <summary>Live: the view is pinned to the right edge. True after <see cref="Fit"/>, false after any zoom or pan.</summary>
    public bool Follow => _follow;

    /// <summary>True when the view shows less than the whole slot range.</summary>
    public bool IsZoomed => ViewSlots < Slots || ViewFrom > 0;

    /// <summary>When set, axis labels are relative ("+m:ss" / "+h:mm:ss") to this unix time instead of wall-clock.</summary>
    public long? TimeOrigin
    {
        get => _timeOrigin;
        set { _timeOrigin = value; InvalidateVisual(); }
    }

    /// <summary>Raised after any change of <see cref="ViewFrom"/>, <see cref="ViewSlots"/> or <see cref="Follow"/>.</summary>
    public event Action? ViewChanged;

    /// <summary>Shows the whole slot range and re-enables follow.</summary>
    public void Fit()
    {
        _anchorUnix = null;
        CommitView(0, Slots, follow: true);
    }

    /// <summary>Turns follow on (jumping to the right edge) or off.</summary>
    public void SetFollow(bool on) => CommitView(on ? Slots - ViewSlots : ViewFrom, ViewSlots, on);

    /// <summary>Sets the view window explicitly (clamped). Turns follow off; used by compare sync.</summary>
    public void SetView(int from, int slots) => CommitView(from, slots, follow: false);

    /// <summary>Zooms by <paramref name="factor"/> (&lt; 1 zooms in) keeping the slot under <paramref name="plotX"/> (pixels inside the plot area, null = centre) in place.</summary>
    public void ZoomAt(double factor, double? plotX)
    {
        int oldSlots = ViewSlots, oldFrom = ViewFrom, total = Slots;
        int newSlots = (int)Math.Round(oldSlots * factor);
        newSlots = Math.Clamp(newSlots, Math.Min(MinViewSlots, total), total);
        // Rounding can swallow small steps (0.98 of a narrow view); nudge by one slot so the gesture still moves.
        if (newSlots == oldSlots)
        {
            newSlots = factor < 1 ? oldSlots - 1 : oldSlots + 1;
            newSlots = Math.Clamp(newSlots, Math.Min(MinViewSlots, total), total);
        }
        // The anchor slot must keep the same fraction of the plot width:
        //   anchor = oldFrom + f * oldSlots  =>  newFrom = anchor - f * newSlots
        double f = plotX.HasValue ? Math.Clamp(plotX.Value / PlotWidth, 0, 1) : 0.5;
        double anchor = oldFrom + f * oldSlots;
        int newFrom = (int)Math.Round(anchor - f * newSlots);
        // Already at full extent and the nudge above still couldn't move: leave follow alone instead of
        // silently dropping it (zooming out further at the full view is a no-op, not a pan).
        if (newSlots == oldSlots && newFrom == oldFrom) return;
        CommitView(newFrom, newSlots, follow: false);
    }

    /// <summary>Moves the view window by <paramref name="slots"/> slots (positive = later in time).</summary>
    public void Pan(int slots) => CommitView(ViewFrom + slots, ViewSlots, follow: false);

    /// <summary>Fits the given unix range into the view with 10 % padding on each side.</summary>
    public void ZoomTo(long fromUnix, long toUnix)
    {
        if (_ticks.Count == 0) return;
        int a = NearestIndex(Math.Min(fromUnix, toUnix)), b = NearestIndex(Math.Max(fromUnix, toUnix));
        int firstSlot = SlotOffset + a, lastSlot = SlotOffset + b;
        int span = Math.Max(1, lastSlot - firstSlot + 1);
        int pad = (int)Math.Round(span * 0.1);
        CommitView(firstSlot - pad, span + 2 * pad, follow: false);
    }

    /// <summary>Unix range of the ticks currently visible, or null when none are.</summary>
    public TimeRange? ViewRange
    {
        get
        {
            int first = VisibleFirst, last = VisibleLast;
            return first > last ? null : new TimeRange(_ticks[first].UnixTime, _ticks[last].UnixTime);
        }
    }

    /// <summary>Clamps the view into the current slot range and raises <see cref="ViewChanged"/> if anything moved.</summary>
    private void CommitView(int from, int slots, bool follow)
    {
        int total = Slots;
        int vs = Math.Clamp(slots, Math.Min(MinViewSlots, total), total);
        int vf = Math.Clamp(from, 0, total - vs);
        bool changed = vs != ViewSlots || vf != ViewFrom || follow != _follow;
        _viewSlots = vs >= total ? 0 : vs;   // 0 keeps a full view "fitted" as the buffer grows
        _viewFrom = vf;
        _follow = follow;
        if (!changed) return;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Re-clamps after the data or the window changed; pins the right edge while following.</summary>
    private void NormalizeView()
    {
        int vs = ViewSlots;
        CommitView(_follow && _windowSlots > 0 ? Slots - vs : _viewFrom, vs, _follow);
    }

    /// <summary>Live and not following: remembers which tick the view starts on, so the incoming data can be
    /// followed instead of the slot index it happens to sit in right now.</summary>
    private void CaptureViewAnchor()
    {
        _anchorUnix = null;
        _anchorLead = 0;
        if (_windowSlots <= 0 || _follow || _ticks.Count == 0) return;
        int first = VisibleFirst;
        if (first > VisibleLast) return;   // the view sits entirely in the empty part of a short buffer
        _anchorUnix = _ticks[first].UnixTime;
        _anchorLead = ViewFrom - (SlotOffset + first);
    }

    /// <summary>Counterpart of <see cref="CaptureViewAnchor"/>: puts the anchored tick back under the left edge of
    /// the view. Without an anchor (fitted, following, or no data) the old slot-based clamp applies; an anchor that
    /// rolled out of the buffer resolves to its first tick, i.e. the view clamps to the left edge.</summary>
    private void RestoreViewAnchor()
    {
        if (_anchorUnix is not { } anchor || _windowSlots <= 0 || _follow || _ticks.Count == 0)
        {
            NormalizeView();
            return;
        }
        CommitView(SlotOffset + NearestIndex(anchor) + _anchorLead, ViewSlots, follow: false);
    }

    /// <summary>First visible tick index (index = slot - SlotOffset), or a value &gt; <see cref="VisibleLast"/> when nothing is visible.</summary>
    private int VisibleFirst => Math.Max(0, ViewFrom - SlotOffset);
    /// <summary>Last visible tick index.</summary>
    private int VisibleLast => Math.Min(_ticks.Count - 1, ViewFrom + ViewSlots - 1 - SlotOffset);

    /// <summary>Inverse of <see cref="XOf"/>: the slot under a control-space x coordinate.</summary>
    private int SlotAtX(double x) => (int)Math.Floor((x - LabelWidth) / PlotWidth * ViewSlots) + ViewFrom;

    /// <summary>When true, right-clicking a marker offers to delete it (live buffer only).</summary>
    public bool AllowMarkerDeletion { get; set; }
    public event Action<Marker>? MarkerDeleteRequested;

    public long? SelectedUnixTime { get; private set; }
    public event Action<Tick?>? SelectionChanged;

    // ---- range, anomalies, overlay, mini-map ------------------------------

    private TimeRange? _range;
    private IReadOnlyList<Anomaly> _anomalies = Array.Empty<Anomaly>();
    private Anomaly? _highlighted;
    private ProcessSeries? _overlay;
    private bool _showMiniMap;

    /// <summary>The analysis range, in unix time, so it survives zoom and pan. Null when nothing is selected.</summary>
    public TimeRange? SelectedRange => _range;

    /// <summary>Raised whenever <see cref="SelectedRange"/> changes, including live during a drag and on Escape (with null).</summary>
    public event Action<TimeRange?>? RangeChanged;

    /// <summary>Sets (or with null clears) the analysis range and raises <see cref="RangeChanged"/>.</summary>
    public void SetRange(TimeRange? range)
    {
        if (Nullable.Equals(_range, range)) return;
        _range = range;
        InvalidateVisual();
        RangeChanged?.Invoke(range);
    }

    /// <summary>Makes the currently visible time span the analysis range.</summary>
    public void SelectViewAsRange() => SetRange(ViewRange);

    /// <summary>Bands drawn behind the lane the anomaly's metric belongs to.</summary>
    public void SetAnomalies(IReadOnlyList<Anomaly> anomalies)
    {
        _anomalies = anomalies;
        // Live detection re-runs hand out fresh instances for the same detections, and the highlight is drawn by
        // identity, so carry it over to the new instance (or drop it when that detection is gone).
        if (_highlighted is not null)
        {
            Anomaly? match = null;
            foreach (var a in anomalies) if (AnomalyKey.Equals(a, _highlighted)) { match = a; break; }
            _highlighted = match;
        }
        InvalidateVisual();
    }

    /// <summary>The anomaly whose band gets a white border, or null.</summary>
    public Anomaly? HighlightedAnomaly
    {
        get => _highlighted;
        set { _highlighted = value; InvalidateVisual(); }
    }

    /// <summary>Raised when a click without a drag lands inside an anomaly band (the most severe one when several overlap).</summary>
    public event Action<Anomaly>? AnomalyClicked;

    /// <summary>Draws one process' contribution as a dashed white curve per lane; null clears it. The series must be aligned to the ticks passed to <see cref="SetData"/>.</summary>
    public void SetOverlay(ProcessSeries? series)
    {
        _overlay = series;
        InvalidateVisual();
    }

    /// <summary>Shows the mini-map strip below the axis (whole recording, with the view window marked).</summary>
    public bool ShowMiniMap
    {
        get => _showMiniMap;
        set
        {
            if (_showMiniMap == value) return;
            _showMiniMap = value;
            InvalidateVisual();
        }
    }

    static TimelineControl()
    {
        CpuPen.Freeze(); GpuPen.Freeze(); FileIoPen.Freeze(); DiskPhysPen.Freeze(); NetworkPen.Freeze();
        RamPen.Freeze(); DiskLatencyPen.Freeze(); CpuMhzPen.Freeze(); CommitPen.Freeze();
        SelectionPen.Freeze();
        foreach (var b in ContextPalette) b.Freeze();
    }

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Selects which lanes are drawn, top to bottom, in the order given.</summary>
    public void SetLanes(IReadOnlyList<LaneKind> kinds)
    {
        var lanes = kinds.Select(k => AllLanes.FirstOrDefault(l => l.Kind == k)).Where(l => l is not null).Select(l => l!).ToArray();
        _lanes = lanes.Length > 0 ? lanes : DefaultLanes();
        InvalidateVisual();
    }

    /// <summary>Enables/disables the foreground-process context strip and supplies a pid-to-name resolver.</summary>
    public void SetContext(bool showForeground, Func<int, string?> nameForPid)
    {
        _showForeground = showForeground;
        _nameForPid = nameForPid;
        InvalidateVisual();
    }

    public void SetData(IReadOnlyList<Tick> ticks, IReadOnlyList<Marker> markers)
    {
        CaptureViewAnchor();
        _ticks = ticks; _markers = markers;
        _diskPhys = new double[ticks.Count];
        _hasForeground = false;
        for (int i = 0; i < ticks.Count; i++)
        {
            double sum = 0;
            foreach (var s in ticks[i].Samples) sum += s.DiskReadBytes + s.DiskWriteBytes;
            _diskPhys[i] = sum;
            if (ticks[i].ForegroundPid != 0) _hasForeground = true;
        }
        if (SelectedUnixTime.HasValue && IndexOf(SelectedUnixTime.Value) < 0) SelectedUnixTime = null;
        RestoreViewAnchor();
        InvalidateVisual();
    }

    private double PlotWidth => Math.Max(1, ActualWidth - LabelWidth);
    private bool ContextVisible => _showForeground && _hasForeground;
    private double StripHeight => ContextVisible ? ContextStripHeight : 0;
    private double MiniMapArea => _showMiniMap ? MiniMapHeight : 0;
    private double LanesHeight => Math.Max(1, ActualHeight - AxisHeight - StripHeight - MiniMapArea);
    private double LaneHeight => Math.Max(1, LanesHeight / Math.Max(1, _lanes.Count));

    /// <summary>Top of the axis band: below the lanes and the context strip, above the mini-map.</summary>
    private double AxisTop => LanesHeight + StripHeight;

    /// <summary>The mini-map strip (empty height when it is hidden).</summary>
    private Rect MiniMapRect => new(LabelWidth, AxisTop + AxisHeight, PlotWidth, MiniMapArea);

    /// <summary>Centre x of a tick's column: the tick sits in slot SlotOffset + index, which is (slot - ViewFrom) view slots from the left edge.</summary>
    private double XOf(int index) => LabelWidth + (SlotOffset + index - ViewFrom + 0.5) / ViewSlots * PlotWidth;

    private double LaneValue(LaneSpec spec, int tickIndex) =>
        spec.Kind == LaneKind.DiskPhysical ? _diskPhys[tickIndex] : spec.Value(_ticks[tickIndex]);

    private int IndexOf(long unix)
    {
        if (_ticks.Count == 0) return -1;
        long first = _ticks[0].UnixTime;
        long idx = unix - first;
        return idx >= 0 && idx < _ticks.Count && _ticks[(int)idx].UnixTime == unix ? (int)idx : BinarySearch(unix);
    }

    private int BinarySearch(long unix)
    {
        int lo = 0, hi = _ticks.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            long v = _ticks[mid].UnixTime;
            if (v == unix) return mid;
            if (v < unix) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>Index of the first tick at or after <paramref name="unix"/>, clamped to the ends (-1 only when there are no ticks).</summary>
    private int NearestIndex(long unix)
    {
        if (_ticks.Count == 0) return -1;
        if (unix <= _ticks[0].UnixTime) return 0;
        if (unix >= _ticks[^1].UnixTime) return _ticks.Count - 1;
        int lo = 0, hi = _ticks.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_ticks[mid].UnixTime < unix) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ---- input ------------------------------------------------------------

    /// <summary>What a left-button drag does, decided once on button down.</summary>
    private enum DragMode { Pan, RangeNew, RangeEdge, MiniMap }

    private Point _pressPoint;
    private bool _pressed;
    private bool _dragging;
    private int _dragOriginFrom;
    private DragMode _mode;
    private long _rangeAnchorUnix;   // the range edge that stays put while the other one follows the cursor

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_ticks.Count == 0) return;
        var p = e.GetPosition(this);
        _pressPoint = p;
        _pressed = true;
        _dragging = false;
        _dragOriginFrom = ViewFrom;
        _mode = PressMode(p);
        if (_mode == DragMode.MiniMap)
        {
            // The mini-map jumps on press and keeps tracking, so it never waits for the drag threshold.
            _dragging = true;
            CaptureMouse();
            PanFromMiniMap(p.X);
        }
    }

    /// <summary>Precedence: Shift (a new range) wins explicitly, then a range edge under the cursor, then the mini-map, then pan.</summary>
    private DragMode PressMode(Point p)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            _rangeAnchorUnix = _ticks[ClampedIndexAt(p.X)].UnixTime;
            return DragMode.RangeNew;
        }
        // Edges are only grabbable over the lanes, where the band is actually drawn.
        if (p.Y < LanesHeight && p.X >= LabelWidth && RangeEdgeAnchor(p.X) is { } anchor)
        {
            _rangeAnchorUnix = anchor;
            return DragMode.RangeEdge;
        }
        if (_showMiniMap && MiniMapRect.Contains(p)) return DragMode.MiniMap;
        return DragMode.Pan;
    }

    /// <summary>When <paramref name="x"/> is within <see cref="RangeEdgeGrabPx"/> of a range edge, the unix time of the opposite edge.</summary>
    private long? RangeEdgeAnchor(double x)
    {
        if (_range is not { } r || !RangeBand(r, out double x0, out double x1)) return null;
        if (Math.Abs(x - x0) <= RangeEdgeGrabPx) return r.ToUnix;
        if (Math.Abs(x - x1) <= RangeEdgeGrabPx) return r.FromUnix;
        return null;
    }

    /// <summary>Tick index under a control-space x, clamped into the recorded ticks.</summary>
    private int ClampedIndexAt(double x) => Math.Clamp(SlotAtX(x) - SlotOffset, 0, _ticks.Count - 1);

    /// <summary>Centres the view on the slot under the mini-map cursor.</summary>
    private void PanFromMiniMap(double x) => SetView(MiniSlotAtX(x) - ViewSlots / 2, ViewSlots);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // The button was released outside the control (before capture, before the drag threshold):
            // without this check the next move would start a button-less pan.
            _pressed = false;
            _dragging = false;
            Cursor = null;
            return;
        }
        double x = e.GetPosition(this).X;
        if (_mode == DragMode.MiniMap) { PanFromMiniMap(x); return; }

        double dx = x - _pressPoint.X;
        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThresholdPx) return;
            _dragging = true;
            CaptureMouse();
            Cursor = _mode == DragMode.Pan ? Cursors.ScrollAll : Cursors.SizeWE;
        }
        if (_mode != DragMode.Pan)
        {
            // Live range: the anchored edge stays, the other one follows the cursor (Of orders them).
            SetRange(TimeRange.Of(_rangeAnchorUnix, _ticks[ClampedIndexAt(x)].UnixTime));
            return;
        }
        // Grab-and-drag: the data follows the cursor, so moving right shows earlier slots.
        // Recomputed from the press origin each move so clamping at an edge does not accumulate drift.
        double colW = PlotWidth / ViewSlots;
        CommitView(_dragOriginFrom - (int)Math.Round(dx / colW), ViewSlots, follow: false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool wasDragging = _dragging;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = null;
        _pressed = false;
        _dragging = false;
        if (wasDragging || _ticks.Count == 0) return;
        var p = e.GetPosition(this);
        SelectAt(p.X);
        if (AnomalyAt(p) is { } anomaly) AnomalyClicked?.Invoke(anomaly);
    }

    /// <summary>The most severe anomaly whose band covers <paramref name="p"/> (x inside the band, y inside that metric's lane), or null.</summary>
    private Anomaly? AnomalyAt(Point p)
    {
        if (_anomalies.Count == 0 || p.X < LabelWidth || p.Y < 0 || p.Y >= LanesHeight) return null;
        int lane = (int)(p.Y / LaneHeight);
        if (lane < 0 || lane >= _lanes.Count) return null;
        var kind = _lanes[lane].Kind;
        Anomaly? best = null;
        foreach (var a in _anomalies)
        {
            if (MetricInfo.Lane(a.Metric) != kind) continue;
            if (!RangeBand(a.Range, out double x0, out double x1)) continue;
            // Matches the minimum-width widening DrawAnomalies applies when drawing (Render.cs).
            if (p.X < x0 - AnomalyHitTolerancePx || p.X > x1 + AnomalyHitTolerancePx) continue;
            if (best is null || a.Severity > best.Severity) best = a;
        }
        return best;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Capture can be taken away by the system (e.g. another control, a modal) mid-drag; reset so a
        // stray move afterwards does not resume panning with no button held.
        _pressed = false;
        _dragging = false;
        Cursor = null;
    }

    private void SelectAt(double x)
    {
        if (x < LabelWidth) return;   // clicking the lane-label gutter should not select a tick
        int idx = SlotAtX(x) - SlotOffset;
        if (idx < 0 || idx >= _ticks.Count) return;
        SelectedUnixTime = _ticks[idx].UnixTime;
        InvalidateVisual();
        SelectionChanged?.Invoke(_ticks[idx]);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_ticks.Count == 0) return;
        double zoomIn = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 0.98 : 0.9;   // Ctrl = fine zoom
        ZoomAt(e.Delta > 0 ? zoomIn : 1 / zoomIn, e.GetPosition(this).X - LabelWidth);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int stepSlots = Math.Max(1, ViewSlots / 10);
        switch (e.Key)
        {
            case Key.Left: Pan(-stepSlots); break;
            case Key.Right: Pan(stepSlots); break;
            case Key.Home: SetView(0, ViewSlots); break;
            case Key.End: SetView(Slots - ViewSlots, ViewSlots); break;
            case Key.OemPlus or Key.Add: ZoomAt(0.9, null); break;
            case Key.OemMinus or Key.Subtract: ZoomAt(1 / 0.9, null); break;
            case Key.F: Fit(); break;
            case Key.Escape:
                if (_range is null) return;
                SetRange(null);
                break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (!AllowMarkerDeletion || _ticks.Count == 0) return;
        double px = e.GetPosition(this).X;
        int first = VisibleFirst, last = VisibleLast;
        Marker? hit = null; double best = 6;
        foreach (var m in _markers)
        {
            int idx = IndexOf(m.UnixTime);
            if (idx < first || idx > last) continue;
            double d = Math.Abs(XOf(idx) - px);
            if (d < best) { best = d; hit = m; }
        }
        if (hit is null) return;
        var menu = new System.Windows.Controls.ContextMenu();
        var item = new System.Windows.Controls.MenuItem { Header = $"Delete marker \"{hit.Text}\" ({Format.Time(hit.UnixTime)})" };
        item.Click += (_, _) => MarkerDeleteRequested?.Invoke(hit);
        menu.Items.Add(item);
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
