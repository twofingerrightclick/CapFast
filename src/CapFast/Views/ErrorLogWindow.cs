using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CapFast.Core;

namespace CapFast.Views;

public sealed class ErrorLogWindow : Window
{
    private readonly ErrorLog _log;
    private readonly StackPanel _entries = new() { Spacing = 16 };
    public ErrorLogWindow(ErrorLog log)
    {
        _log = log;
        Title = "CapFast — Errors";
        Width = 640; Height = 440; MinWidth = 400; MinHeight = 240;
        var clear = new Button { Content = "Clear log", HorizontalAlignment = HorizontalAlignment.Right };
        clear.Click += (_, _) => log.Clear();
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(new TextBlock { Text = "Errors", FontSize = 26, FontWeight = FontWeight.SemiBold });
        var scroll = new ScrollViewer { Content = _entries, Margin = new Thickness(0, 20) };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(new TextBlock { Text = "Keeps the most recent 100 errors.", Opacity = .65, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(clear, 1); footer.Children.Add(clear);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
        log.Changed += Refresh;
        Closed += (_, _) => log.Changed -= Refresh;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Refresh();
    }
    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Refresh); return; }
        _entries.Children.Clear();
        if (_log.Entries.Count == 0) _entries.Children.Add(new TextBlock { Text = "No errors recorded.", Opacity = .7 });
        foreach (var error in _log.Entries.Reverse()) _entries.Children.Add(new StackPanel { Spacing = 4, Children =
        {
            new TextBlock { Text = error.Time.ToLocalTime().ToString("MMM d, HH:mm:ss"), FontSize = 12, Opacity = .6 },
            new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap }
        } });
    }
}
