using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CapFast.Core;
using CapFast.Speech;

namespace CapFast.Views;

public sealed class CaptionWindow : Window
{
    private readonly CaptionController _controller;
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 48,
        FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.NoWrap, MaxLines = 3,
        TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly CaptionLineLayout _captionLayout = new();
    private readonly Canvas _captionCanvas = new() { ClipToBounds = true };
    private readonly Queue<string[]> _captionFrames = new();
    private readonly List<TextBlock> _rowLabels = new();
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly System.Diagnostics.Stopwatch _scrollClock = new();
    private string[] _visibleLines = Array.Empty<string>();
    private string[]? _scrollTarget;
    private string _geometry = "";
    private int _captionRows = 3;
    private readonly Grid _stage;
    private readonly Border _band;
    private readonly StackPanel _pausedControls;
    private readonly ComboBox _audio = new() { Width = 280, PlaceholderText = "Audio input" };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, FontSize = 18, MaxWidth = 540 };
    private readonly StackPanel _configuration;
    private readonly TextBlock _status = new() { FontSize = 13, Opacity = .7 };
    private readonly Control _pauseIcon;
    private bool _spaceDown;
    private bool _updatingAudio;
    private bool _listingAudio;
    private bool _allowClose;
    private bool _closing;
    private bool _wasPaused;
    public Action? OpenSettings { get; set; }
    public Action? CloseSecondaryWindows { get; set; }

    public CaptionWindow(CaptionController controller)
    {
        _controller = controller;
        _audio.ItemTemplate = new FuncDataTemplate<AudioInput>((input, _) => new TextBlock { Text = input?.Name });
        Title = "CapFast";
        Focusable = true;
        Background = Brushes.Black;
        Width = 1280; Height = 720; MinWidth = 640; MinHeight = 360;
        _band = new Border { Child = _captionCanvas, VerticalAlignment = VerticalAlignment.Bottom, ClipToBounds = true };
        _scrollTimer.Tick += (_, _) => AnimateScroll();
        var settings = new Button { Content = "Settings…" };
        settings.Click += (_, _) => OpenSettings?.Invoke();
        var openSettings = new Button { Content = "Open settings", HorizontalAlignment = HorizontalAlignment.Center };
        openSettings.Click += (_, _) => OpenSettings?.Invoke();
        _configuration = new StackPanel { Spacing = 18, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(28),
            Children = { _message, openSettings } };
        var refresh = new Button { Content = "Refresh", FontSize = 12 };
        refresh.Click += async (_, _) => await RefreshAudioAsync();
        _pausedControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { _audio, refresh, settings } };
        _audio.ItemsSource = new[] { new AudioInput("", "System default") };
        _audio.SelectedIndex = 0;
        _audio.DropDownClosed += (_, _) => Focus();
        _audio.SelectionChanged += (_, _) =>
        {
            if (_updatingAudio || !_controller.IsPaused) return;
            if (_audio.SelectedItem is AudioInput input)
            {
                _controller.Settings.AudioDeviceId = input.Id;
                _controller.SettingsChanged();
                if (!_audio.IsDropDownOpen) Focus();
            }
        };
        _pauseIcon = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center, Children =
            {
                new Border { Width = 4, Height = 17, Background = Brushes.White, CornerRadius = new CornerRadius(1) },
                new Border { Width = 4, Height = 17, Background = Brushes.White, CornerRadius = new CornerRadius(1) }
            } };
        ToolTip.SetTip(_pauseIcon, "Paused — press Space to start");
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 0, 24, 18) };
        var left = new StackPanel { Spacing = 7, Children = { _status, _pausedControls } };
        footer.Children.Add(left);
        Grid.SetColumn(_pauseIcon, 1); footer.Children.Add(_pauseIcon);
        var shell = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        _stage = new Grid { Children = { _band, _configuration } };
        _stage.SizeChanged += (_, _) => FitCaption();
        shell.Children.Add(_stage); Grid.SetRow(footer, 1); shell.Children.Add(footer);
        Content = shell;
        SizeChanged += (_, _) => FitCaption();
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => { if (e.Key == Key.Space) _spaceDown = false; }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _spaceDown = false;
        Opened += async (_, _) => { Focus(); await RefreshAudioAsync(); };
        _controller.Changed += Refresh;
        Closed += (_, _) => { _controller.Changed -= Refresh; _scrollTimer.Stop(); _captionFrames.Clear(); };
        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            await _controller.ShutdownAsync();
            CloseSecondaryWindows?.Invoke();
            _allowClose = true;
            Close();
        };
        Refresh();
    }

    private async void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemComma && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
        { e.Handled = true; OpenSettings?.Invoke(); return; }
        if (e.Key == Key.Escape) { WindowState = WindowState.Normal; return; }
        if (e.Key == Key.F11) { ToggleFullscreen(); return; }
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || _audio.IsDropDownOpen || e.Source is not CaptionWindow) return;
        e.Handled = true;
        if (_spaceDown) return;
        _spaceDown = true;
        await _controller.ToggleAsync();
    }

    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Refresh); return; }
        var paused = _controller.IsPaused;
        _pausedControls.IsVisible = paused;
        _pauseIcon.IsVisible = paused;
        _status.Text = paused ? $"{_controller.Status}  ·  {(OperatingSystem.IsMacOS() ? "⌘, settings" : "Ctrl+, settings")}" : _controller.State == CaptionState.Live ? "" : _controller.Status;
        _status.IsVisible = !string.IsNullOrEmpty(_status.Text);
        var missing = _controller.MissingConfiguration;
        _configuration.IsVisible = paused && missing is not null;
        _message.Text = missing;
        _text.FontSize = _controller.Settings.FontSize;
        FitCaption();
        if (paused && !_wasPaused && IsVisible) _ = RefreshAudioAsync();
        _wasPaused = paused;
        if (!paused) Focus();
    }

    private async Task RefreshAudioAsync()
    {
        if (_listingAudio || !_controller.IsPaused) return;
        _listingAudio = true;
        try
        {
            var inputs = await MacAudioInputs.ListAsync();
            if (!_controller.IsPaused) return;
            _updatingAudio = true;
            var list = inputs.ToList();
            var selected = list.FirstOrDefault(x => x.Id == _controller.Settings.AudioDeviceId);
            if (selected is null)
            {
                selected = new AudioInput(_controller.Settings.AudioDeviceId, "Selected input unavailable — choose another");
                list.Add(selected);
            }
            _audio.ItemsSource = list;
            _audio.SelectedItem = selected;
        }
        catch { _controller.Errors.Add("Could not list audio inputs. Check microphone permission and the audio helper."); }
        finally { _updatingAudio = false; _listingAudio = false; if (IsActive && !_audio.IsDropDownOpen) Focus(); }
    }

    private void FitCaption()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var side = Math.Clamp(width * .05, 24, 80);
        _band.Padding = new Thickness(side, 0, side, 22);
        _text.LineHeight = _text.FontSize * 1.25;
        var stageHeight = _stage.Bounds.Height > 0 ? _stage.Bounds.Height : Height - 80;
        var rows = Math.Clamp((int)Math.Floor(Math.Max(0, stageHeight - 22) / _text.LineHeight), 1, 3);
        _band.Height = rows * _text.LineHeight + 22;
        _text.Height = rows * _text.LineHeight;
        _text.MaxLines = rows;
        var typeface = new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch);
        double Measure(string value) => new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, _text.FontSize, Brushes.White).WidthIncludingTrailingWhitespace;
        var lines = _captionLayout.Update(_controller.Text, Math.Max(1, width - 2 * side), Measure,
            rows, $"{_text.FontSize}:{typeface}", _controller.CaptionUtteranceId);
        _captionCanvas.Height = rows * _text.LineHeight;
        var geometry = $"{width}:{rows}:{_text.FontSize}:{typeface}";
        _captionRows = rows;
        if (_geometry != geometry || lines.Count == 0 || (_controller.IsPaused && !_wasPaused))
        {
            _geometry = geometry;
            _scrollTimer.Stop(); _scrollTarget = null; _captionFrames.Clear();
            ShowRows(lines.ToArray());
            return;
        }
        foreach (var frame in _captionLayout.ScrollFrames) QueueFrame(frame.ToArray());
        if (_captionLayout.ScrollFrames.Count == 0)
        {
            var target = lines.ToArray();
            if (_captionFrames.Count == 0 && _scrollTarget is not null && SameCompletedRows(_scrollTarget, target))
            {
                _scrollTarget = target;
                if (_rowLabels.Count > 0 && target.Length > 0) _rowLabels[^1].Text = target[^1];
            }
            else if (_captionFrames.Count == 0 && _scrollTarget is null) ShowRows(target);
            else QueueFrame(target, coalesceTail: true);
        }
        StartNextFrame();
    }

    private static bool SameCompletedRows(string[] left, string[] right) => left.Length == right.Length &&
        left.Take(Math.Max(0, left.Length - 1)).SequenceEqual(right.Take(Math.Max(0, right.Length - 1)));

    private void QueueFrame(string[] lines, bool coalesceTail = false)
    {
        if (coalesceTail && _captionFrames.Count > 0 && SameCompletedRows(_captionFrames.Last(), lines))
        {
            var frames = _captionFrames.ToArray(); _captionFrames.Clear();
            foreach (var frame in frames.Take(frames.Length - 1)) _captionFrames.Enqueue(frame);
        }
        // Ordinary batches retain each incoming row. An extreme backlog is
        // coalesced to bound display latency and memory.
        if (_captionFrames.Count >= 24) _captionFrames.Dequeue();
        _captionFrames.Enqueue(lines);
    }

    private void StartNextFrame()
    {
        if (_scrollTarget is not null) return;
        while (_captionFrames.TryDequeue(out var target))
        {
            var shift = _visibleLines.Length == _captionRows && target.Length == _captionRows &&
                _visibleLines.Skip(1).SequenceEqual(target.Take(_captionRows - 1));
            if (!shift) { ShowRows(target); continue; }
            _scrollTarget = target;
            DrawRows(_visibleLines.Concat(new[] { target[^1] }).ToArray());
            _scrollClock.Restart(); _scrollTimer.Start();
            return;
        }
    }

    private void AnimateScroll()
    {
        if (_scrollTarget is null) { _scrollTimer.Stop(); return; }
        var progress = Math.Clamp(_scrollClock.Elapsed.TotalMilliseconds / 200, 0, 1);
        // Smooth acceleration/deceleration, with the outgoing row fading away.
        var eased = progress * progress * (3 - 2 * progress);
        for (var index = 0; index < _rowLabels.Count; index++)
        {
            Canvas.SetTop(_rowLabels[index], (index - eased) * _text.LineHeight);
            _rowLabels[index].Opacity = index == 0 ? 1 - progress : 1;
        }
        if (progress < 1) return;
        var target = _scrollTarget; _scrollTarget = null; _scrollTimer.Stop();
        ShowRows(target); StartNextFrame();
    }

    private void ShowRows(string[] lines)
    {
        _visibleLines = lines;
        DrawRows(lines);
    }

    private void DrawRows(string[] lines)
    {
        _captionCanvas.Children.Clear(); _rowLabels.Clear();
        for (var index = 0; index < lines.Length; index++)
        {
            var label = new TextBlock { Text = lines[index], Foreground = Brushes.White,
                FontSize = _text.FontSize, FontFamily = _text.FontFamily, FontWeight = _text.FontWeight,
                LineHeight = _text.LineHeight, Height = _text.LineHeight, TextWrapping = TextWrapping.NoWrap };
            Canvas.SetTop(label, index * _text.LineHeight);
            _captionCanvas.Children.Add(label); _rowLabels.Add(label);
        }
    }
    public void ToggleFullscreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
}
