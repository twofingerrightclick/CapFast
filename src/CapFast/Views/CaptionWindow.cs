using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using CapFast.Core;
using CapFast.Speech;

namespace CapFast.Views;

public sealed class CaptionWindow : Window
{
    private readonly CaptionController _controller;
    private readonly CaptionSurface _englishCaptions = new();
    private readonly CaptionSurface _frenchCaptions = new() { IsVisible = false };
    private readonly TextBlock _englishLabel = new() { Text = "English", FontSize = 14, Opacity = .65, Margin = new Thickness(24, 10, 24, 0), IsVisible = false };
    private readonly TextBlock _frenchLabel = new() { Text = "Français", FontSize = 14, Opacity = .65, Margin = new Thickness(24, 10, 24, 0) };
    private readonly Border _divider = new() { Background = Brushes.Gray, Opacity = .25, IsVisible = false };
    private readonly Grid _columns;
    private readonly Grid _frenchColumn;
    private readonly Grid _stage;
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
        var englishColumn = new Grid { RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { _englishLabel, _englishCaptions } };
        Grid.SetRow(_englishCaptions, 1);
        _frenchColumn = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), IsVisible = false,
            Children = { _frenchLabel, _frenchCaptions } };
        Grid.SetRow(_frenchCaptions, 1);
        _columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,0,0"),
            Children = { englishColumn, _divider, _frenchColumn } };
        Grid.SetColumn(_divider, 1); Grid.SetColumn(_frenchColumn, 2);
        _stage = new Grid { Children = { _columns, _configuration } };
        _stage.SizeChanged += (_, _) => FitCaption();
        shell.Children.Add(_stage); Grid.SetRow(footer, 1); shell.Children.Add(footer);
        Content = shell;
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        ApplyTheme();
        SizeChanged += (_, _) => FitCaption();
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => { if (e.Key == Key.Space) _spaceDown = false; }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _spaceDown = false;
        Opened += async (_, _) => { Focus(); await RefreshAudioAsync(); };
        _controller.Changed += Refresh;
        _controller.CaptionReceived += ReceiveCaption;
        _controller.CaptionsCleared += ClearCaption;
        _controller.CaptionsFading += FadeCaption;
        _controller.CanAcceptCaptions = () => _englishCaptions.CanAcceptCaptions && (!_controller.Settings.ShowFrench || _frenchCaptions.CanAcceptCaptions);
        Closed += (_, _) => { _controller.Changed -= Refresh; _controller.CaptionReceived -= ReceiveCaption;
            _controller.CaptionsFading -= FadeCaption;
            _controller.CaptionsCleared -= ClearCaption; _controller.CanAcceptCaptions = null;
            _activityTimer.Stop(); _englishCaptions.Stop(); _frenchCaptions.Stop(); };
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

    private void ApplyTheme()
    {
        var light = ActualThemeVariant == ThemeVariant.Light;
        Background = light ? Brushes.White : Brushes.Black;
        var captionColor = light ? Color.Parse("#171717") : Colors.White;
        var foreground = new SolidColorBrush(captionColor);
        Foreground = foreground;
        _englishLabel.Foreground = _frenchLabel.Foreground = foreground;
        _englishCaptions.SetCaptionColor(captionColor);
        _frenchCaptions.SetCaptionColor(captionColor);
        foreach (var bar in ((StackPanel)_pauseIcon).Children.OfType<Border>()) bar.Background = foreground;
        var activity = new SolidColorBrush(Color.Parse(light ? "#0067B8" : "#59B8FF"));
        _idleDot.Background = activity;
        _musicIcon.Foreground = activity;
        foreach (var dot in _listeningDots) dot.Background = activity;
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
        var bilingual = _controller.Settings.ShowFrench;
        _columns.ColumnDefinitions[1].Width = new GridLength(bilingual ? 1 : 0);
        _columns.ColumnDefinitions[2].Width = bilingual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _englishLabel.IsVisible = _frenchColumn.IsVisible = _frenchCaptions.IsVisible = _divider.IsVisible = bilingual;
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
        try
        {
            var ready = _englishCaptions.Submit(update);
            if (_controller.Settings.ShowFrench)
                ready = _frenchCaptions.Submit(update with { Text = update.FrenchText ?? "", FrenchText = null }) && ready;
            if (!ready) _ = _controller.PauseForDisplayBacklogAsync();
        }
        catch (InvalidOperationException) { _ = _controller.PauseForDisplayBacklogAsync(); }
    }

    private void FadeCaption()
    {
        _englishCaptions.FadeCaption();
        _frenchCaptions.FadeCaption();
    }

    private void ClearCaption()
    {
        _englishCaptions.ClearCaption();
        _frenchCaptions.ClearCaption();
    }

    private void FitCaption()
    {
        _englishCaptions.Configure(_controller.Settings.FontSize, _controller.Settings.ReadingWordsPerMinute);
        _frenchCaptions.Configure(_controller.Settings.FontSize, _controller.Settings.ReadingWordsPerMinute);
    }

    public void ToggleFullscreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
}
