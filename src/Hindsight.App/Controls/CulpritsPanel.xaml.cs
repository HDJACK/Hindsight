using System.Windows;
using System.Windows.Controls;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.App.Controls;

public partial class CulpritsPanel : UserControl
{
    private Tick? _lastTick;
    private IIdentitySource? _lastSource;
    private bool _suppressSelection;

    public CulpritsPanel()
    {
        InitializeComponent();
        ApplyDetailLevel(AppHost.DetailLevel);
        AppHost.DetailLevelChanged += () => ApplyDetailLevel(AppHost.DetailLevel);
    }

    /// <summary>Toggles the Professional-only columns; Easy keeps Process, CPU, GPU, File I/O, Memory and Note.</summary>
    public void ApplyDetailLevel(DetailLevel level)
    {
        var v = level == DetailLevel.Professional ? Visibility.Visible : Visibility.Collapsed;
        ColNetwork.Visibility = v;
        ColDiskPhysical.Visibility = v;
        ColPrivate.Visibility = v;
        ColHandles.Visibility = v;
        ColThreads.Visibility = v;
        ColParentChain.Visibility = v;
        ColCommandLine.Visibility = v;
        if (_lastSource is not null) Show(_lastTick, _lastSource);
    }

    /// <summary>Raised when the user picks a row, or clears the selection (null). Not raised while
    /// <see cref="Show"/> rebuilds the rows.</summary>
    public event Action<int?>? ProcessSelected;

    /// <summary>The identity behind the selected row, or the top row when nothing is selected; null when empty.</summary>
    public int? SelectedIdentityId =>
        Rows.SelectedItem is CulpritRow selected ? selected.IdentityId
        : Rows.Items.Count > 0 && Rows.Items[0] is CulpritRow first ? first.IdentityId
        : null;

    public void Show(Tick? tick, IIdentitySource source)
    {
        _lastTick = tick;
        _lastSource = source;
        if (tick is null)
        {
            SetRows(null);
            Header.Text = "Click a second in the timeline to see what was running.";
            return;
        }
        var flags = new List<string>();
        if (tick.Has(TickFlags.Spike)) flags.Add("Spike");
        if (tick.Has(TickFlags.Gap)) flags.Add("Gap (standby or app not running)");
        if (!tick.Has(TickFlags.EtwActive)) flags.Add("ETW inactive");
        if (tick.LostEvents > 0) flags.Add($"{tick.LostEvents} ETW events lost");
        bool easy = AppHost.DetailLevel != DetailLevel.Professional;
        Header.Text = $"{Format.Time(tick.UnixTime)}  ·  CPU {Format.Percent(tick.CpuTotalPercent)}  ·  File I/O {Format.Rate(tick.DiskBytes, tick.IntervalSeconds)}"
                      + (easy ? "" : $"  ·  Network {Format.Rate(tick.NetBytes, tick.IntervalSeconds)}")
                      + (flags.Count > 0 ? "  ·  " + string.Join(", ", flags) : "");

        int rowCount = AppHost.DetailLevel == DetailLevel.Professional ? 5 : 3;
        var rows = new List<CulpritRow>();
        foreach (var s in tick.Samples.Take(rowCount))
        {
            var id = source.Lookup(s.IdentityId) ?? ProcessIdentity.Placeholder(s.Pid, 0);
            var note = new List<string>();
            if ((s.Flags & SampleFlags.ShortLived) != 0) note.Add("short-lived");
            if ((s.Flags & SampleFlags.Estimated) != 0) note.Add("estimated");
            rows.Add(new CulpritRow(
                Process: $"{id.DisplayName} ({s.Pid})",
                Cpu: Format.Percent(s.CpuPercent),
                Gpu: Format.Percent(s.GpuPercent),
                FileIo: Format.Bytes(s.ReadBytes + s.WriteBytes),
                Network: Format.Bytes(s.NetBytes),
                DiskPhysical: Format.Bytes(s.DiskReadBytes + s.DiskWriteBytes),
                Memory: Format.Bytes(s.WorkingSet),
                Private: Format.Bytes(s.PrivateBytes),
                Handles: s.HandleCount.ToString(),
                Threads: s.ThreadCount.ToString(),
                ParentChain: ParentChain.Describe(id, source),
                Note: string.Join(", ", note),
                CommandLine: string.IsNullOrEmpty(id.CommandLine) ? id.Path : id.CommandLine,
                Description: Describe(id),
                IdentityId: s.IdentityId));
        }
        SetRows(rows);
    }

    /// <summary>"{Description} · {Company}" with the empty halves dropped; "" when neither is known.</summary>
    private static string Describe(ProcessIdentity id)
    {
        bool hasDescription = !string.IsNullOrWhiteSpace(id.Description);
        bool hasCompany = !string.IsNullOrWhiteSpace(id.Company);
        if (hasDescription && hasCompany) return id.Description + "  ·  " + id.Company;
        return hasDescription ? id.Description : hasCompany ? id.Company : "";
    }

    /// <summary>Replaces the rows without raising <see cref="ProcessSelected"/>: assigning ItemsSource clears the
    /// selection, which would otherwise report "nothing selected" on every rebuild.</summary>
    private void SetRows(IReadOnlyList<CulpritRow>? rows)
    {
        _suppressSelection = true;
        try { Rows.ItemsSource = rows; }
        finally { _suppressSelection = false; }
    }

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        ProcessSelected?.Invoke((Rows.SelectedItem as CulpritRow)?.IdentityId);
    }
}
