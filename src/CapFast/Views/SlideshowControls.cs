using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CapFast.Core;

namespace CapFast.Views;

public sealed class SlideshowControls : StackPanel, IDisposable
{
    private readonly Slideshow _slideshow;
    private readonly SlideSurface _preview = new() { Height = 150 };
    private readonly TextBlock _empty = new() { Text = "No slideshow loaded", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _choose = new() { Content = "Choose PowerPoint…" };
    private readonly Button _previous = new() { Content = "← Previous" };
    private readonly Button _next = new() { Content = "Next →" };
    private readonly Button _selectPreview = new() { Padding = new Thickness(3), BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private bool _loading;
    private bool _disposed;

    public SlideshowControls(Slideshow slideshow)
    {
        _slideshow = slideshow;
        Spacing = 10;
        var drop = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
                Children = { _choose, new TextBlock { Text = "or drop a PowerPoint (.pptx) here", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap } } } };
        DragDrop.SetAllowDrop(drop, true);
        drop.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var files = e.DataTransfer.TryGetFiles()?.ToArray();
            e.DragEffects = !_loading && files?.Length == 1 && IsPowerPoint(files[0].Name) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        drop.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            var files = e.DataTransfer.TryGetFiles()?.ToArray();
            if (files?.Length != 1 || files[0].TryGetLocalPath() is not { } path)
            { _message.Text = "Drop one local PowerPoint (.pptx) file."; return; }
            await LoadAsync(path);
        });
        _choose.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            try
            {
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title = "Choose text slideshow", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("PowerPoint") { Patterns = ["*.pptx"] }] });
                if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await LoadAsync(path);
            }
            catch (Exception) { if (!_disposed) _message.Text = "Could not open the file picker. Drag a PowerPoint into the drop area instead."; }
        };
        _previous.Click += (_, _) => slideshow.Move(-1);
        _next.Click += (_, _) => slideshow.Move(1);
        _selectPreview.Width = 275;
        _selectPreview.HorizontalAlignment = HorizontalAlignment.Left;
        _selectPreview.Content = new Grid { Width = 267, Height = 150, Children = { _preview, _empty } };
        ToolTip.SetTip(_selectPreview, "Click to show slideshow · Enter or Space when focused");
        _selectPreview.Click += (_, _) => slideshow.SetShowing(true);
        Avalonia.Automation.AutomationProperties.SetName(_selectPreview, "Show slideshow on audience screen");
        Children.Add(new TextBlock { Text = "Slideshow", FontWeight = FontWeight.Medium });
        Children.Add(_selectPreview);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { _previous, _position, _next, _name } });
        Children.Add(drop);
        Children.Add(_message);
        Children.Add(new TextBlock { FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap,
            Text = "Text only: paragraph alignment is preserved in a clean layout. Images, animations and other PowerPoint styling are omitted. Captions keep running while slides are shown." });
        slideshow.Changed += Refresh;
        Refresh();
    }

    private static bool IsPowerPoint(string path) => string.Equals(Path.GetExtension(path), ".pptx", StringComparison.OrdinalIgnoreCase);
    private async Task LoadAsync(string path)
    {
        if (_loading || _disposed) return;
        _loading = true;
        _message.Text = "Loading slideshow…";
        Refresh();
        try
        {
            var slides = await Task.Run(() => PowerPointTextReader.Read(path));
            if (_disposed) return;
            _slideshow.Load(Path.GetFileName(path), slides);
            _message.Text = "Ready. Select the slideshow preview to show it to the audience.";
        }
        catch (Exception error)
        {
            if (!_disposed) _message.Text = error is InvalidDataException ? error.Message : "Could not read this PowerPoint. Check that it opens normally and is not password-protected.";
        }
        finally { _loading = false; if (!_disposed) Refresh(); }
    }

    private void Refresh()
    {
        _choose.IsEnabled = !_loading;
        _name.Text = _slideshow.Count == 0 ? "No slideshow loaded" : _slideshow.Name;
        _preview.Show(_slideshow.Current);
        _empty.IsVisible = _slideshow.Count == 0;
        _position.Text = _slideshow.Count == 0 ? "0 / 0" : $"{_slideshow.Index + 1} / {_slideshow.Count}";
        _previous.IsEnabled = _slideshow.Index > 0;
        _next.IsEnabled = _slideshow.Index + 1 < _slideshow.Count;
        _selectPreview.IsEnabled = _slideshow.Count > 0;
        SetOutputState(_selectPreview, "Slideshow", _slideshow.IsShowing);
    }

    internal static void SetOutputState(Button button, string label, bool active)
    {
        IBrush outline = active ? new SolidColorBrush(Color.Parse("#28A745")) : Brushes.Transparent;
        button.BorderBrush = outline;
        // Keep the selected outline visible even while hovering or pressing the button.
        button.Resources["ButtonBorderBrushPointerOver"] = outline;
        button.Resources["ButtonBorderBrushPressed"] = outline;
        Avalonia.Automation.AutomationProperties.SetHelpText(button,
            active ? $"{label} is showing on the audience screen." : $"Show {label.ToLowerInvariant()} on the audience screen.");
    }
    public void Dispose() { _disposed = true; _slideshow.Changed -= Refresh; }
}
