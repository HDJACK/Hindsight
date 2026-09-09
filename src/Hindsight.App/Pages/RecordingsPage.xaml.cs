using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hindsight.Core.Recordings;
using Wpf.Ui.Controls;

namespace Hindsight.App.Pages;

public sealed record RecordingRow(string Path, string Name, string Started, string Duration, string Size, string Notes, bool Readable, string? Error);

public partial class RecordingsPage : Page
{
    public RecordingsPage()
    {
        InitializeComponent();
        AppHost.Services.Recordings.Changed += () => Dispatcher.BeginInvoke(() => { Refresh(); UpdateRecordingUi(); });
        AppHost.Services.TickWritten += _ => Dispatcher.BeginInvoke(UpdateRecordingStatus);
        AppHost.Services.PipelineRestarted += () => Dispatcher.BeginInvoke(UpdateRecordingUi);
        AppHost.Settings.Changed += _ => Dispatcher.BeginInvoke(UpdateRecordingUi);
        Loaded += (_, _) => { Refresh(); UpdateRecordingUi(); };
    }

    private void UpdateRecordingUi()
    {
        var r = AppHost.Services.Recordings;
        RecordButton.Content = r.IsRecording ? "Stop recording" : "Start recording";
        RecordButton.Icon = new SymbolIcon(r.IsRecording ? SymbolRegular.Stop24 : SymbolRegular.Play24);
        RecordButton.Appearance = r.IsRecording ? ControlAppearance.Danger : ControlAppearance.Primary;
        RecordingName.IsEnabled = !r.IsRecording;
        RecordingNotes.IsEnabled = !r.IsRecording;
        SaveBufferButton.Content = $"Save last {AppHost.Services.Current.BufferMinutes} min";
        UpdateRecordingStatus();
    }

    private void UpdateRecordingStatus()
    {
        var r = AppHost.Services.Recordings;
        if (!r.IsRecording || r.StartedAt is null) { RecStatus.Text = "Not recording"; return; }
        long secs = (long)(DateTimeOffset.Now - r.StartedAt.Value).TotalSeconds;
        RecStatus.Text = $"● REC {r.CurrentName} · {Format.Duration(secs)} · {r.TickCount} ticks";
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        var r = AppHost.Services.Recordings;
        try
        {
            if (r.IsRecording)
            {
                string path = r.Stop();
                Show("Recording saved", Path.GetFileName(path) + " — opening it in Analysis.", InfoBarSeverity.Success);
                AppHost.Window?.OpenRecording(path);
            }
            else
            {
                r.Start(RecordingName.Text, RecordingNotes.Text.Trim());
                RecordingName.Text = ""; RecordingNotes.Text = "";
                Info.IsOpen = false;
            }
        }
        catch (Exception ex) { Show("Recording failed", ex.Message, InfoBarSeverity.Error); }
    }

    private void SaveBuffer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = AppHost.Services.Recordings.SaveBuffer(RecordingName.Text, RecordingNotes.Text.Trim());
            RecordingName.Text = ""; RecordingNotes.Text = "";
            Show("Snapshot saved", Path.GetFileName(path) + " — opening it in Analysis.", InfoBarSeverity.Success);
            AppHost.Window?.OpenRecording(path);
        }
        catch (Exception ex) { Show("Snapshot failed", ex.Message, InfoBarSeverity.Error); }
    }

    private RecordingCatalog Catalog => new(AppHost.Services.Recordings.Folder);

    private void Refresh()
    {
        var rows = Catalog.List().Select(e => new RecordingRow(
            e.Path,
            e.DisplayName,
            e.Meta is null ? "" : DateTimeOffset.FromUnixTimeSeconds(e.Meta.StartUnix).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            e.Meta is null ? "" : Format.Duration(e.Meta.DurationSeconds),
            Format.Bytes(e.SizeBytes),
            e.Meta?.Notes ?? (e.Error ?? ""),
            e.IsReadable, e.Error)).ToList();
        RecordingsGrid.ItemsSource = rows;
        FolderText.Text = Catalog.Folder;
        UpdateButtons();
    }

    private IReadOnlyList<RecordingRow> Selected => RecordingsGrid.SelectedItems.Cast<RecordingRow>().ToList();

    private void UpdateButtons()
    {
        var sel = Selected;
        bool one = sel.Count == 1 && sel[0].Readable;
        OpenButton.IsEnabled = one; RenameButton.IsEnabled = one;
        ExportTicksButton.IsEnabled = one; ExportProcessesButton.IsEnabled = one; ExportJsonButton.IsEnabled = one;
        DeleteButton.IsEnabled = sel.Count >= 1;
        CompareButton.IsEnabled = sel.Count == 2 && sel.All(s => s.Readable);
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (Selected.Count == 1 && Selected[0].Readable) Open_Click(sender, e); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.Count != 1) return;
        AppHost.Window?.OpenRecording(Selected[0].Path);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.Count != 1) return;
        string name = RenameBox.Text.Trim();
        if (name.Length == 0) { Show("Enter a new name first", "", InfoBarSeverity.Warning); return; }
        Try(() => { Catalog.Rename(Selected[0].Path, name); RenameBox.Text = ""; Refresh(); }, "Rename failed");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected; if (sel.Count == 0) return;
        var result = System.Windows.MessageBox.Show($"Delete {sel.Count} recording(s)? This cannot be undone.", "Hindsight",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.OK) return;
        Try(() => { foreach (var s in sel) Catalog.Delete(s.Path); Refresh(); }, "Delete failed");
    }

    private void ExportTicks_Click(object sender, RoutedEventArgs e) => Export("CSV files|*.csv", "_ticks.csv", (r, path) => { using var w = new StreamWriter(path); RecordingExporter.TicksCsv(r, w); });
    private void ExportProcesses_Click(object sender, RoutedEventArgs e) => Export("CSV files|*.csv", "_processes.csv", (r, path) => { using var w = new StreamWriter(path); RecordingExporter.ProcessesCsv(r, w); });
    private void ExportJson_Click(object sender, RoutedEventArgs e) => Export("JSON files|*.json", ".json", (r, path) => { using var s = File.Create(path); RecordingExporter.Json(r, s); });

    private void Export(string filter, string suffix, Action<Recording, string> write)
    {
        if (Selected.Count != 1) return;
        var row = Selected[0];
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = Path.GetFileNameWithoutExtension(row.Path) + suffix };
        if (dlg.ShowDialog() != true) return;
        Try(() => { var rec = RecordingReader.Open(row.Path); write(rec, dlg.FileName); Show("Exported", dlg.FileName, InfoBarSeverity.Success); }, "Export failed");
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected; if (sel.Count != 2) return;
        AppHost.Window?.OpenCompare(sel[0].Path, sel[1].Path);
    }

    private void Try(Action action, string errorTitle)
    {
        try { action(); }
        catch (Exception ex) { Show(errorTitle, ex.Message, InfoBarSeverity.Error); }
    }

    private void Show(string title, string text, InfoBarSeverity severity)
    {
        Info.Title = title; Info.Message = text; Info.Severity = severity; Info.IsOpen = true;
    }
}
