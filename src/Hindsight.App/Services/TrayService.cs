using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Wpf.Ui.Tray;

namespace Hindsight.App.Services;

/// <summary>Tray icon without a parent window: the window is hidden on close, and NotifyIconService
/// would dispose itself on the parent's Closing event.</summary>
public sealed class TrayService : NotifyIconService
{
    private readonly Action _open;
    private readonly MenuItem _startItem;
    private readonly MenuItem _stopItem;

    public TrayService(Action open, Action addMarker, Action startRecording, Action stopRecording, Action exit)
    {
        _open = open;
        // INotifyIconService.Icon is typed BitmapFrame (Wpf.Ui.Tray.xml), not BitmapImage/ImageSource,
        // so the icon has to be decoded through BitmapFrame.Create rather than constructed directly.
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/hindsight.ico"));
        TooltipText = "Hindsight";
        _startItem = Item("Start recording…", startRecording);
        _stopItem = Item("Stop recording", stopRecording);
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open", open));
        menu.Items.Add(Item("Add marker", addMarker));
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", exit));
        ContextMenu = menu;
        SetRecording(false);
    }

    public void SetRecording(bool recording)
    {
        _startItem.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        _stopItem.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    protected override void OnLeftClick() => _open();
    protected override void OnLeftDoubleClick() => _open();
}
