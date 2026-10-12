using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using CapFast.Core;

namespace CapFast.Views;

public sealed class CaptionWindow : Window
{
    private readonly CaptionController _controller;
    private readonly LanguageSurfaceRouting _languages = new();
    private readonly CaptionSurface _englishCaptions = new();
    private readonly CaptionSurface _frenchCaptions = new();
    private readonly TextBlock _englishLabel = new() { Text = "English / Swahili", FontSize = 14, Opacity = .65, Margin = new Thickness(24, 10, 24, 0) };
    private readonly TextBlock _frenchLabel = new() { Text = "Français", FontSize = 14, Opacity = .65, Margin = new Thickness(24, 10, 24, 0) };
    private readonly Border _divider = new() { Background = Brushes.Gray, Opacity = .25 };
    private readonly Grid _columns;
    private readonly Grid _frenchColumn;
    private readonly Grid _stage;
    private bool _allowClose;
    private bool _closing;
    public Action? OpenSettings { get; set; }
    public Action? CloseSecondaryWindows { get; set; }

    public CaptionWindow(CaptionController controller)
    {
        _controller = controller;
        Title = "CapFast";
        Focusable = true;
        Background = Brushes.Black;
        Width = 1280; Height = 720; MinWidth = 640; MinHeight = 360;
        var englishColumn = new Grid { RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { _englishLabel, _englishCaptions } };
        Grid.SetRow(_englishCaptions, 1);
        _frenchColumn = new Grid { RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { _frenchLabel, _frenchCaptions } };
        Grid.SetRow(_frenchCaptions, 1);
        _columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,*"),
            Children = { englishColumn, _divider, _frenchColumn } };
        Grid.SetColumn(_divider, 1); Grid.SetColumn(_frenchColumn, 2);
        _stage = new Grid { Children = { _columns } };
        _stage.SizeChanged += (_, _) => FitCaption();
        Content = _stage;
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        ApplyTheme();
        SizeChanged += (_, _) => FitCaption();
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => Focus();
        _controller.Changed += Refresh;
        _controller.CaptionReceived += ReceiveCaption;
        _controller.LanguageAssignmentChanged += ApplyLanguages;
        _controller.CaptionsCleared += ClearCaption;
        _controller.CaptionsFading += FadeCaption;
        _controller.CanAcceptCaptions = () => _englishCaptions.CanAcceptCaptions && _frenchCaptions.CanAcceptCaptions;
        _controller.DisplayQueueStatus = () => $"English / Swahili: {_englishCaptions.QueueStatus}\nFrench: {_frenchCaptions.QueueStatus}";
        _controller.HasUnreadCaptions = () => _englishCaptions.HasUnreadCaptions || _frenchCaptions.HasUnreadCaptions;
        Closed += (_, _) => { _controller.Changed -= Refresh; _controller.CaptionReceived -= ReceiveCaption;
            _controller.LanguageAssignmentChanged -= ApplyLanguages;
            _controller.CaptionsFading -= FadeCaption;
            _controller.CaptionsCleared -= ClearCaption; _controller.CanAcceptCaptions = null;
            _controller.HasUnreadCaptions = null; _controller.DisplayQueueStatus = null;
            _englishCaptions.Stop(); _frenchCaptions.Stop(); };
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
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemComma && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
        { e.Handled = true; OpenSettings?.Invoke(); return; }
        if (e.Key == Key.Escape) { WindowState = WindowState.Normal; return; }
        if (e.Key == Key.F11) { ToggleFullscreen(); return; }
    }

    private CaptionState _lastRoutingState = CaptionState.Paused;
    private void Refresh()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { Avalonia.Threading.Dispatcher.UIThread.Post(Refresh); return; }
        if (_controller.State == CaptionState.Starting && _lastRoutingState != CaptionState.Starting)
            _languages.BeginRun();
        _lastRoutingState = _controller.State;
        FitCaption();
    }

    private void ApplyLanguages(LanguageAssignment? assignment)
    {
        ApplyRouting(_languages.Apply(assignment));
    }
    private void ApplyRouting((bool ClearFirst, bool ClearSecond) clears)
    {
        if (clears.ClearFirst) _englishCaptions.ClearCaption();
        if (clears.ClearSecond) _frenchCaptions.ClearCaption();
        _englishLabel.Text = LanguageSurfaceRouting.Label(_languages.First);
        _frenchLabel.Text = LanguageSurfaceRouting.Label(_languages.Second);
    }

    private void ReceiveCaption(CaptionUpdate update)
    {
        try
        {
            if (update.PrimaryLanguage is { } primary && update.SecondaryLanguage is { } secondary)
            {
                if (update.IsSpeechBlockDrain)
                    ApplyRouting(_languages.ApplyDrain(new(update.SourceLanguage!, primary, secondary, update.AssignmentEpoch)));
                bool Submit(string code, string text)
                {
                    var slot = _languages.Slot(code);
                    if (slot < 0) return true;
                    return (slot == 0 ? _englishCaptions : _frenchCaptions).Submit(update with
                    { UtteranceId = $"{update.UtteranceId}:{code}", Text = text, FrenchText = null, PrimaryLanguage = code });
                }
                var accepted = Submit(primary, update.Text);
                accepted = Submit(secondary, update.FrenchText ?? "") && accepted;
                if (!accepted) _ = _controller.PauseForDisplayBacklogAsync();
                return;
            }
            var ready = _englishCaptions.Submit(update with { PrimaryLanguage = "en" });
            ready = _frenchCaptions.Submit(update with { Text = update.FrenchText ?? "", FrenchText = null, PrimaryLanguage = "fr" }) && ready;
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
        _englishCaptions.Configure(_controller.Settings);
        _frenchCaptions.Configure(_controller.Settings);
    }

    public void ToggleFullscreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
}
