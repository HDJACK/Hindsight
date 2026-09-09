using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Hindsight.Core.Analysis;
using Hindsight.Core.Model;

namespace Hindsight.App.Controls;

/// <summary>Drawing half of <see cref="TimelineControl"/>: lanes, anomaly bands, overlay, range band, axis, context strip and mini-map.</summary>
public sealed partial class TimelineControl
{
    private const double RangeHandleSize = 6;
    private const double AnomalyLabelMinWidth = 40;

    private static Brush Tint(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness, DashStyle? dash = null)
    {
        var pen = new Pen(brush, thickness);
        if (dash is not null) pen.DashStyle = dash;
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private static readonly Brush RangeBrush = Tint(0x30, 0x60, 0xA0, 0xFF);
    private static readonly Brush RangeEdgeBrush = Tint(0xFF, 0x60, 0xA0, 0xFF);
    private static readonly Pen RangeEdgePen = FrozenPen(RangeEdgeBrush, 1);

    private static readonly Brush LowSeverityBrush = Tint(0x40, 0xFF, 0xD7, 0x00);
    private static readonly Brush MediumSeverityBrush = Tint(0x60, 0xFF, 0x8C, 0x00);
    private static readonly Brush HighSeverityBrush = Tint(0x70, 0xFF, 0x30, 0x30);
    private static readonly Pen HighlightPen = FrozenPen(Brushes.White, 1.5);

    private static readonly Pen OverlayPen = FrozenPen(Brushes.White, 1, DashStyles.Dash);

    /// <summary>One frozen pen and glyph per <see cref="MarkerKind"/>, built once: the marker loop runs on every
    /// render, so nothing here may allocate a brush or a pen per frame.</summary>
    private static readonly (Pen Pen, Brush Brush, string Glyph)[] MarkerStyles = BuildMarkerStyles();

    private static (Pen, Brush, string)[] BuildMarkerStyles()
    {
        var kinds = Enum.GetValues<MarkerKind>();
        var styles = new (Pen, Brush, string)[kinds.Length];
        foreach (var kind in kinds)
        {
            var (brush, glyph) = MarkerStyle(kind);
            styles[(int)kind] = (FrozenPen(brush, 1), brush, glyph);
        }
        return styles;
    }

    /// <summary>Colour and glyph per marker kind: power gold, session grey, update green, Defender orange,
    /// scheduled task grey, USB purple, display slate, manual blue.</summary>
    private static (Brush Brush, string Glyph) MarkerStyle(MarkerKind kind) => kind switch
    {
        MarkerKind.AcConnected or MarkerKind.AcDisconnected => (Brushes.Gold, "⚡"),
        MarkerKind.Suspend or MarkerKind.Resume or MarkerKind.Lock or MarkerKind.Unlock => (Brushes.LightGray, "◐"),
        MarkerKind.WindowsUpdate => (Brushes.MediumSeaGreen, "↓"),
        MarkerKind.DefenderScan => (Brushes.Orange, "⛨"),
        MarkerKind.ScheduledTask => (Brushes.Gray, "⏱"),
        MarkerKind.UsbArrived or MarkerKind.UsbRemoved => (Brushes.MediumPurple, "⏏"),
        MarkerKind.DisplayOff => (Brushes.SlateGray, "☾"),
        MarkerKind.DisplayOn => (Brushes.SlateGray, "☀"),
        _ => (Brushes.DeepSkyBlue, "●"),
    };

    /// <summary>Falls back to the manual style for a kind a newer file might carry.</summary>
    private static (Pen Pen, Brush Brush, string Glyph) StyleOf(MarkerKind kind)
    {
        int i = (int)kind;
        return i >= 0 && i < MarkerStyles.Length ? MarkerStyles[i] : MarkerStyles[(int)MarkerKind.Manual];
    }

    private static readonly Brush MiniMapFillBrush = Tint(0x60, 0x80, 0x80, 0x80);
    private static readonly Brush MiniMapViewBrush = Tint(0x28, 0xFF, 0xFF, 0xFF);
    private static readonly Pen MiniMapViewPen = FrozenPen(Tint(0xB0, 0xFF, 0xFF, 0xFF), 1);

    /// <summary>Lane kinds <see cref="ProcessSeries.Value"/> actually provides a per-process figure for; other lanes have no per-process data and would otherwise draw a flat line pinned to the lane floor.</summary>
    private static readonly HashSet<LaneKind> OverlayLanes = new()
    {
        LaneKind.Cpu, LaneKind.Gpu, LaneKind.FileIo, LaneKind.DiskPhysical, LaneKind.Network, LaneKind.RamFree,
    };

    private static bool HasOverlayValue(LaneKind kind) => OverlayLanes.Contains(kind);

    private static Brush SeverityBrush(Severity severity) => severity switch
    {
        Severity.High => HighSeverityBrush,
        Severity.Medium => MediumSeverityBrush,
        _ => LowSeverityBrush,
    };

    // Theme brushes, resolved once and reused across frames instead of five resource lookups (plus a new Pen) per render.
    private Brush _bgBrush = Brushes.Black;
    private Brush _laneBgBrush = Brushes.DimGray;
    private Brush _textBrush = Brushes.White;
    private Brush _mutedBrush = Brushes.Gray;
    private Pen? _axisPen;

    /// <summary>Re-resolves the theme brushes on the first render and after a theme change. WPF-UI swaps the whole
    /// resource dictionary, so a new application background brush instance means the theme changed. The axis pen is
    /// built over a frozen clone of the stroke brush so freezing it cannot freeze the shared resource.</summary>
    private void EnsureThemeBrushes()
    {
        var bg = TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black;
        if (_axisPen is not null && ReferenceEquals(bg, _bgBrush)) return;

        _bgBrush = bg;
        _laneBgBrush = TryFindResource("CardBackgroundFillColorDefaultBrush") as Brush ?? Brushes.DimGray;
        _textBrush = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.White;
        _mutedBrush = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;

        var stroke = TryFindResource("ControlStrokeColorDefaultBrush") as Brush ?? Brushes.DimGray;
        if (!stroke.IsFrozen && stroke.CanFreeze) { stroke = stroke.Clone(); stroke.Freeze(); }
        _axisPen = FrozenPen(stroke, 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsureThemeBrushes();
        Brush bg = _bgBrush, laneBg = _laneBgBrush, text = _textBrush, muted = _mutedBrush;
        var axisPen = _axisPen!;

        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_ticks.Count == 0)
        {
            DrawText(dc, "No data yet. Recording is running.", LabelWidth + 10, 10, 14, text);
            return;
        }

        double stripHeight = StripHeight;
        double lanesHeight = LanesHeight;
        double lh = LaneHeight;
        for (int lane = 0; lane < _lanes.Count; lane++)
        {
            var rect = new Rect(LabelWidth, lane * lh, PlotWidth, lh - 2);
            dc.DrawRectangle(laneBg, null, rect);
            DrawText(dc, _lanes[lane].Label, 6, lane * lh + 4, 12, text);
        }

        int first = VisibleFirst, last = VisibleLast;
        if (first > last) return;   // the view sits over empty slots of a partially filled buffer
        // One extra tick on each side keeps lane lines and context runs continuous across the edges (ClipToBounds trims them).
        int renderFirst = Math.Max(0, first - 1), renderLast = Math.Min(_ticks.Count - 1, last + 1);

        // The padded render range can draw one column into the lane-label gutter (XOf(renderFirst) can land
        // left of LabelWidth); ClipToBounds only trims to the control, not the plot rect, so clip explicitly.
        dc.PushClip(new RectangleGeometry(new Rect(LabelWidth, 0, PlotWidth, ActualHeight)));

        double colW = PlotWidth / ViewSlots;
        for (int i = renderFirst; i <= renderLast; i++)
        {
            var t = _ticks[i];
            if (t.Has(TickFlags.Gap)) dc.DrawRectangle(GapBrush, null, new Rect(XOf(i) - colW / 2, 0, colW, lanesHeight));
            else if (t.Has(TickFlags.Spike)) dc.DrawRectangle(SpikeBrush, null, new Rect(XOf(i) - colW / 2, 0, colW, lanesHeight));
        }

        DrawAnomalies(dc, lh, text);

        // Rate lanes (File I/O, Disk phys, Network) hold per-tick byte totals; the label is normalized
        // to per-second using the tick interval so it reads as a real throughput rate.
        int interval = Math.Max(1, (int)_ticks[^1].IntervalSeconds);
        var maxes = new double[_lanes.Count];
        for (int lane = 0; lane < _lanes.Count; lane++)
        {
            var spec = _lanes[lane];
            double max = spec.FixedMax100 ? 100 : AutoMax(spec, first, last);
            maxes[lane] = max;
            double labelMax = RateLanes.Contains(spec.Kind) ? max / interval : max;
            DrawLane(dc, lane, spec, max, labelMax, muted, lh, renderFirst, renderLast);
        }

        DrawOverlay(dc, renderFirst, renderLast, lh, maxes, text);

        double stripTop = lanesHeight;
        if (ContextVisible) DrawContextStrip(dc, stripTop, text, stripHeight, renderFirst, renderLast);

        DrawAxis(dc, axisPen, text, lanesHeight, colW, interval, first, last);

        if (ShowMiniMap) DrawMiniMap(dc, axisPen);

        foreach (var m in _markers)
        {
            int idx = IndexOf(m.UnixTime);
            if (idx < first || idx > last) continue;
            double x = XOf(idx);
            var style = StyleOf(m.Kind);
            dc.DrawLine(style.Pen, new Point(x, 0), new Point(x, lanesHeight));
            DrawText(dc, style.Glyph + " " + m.Text, x + 3, 2, 10, style.Brush);
        }

        DrawRange(dc, lanesHeight);

        if (SelectedUnixTime.HasValue)
        {
            int selIdx = IndexOf(SelectedUnixTime.Value);
            if (selIdx >= first && selIdx <= last)
            {
                double x = XOf(selIdx);
                dc.DrawLine(SelectionPen, new Point(x, 0), new Point(x, lanesHeight));
            }
        }

        dc.Pop();
    }

    /// <summary>Left/right pixel edges of a unix range's band (a whole column wide at each end), or false when the range lies outside the recorded ticks.</summary>
    private bool RangeBand(TimeRange range, out double x0, out double x1)
    {
        x0 = x1 = 0;
        if (_ticks.Count == 0 || range.ToUnix < _ticks[0].UnixTime || range.FromUnix > _ticks[^1].UnixTime) return false;
        double colW = PlotWidth / ViewSlots;
        x0 = XOf(NearestIndex(range.FromUnix)) - colW / 2;
        x1 = XOf(NearestIndex(range.ToUnix)) + colW / 2;
        return true;
    }

    /// <summary>Index of the lane drawing <paramref name="kind"/>, or -1 when it is not shown.</summary>
    private int LaneIndexOf(LaneKind kind)
    {
        for (int i = 0; i < _lanes.Count; i++)
            if (_lanes[i].Kind == kind) return i;
        return -1;
    }

    private void DrawAnomalies(DrawingContext dc, double lh, Brush text)
    {
        foreach (var a in _anomalies)
        {
            if (MetricInfo.Lane(a.Metric) is not { } kind) continue;
            int lane = LaneIndexOf(kind);
            if (lane < 0 || !RangeBand(a.Range, out double x0, out double x1)) continue;
            if (x1 < LabelWidth || x0 > ActualWidth) continue;
            var rect = new Rect(x0, lane * lh, Math.Max(1, x1 - x0), Math.Max(1, lh - 2));
            bool highlighted = a.Equals(_highlighted);
            // HighlightPen is 1.5 px; inset by half so the stroke does not bleed into the neighbouring lane.
            var strokeRect = highlighted ? Rect.Inflate(rect, -0.75, -0.75) : rect;
            dc.DrawRectangle(SeverityBrush(a.Severity), null, rect);
            if (highlighted) dc.DrawRectangle(null, HighlightPen, strokeRect);
            if (rect.Width >= AnomalyLabelMinWidth) DrawText(dc, a.Severity.ToString(), rect.X + 3, rect.Y + 2, 9, text);
        }
    }

    private void DrawRange(DrawingContext dc, double lanesHeight)
    {
        if (_range is not { } range || !RangeBand(range, out double x0, out double x1)) return;
        dc.DrawRectangle(RangeBrush, null, new Rect(x0, 0, Math.Max(1, x1 - x0), lanesHeight));
        dc.DrawLine(RangeEdgePen, new Point(x0, 0), new Point(x0, lanesHeight));
        dc.DrawLine(RangeEdgePen, new Point(x1, 0), new Point(x1, lanesHeight));
        dc.DrawRectangle(RangeEdgeBrush, null, new Rect(x0 - RangeHandleSize / 2, 0, RangeHandleSize, RangeHandleSize));
        dc.DrawRectangle(RangeEdgeBrush, null, new Rect(x1 - RangeHandleSize / 2, 0, RangeHandleSize, RangeHandleSize));
    }

    /// <summary>Dashed white curve of one process per lane, scaled like the lane itself (RAM free is scaled to the process' peak working set instead).</summary>
    private void DrawOverlay(DrawingContext dc, int renderFirst, int renderLast, double lh, double[] laneMax, Brush text)
    {
        // The series is aligned index-for-index with the ticks passed to SetData. On a live source the tick list is
        // replaced every second, so an equal count is not enough: compare the extraction range too and drop a stale
        // overlay rather than drawing it against the wrong times.
        if (_overlay is not { } series || _ticks.Count == 0 || series.Points.Count != _ticks.Count) return;
        if (series.SourceFirstUnix != _ticks[0].UnixTime || series.SourceLastUnix != _ticks[^1].UnixTime) return;

        // Draw the header once at the top of lane 0, regardless of lane order.
        double headerTop = 2;
        DrawTextRight(dc, $"{series.Identity.Name} ({series.Identity.Pid})", ActualWidth - 4, headerTop, 10, text);

        for (int lane = 0; lane < _lanes.Count; lane++)
        {
            var kind = _lanes[lane].Kind;
            if (!HasOverlayValue(kind)) continue;
            double top = lane * lh + 2, h = lh - 6;
            double max = kind == LaneKind.RamFree ? Math.Max(1, series.PeakWorkingSet) : Math.Max(1, laneMax[lane]);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool open = false;
                for (int i = renderFirst; i <= renderLast; i++)
                {
                    if (series.Points[i] is null) { open = false; continue; }
                    var p = new Point(XOf(i), top + h - Math.Clamp(series.Value(i, kind) / max, 0, 1) * h);
                    if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                    else ctx.LineTo(p, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, OverlayPen, geo);
            if (kind == LaneKind.RamFree)
                DrawTextRight(dc, "ws max " + Format.Bytes(series.PeakWorkingSet), ActualWidth - 4, top + h - 12, 10, text);
        }
    }

    /// <summary>Centre x of a tick's column in mini-map space, which spans all <see cref="Slots"/> rather than the view.</summary>
    private double MiniX(int index) => LabelWidth + (SlotOffset + index + 0.5) / Slots * PlotWidth;

    /// <summary>Inverse of <see cref="MiniX"/>: the slot under a control-space x coordinate in mini-map space.</summary>
    private int MiniSlotAtX(double x) => (int)Math.Floor((x - LabelWidth) / PlotWidth * Slots);

    private void DrawMiniMap(DrawingContext dc, Pen axisPen)
    {
        var rect = MiniMapRect;
        double bottom = rect.Bottom - 1, h = Math.Max(1, rect.Height - 2);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(MiniX(0), bottom), true, true);
            for (int i = 0; i < _ticks.Count; i++)
            {
                double v = _ticks[i].Has(TickFlags.Gap) ? 0 : Math.Clamp(_ticks[i].CpuTotalPercent / 100.0, 0, 1);
                ctx.LineTo(new Point(MiniX(i), bottom - v * h), true, false);
            }
            ctx.LineTo(new Point(MiniX(_ticks.Count - 1), bottom), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(MiniMapFillBrush, null, geo);

        double vx0 = rect.X + (double)ViewFrom / Slots * rect.Width;
        double vx1 = rect.X + (double)(ViewFrom + ViewSlots) / Slots * rect.Width;
        dc.DrawRectangle(MiniMapViewBrush, MiniMapViewPen, new Rect(vx0, rect.Y, Math.Max(1, vx1 - vx0), rect.Height));
        dc.DrawLine(axisPen, new Point(rect.X, rect.Y), new Point(rect.Right, rect.Y));
    }

    private static readonly long[] AxisSteps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };

    /// <summary>Smallest ladder step whose gridline spacing (step / secondsPerSlot columns wide) reaches the minimum label spacing.</summary>
    private static long AxisStep(int secondsPerSlot, double colW)
    {
        foreach (long step in AxisSteps)
            if (step / (double)secondsPerSlot * colW >= MinAxisLabelSpacingPx) return step;
        return AxisSteps[^1];
    }

    private void DrawAxis(DrawingContext dc, Pen axisPen, Brush text, double lanesHeight, double colW, int interval, int first, int last)
    {
        long step = AxisStep(interval, colW);
        long firstTs = _ticks[first].UnixTime, lastTs = _ticks[last].UnixTime;
        long origin = _timeOrigin ?? 0;   // relative mode aligns gridlines to the origin, absolute mode to the epoch
        long k = (long)Math.Ceiling((firstTs - origin) / (double)step);
        int lastIdx = -1;
        for (long ts = origin + k * step; ts <= lastTs; ts += step)
        {
            int idx = NearestIndex(ts);
            if (idx < first || idx > last || idx == lastIdx) continue;   // a step finer than the tick interval can repeat a tick
            lastIdx = idx;
            long tickTs = _ticks[idx].UnixTime;
            double x = XOf(idx);
            dc.DrawLine(axisPen, new Point(x, 0), new Point(x, lanesHeight));
            string label = _timeOrigin.HasValue
                ? "+" + Format.Duration(tickTs - _timeOrigin.Value)
                : step >= 60 ? Format.TimeShort(tickTs) : Format.Time(tickTs);
            DrawTextCentered(dc, label, x, AxisTop + 4, 11, text);
        }
    }

    private void DrawContextStrip(DrawingContext dc, double top, Brush text, double stripHeight, int renderFirst, int renderLast)
    {
        double colW = PlotWidth / ViewSlots;
        int i = renderFirst;
        while (i <= renderLast)
        {
            int pid = _ticks[i].ForegroundPid;
            int j = i;
            while (j + 1 <= renderLast && _ticks[j + 1].ForegroundPid == pid) j++;
            double x0 = XOf(i) - colW / 2, x1 = XOf(j) + colW / 2;
            var rect = new Rect(x0, top, Math.Max(0, x1 - x0), stripHeight);
            var brush = pid == 0 ? Brushes.Transparent : ContextPalette[(uint)pid % (uint)ContextPalette.Length];
            dc.DrawRectangle(brush, null, rect);
            if (pid != 0 && rect.Width >= 60)
            {
                string? name = _nameForPid?.Invoke(pid);
                if (!string.IsNullOrEmpty(name))
                {
                    dc.PushClip(new RectangleGeometry(rect));
                    DrawText(dc, name!, Math.Max(x0, LabelWidth) + 2, top + 1, 10, text);
                    dc.Pop();
                }
            }
            i = j + 1;
        }

        for (int k = renderFirst; k <= renderLast; k++)
        {
            if (_ticks[k].Has(TickFlags.UserIdle))
                dc.DrawRectangle(IdleBrush, null, new Rect(XOf(k) - colW / 2, top, colW, stripHeight));
        }
    }

    private double AutoMax(LaneSpec spec, int first, int last)
    {
        double max = 1;
        for (int i = first; i <= last; i++)
            if (!_ticks[i].Has(TickFlags.Gap)) max = Math.Max(max, LaneValue(spec, i));
        return max;
    }

    private void DrawLane(DrawingContext dc, int lane, LaneSpec spec, double max, double labelMax, Brush muted, double lh, int renderFirst, int renderLast)
    {
        double top = lane * lh + 2, h = lh - 6;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool open = false;
            for (int i = renderFirst; i <= renderLast; i++)
            {
                var t = _ticks[i];
                if (t.Has(TickFlags.Gap)) { open = false; continue; }
                var p = new Point(XOf(i), top + h - Math.Clamp(LaneValue(spec, i) / max, 0, 1) * h);
                if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, spec.Pen, geo);
        DrawText(dc, spec.FormatMax(labelMax), LabelWidth + 4, top, 10, muted);
    }

    private FormattedText MakeText(string text, double size, Brush? brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush ?? Brushes.Gainsboro, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush? brush = null) =>
        dc.DrawText(MakeText(text, size, brush), new Point(x, y));

    private void DrawTextCentered(DrawingContext dc, string text, double centerX, double y, double size, Brush? brush = null)
    {
        var ft = MakeText(text, size, brush);
        dc.DrawText(ft, new Point(centerX - ft.Width / 2, y));
    }

    private void DrawTextRight(DrawingContext dc, string text, double right, double y, double size, Brush? brush = null)
    {
        var ft = MakeText(text, size, brush);
        dc.DrawText(ft, new Point(right - ft.Width, y));
    }
}
