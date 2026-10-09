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
        _band = new Border { Child = _text, VerticalAlignment = VerticalAlignment.Bottom, ClipToBounds = true };
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
        Closed += (_, _) => _controller.Changed -= Refresh;
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
            rows, $"{_text.FontSize}:{typeface}");
        var caption = string.Join("\n", lines);
        if (_text.Text != caption) _text.Text = caption;
    }
    public void ToggleFullscreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
}
