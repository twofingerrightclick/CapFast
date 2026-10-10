using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Documents;
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
    private readonly CaptionPresentation _presentation = new();
    private readonly Canvas _captionCanvas = new() { ClipToBounds = true };
    private readonly System.Diagnostics.Stopwatch _presentationClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<TextBlock> _rowLabels = new();
    private readonly CaptionWordReveal _wordReveal = new();
    private readonly List<(CaptionWordReveal.Word Word, SolidColorBrush Brush)> _wordBrushes = new();
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly System.Diagnostics.Stopwatch _fadeClock = new();
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly System.Diagnostics.Stopwatch _scrollClock = new();
    private string[] _visibleLines = Array.Empty<string>();
    private string[]? _scrollTarget;
    private string _geometry = "";
    private int _scrollRows;
    private int _presentationGeneration;
    private bool _waitingForReveal;
    private readonly Grid _stage;
    private readonly Border _band;
    private readonly StackPanel _pausedControls;
    private readonly ComboBox _audio = new() { Width = 280, PlaceholderText = "Audio input" };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, FontSize = 18, MaxWidth = 540 };
    private readonly StackPanel _configuration;
    private readonly TextBlock _status = new() { FontSize = 13, Opacity = .7 };
    private readonly Control _pauseIcon;
    private readonly Canvas _audioIndicator = new() { Width = 32, Height = 24, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _idleDot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.Parse("#59B8FF")) };
    private readonly TextBlock _musicIcon = new() { Text = "♫", FontSize = 22, Foreground = new SolidColorBrush(Color.Parse("#59B8FF")), IsVisible = false };
    private readonly List<Border> _listeningDots = new();
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly System.Diagnostics.Stopwatch _activityClock = System.Diagnostics.Stopwatch.StartNew();
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
        _band = new Border { Child = _captionCanvas, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
        _fadeTimer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(_fadeClock.Elapsed.TotalMilliseconds / 800, 0, 1);
            _captionCanvas.Opacity = 1 - progress * progress * (3 - 2 * progress);
            if (progress >= 1) ClearCaption();
        };
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
        Canvas.SetLeft(_idleDot, 12); Canvas.SetTop(_idleDot, 8); _audioIndicator.Children.Add(_idleDot);
        Canvas.SetLeft(_musicIcon, 5); Canvas.SetTop(_musicIcon, -3); _audioIndicator.Children.Add(_musicIcon);
        for (var index = 0; index < 3; index++)
        {
            var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5),
                Background = new SolidColorBrush(Color.Parse("#59B8FF")), IsVisible = false };
            Canvas.SetLeft(dot, 3 + index * 10); Canvas.SetTop(dot, 9.5);
            _audioIndicator.Children.Add(dot); _listeningDots.Add(dot);
        }
        _activityTimer.Tick += (_, _) => AnimateAudioActivity();
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 0, 24, 18) };
        var left = new StackPanel { Spacing = 7, Children = { _status, _pausedControls } };
        footer.Children.Add(left);
        var corner = new Grid { Width = 32, Height = 24, VerticalAlignment = VerticalAlignment.Center,
            Children = { _pauseIcon, _audioIndicator } };
        Grid.SetColumn(corner, 1); footer.Children.Add(corner);
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
        _controller.CaptionReceived += ReceiveCaption;
        _controller.CaptionsCleared += ClearCaption;
        _controller.CaptionsFading += FadeCaption;
        _controller.CanAcceptCaptions = () => _presentation.CanAcceptCaptions;
        Closed += (_, _) => { _controller.Changed -= Refresh; _controller.CaptionReceived -= ReceiveCaption;
            _controller.CaptionsFading -= FadeCaption; _fadeTimer.Stop();
            _controller.CaptionsCleared -= ClearCaption; _controller.CanAcceptCaptions = null;
            _scrollTimer.Stop(); _activityTimer.Stop(); _presentation.Clear(); _wordReveal.Clear(); _wordBrushes.Clear(); };
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
        _audioIndicator.IsVisible = !paused && _controller.HasAudioActivity;
        var listening = _audioIndicator.IsVisible && _controller.AudioActivity == AudioActivity.Speech;
        var music = _audioIndicator.IsVisible && _controller.AudioActivity == AudioActivity.Music;
        _musicIcon.IsVisible = music;
        _idleDot.IsVisible = !listening && !music;
        foreach (var dot in _listeningDots) dot.IsVisible = listening;
        ToolTip.SetTip(_audioIndicator, music ? "Music detected — captions fade after seven seconds" : listening ? "Speech detected — qualifying or transcribing" : "Listening — captions fade after fifteen seconds without translatable speech; music and singing are skipped");
        Avalonia.Automation.AutomationProperties.SetName(_audioIndicator, music ? "Music detected" : listening ? "Speech detected" : "Listening for speech");
        if (listening) { AnimateAudioActivity(); _activityTimer.Start(); }
        else _activityTimer.Stop();
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

    private void AnimateAudioActivity()
    {
        for (var index = 0; index < _listeningDots.Count; index++)
        {
            var pulse = (1 + Math.Sin(_activityClock.Elapsed.TotalSeconds * 2 * Math.PI / 1.2 - index * .85)) / 2;
            _listeningDots[index].Opacity = .3 + .7 * pulse;
            Canvas.SetTop(_listeningDots[index], 9.5 - 2 * pulse);
        }
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

    private void ReceiveCaption(CaptionUpdate update)
    {
        if (_fadeClock.IsRunning)
        {
            if (string.IsNullOrWhiteSpace(update.Text)) return;
            ClearCaption();
        }
        try
        {
            if (!_presentation.Submit(update)) _ = _controller.PauseForDisplayBacklogAsync();
        }
        catch (InvalidOperationException) { _ = _controller.PauseForDisplayBacklogAsync(); }
        FitCaption();
    }

    private void FadeCaption()
    {
        _scrollTimer.Stop();
        _fadeClock.Restart(); _fadeTimer.Start();
    }

    private void ClearCaption()
    {
        _fadeTimer.Stop(); _fadeClock.Reset(); _captionCanvas.Opacity = 1;
        _presentation.Clear(); _scrollTarget = null; _waitingForReveal = false; _scrollTimer.Stop(); _wordReveal.Clear();
        _presentationGeneration = _presentation.Generation;
        ShowRows(Array.Empty<string>());
    }

    private void FitCaption()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var side = Math.Clamp(width * .05, 24, 80);
        _band.Padding = new Thickness(side, 22, side, 22);
        _text.LineHeight = _text.FontSize * 1.25;
        var stageHeight = _stage.Bounds.Height > 0 ? _stage.Bounds.Height : Height - 80;
        var rows = Math.Max(1, (int)Math.Floor(Math.Max(0, stageHeight - 44) / _text.LineHeight));
        _band.Height = rows * _text.LineHeight + 44;
        _captionCanvas.Height = rows * _text.LineHeight;
        var typeface = new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch);
        double Measure(string value) => new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, _text.FontSize, Brushes.White).WidthIncludingTrailingWhitespace;
        var geometry = $"{width}:{rows}:{_text.FontSize}:{typeface}";
        _presentation.ReadingWordsPerMinute = _controller.Settings.ReadingWordsPerMinute;
        _presentation.Configure(Math.Max(1, width - 2 * side), rows, Measure, geometry,
            _presentationClock.Elapsed.TotalSeconds);
        if (_geometry != geometry || _presentationGeneration != _presentation.Generation)
        {
            _geometry = geometry; _presentationGeneration = _presentation.Generation;
            _scrollTarget = null; _waitingForReveal = false;
            ShowRows(_presentation.CurrentLines.ToArray(), revealNewWords: false);
        }
        StartNextFrame();
    }

    private void StartNextFrame()
    {
        if (_fadeClock.IsRunning) return;
        if (_scrollTarget is not null) return;
        var now = _presentationClock.Elapsed.TotalSeconds;
        if (_waitingForReveal)
        {
            if (_wordReveal.IsAnimating(now)) { _scrollTimer.Start(); return; }
            _waitingForReveal = false;
            _presentation.Presented(now);
        }
        if (!_presentation.TryPresent(now, out var frame))
        {
            if (_presentation.PendingPhrases > 0) _scrollTimer.Start();
            else if (!_wordReveal.IsAnimating(_presentationClock.Elapsed.TotalSeconds)) _scrollTimer.Stop();
            return;
        }
        var target = frame!.Lines.ToArray();
        if (frame.ScrollRows == 0)
        {
            ShowRows(target);
            _waitingForReveal = true;
            _scrollTimer.Start();
            return;
        }
        _scrollRows = frame.ScrollRows;
        _scrollTarget = target;
        DrawRows(frame.AnimationLines.ToArray());
        _scrollClock.Restart(); _scrollTimer.Start();
    }

    private void AnimateScroll()
    {
        var now = _presentationClock.Elapsed.TotalSeconds;
        foreach (var (word, brush) in _wordBrushes) brush.Opacity = word.Opacity(now);
        if (_scrollTarget is null) { StartNextFrame(); return; }
        var progress = Math.Clamp(_scrollClock.Elapsed.TotalMilliseconds / 300, 0, 1);
        var eased = progress * progress * (3 - 2 * progress);
        for (var index = 0; index < _rowLabels.Count; index++)
        {
            Canvas.SetTop(_rowLabels[index], (index - eased * _scrollRows) * _text.LineHeight);
            _rowLabels[index].Opacity = index < _scrollRows ? 1 - progress : 1;
        }
        if (progress < 1) return;
        var target = _scrollTarget; _scrollTarget = null;
        _wordReveal.RemoveLeadingRows(_scrollRows);
        _visibleLines = target;
        DrawRows(target, preserveReveal: true);
        _waitingForReveal = true;
        StartNextFrame();
    }

    private void ShowRows(string[] lines, bool revealNewWords = true)
    {
        _visibleLines = lines;
        DrawRows(lines, revealNewWords: revealNewWords);
    }

    private void DrawRows(string[] lines, bool preserveReveal = false, bool revealNewWords = true)
    {
        var now = _presentationClock.Elapsed.TotalSeconds;
        if (!preserveReveal) _wordReveal.Update(lines, now, revealNewWords);
        _captionCanvas.Children.Clear(); _rowLabels.Clear(); _wordBrushes.Clear();
        for (var index = 0; index < lines.Length; index++)
        {
            var label = new TextBlock { Foreground = Brushes.White,
                FontSize = _text.FontSize, FontFamily = _text.FontFamily, FontWeight = _text.FontWeight,
                LineHeight = _text.LineHeight, Height = _text.LineHeight, TextWrapping = TextWrapping.NoWrap };
            AutomationProperties.SetName(label, lines[index]);
            foreach (var word in _wordReveal.Rows[index])
            {
                var brush = new SolidColorBrush(Colors.White, word.Opacity(now));
                label.Inlines!.Add(new Run(word.Text) { Foreground = brush });
                _wordBrushes.Add((word, brush));
            }
            Canvas.SetTop(label, index * _text.LineHeight);
            _captionCanvas.Children.Add(label); _rowLabels.Add(label);
        }
    }
    public void ToggleFullscreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
}
