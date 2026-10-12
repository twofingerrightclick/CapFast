using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CapFast.Core;
using CapFast.Speech;
using CapFast.Views;

namespace CapFast;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<App>()
        .UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    private CaptionController? _controller;
    private CaptionWindow? _captions;
    private SettingsWindow? _settings;
    private ErrorLogWindow? _errors;
    private AppAppearance _appearance;
    public override void Initialize()
    {
        Name = "CapFast";
        var menu = new NativeMenu();
        var settingsItem = new NativeMenuItem("Settings…")
        { Gesture = new KeyGesture(Key.OemComma, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) };
        settingsItem.Click += (_, _) => ShowSettings();
        var errorsItem = new NativeMenuItem("Errors…");
        errorsItem.Click += (_, _) => ShowErrors();
        menu.Items.Add(settingsItem);
        menu.Items.Add(errorsItem);
        NativeMenu.SetMenu(this, menu);
        Styles.Add(new FluentTheme());
        _appearance = AppearancePreferences.Load();
        RequestedThemeVariant = _appearance == AppAppearance.Light
            ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var saved = CaptionPreferences.Load();
            _controller = new CaptionController(new CaptionSettings(loadLocalConfiguration: false)
            {
                IsLoadingConfiguration = true, Appearance = _appearance,
                AudioDeviceId = saved.AudioDeviceId, FontSize = saved.FontSize,
                ReadingWordsPerMinute = saved.ReadingWordsPerMinute,
                EnglishReadingWordsPerMinute = saved.EnglishReadingWordsPerMinute,
                SwahiliReadingWordsPerMinute = saved.SwahiliReadingWordsPerMinute,
                FrenchReadingWordsPerMinute = saved.FrenchReadingWordsPerMinute,
                AutomaticCaptionCatchUp = saved.AutomaticCaptionCatchUp
            }, settings => new GeminiCaptionSession(settings.GeminiKey, settings.AudioDeviceId, settings.RejectMusic),
                action => Dispatcher.UIThread.Post(action));
            _controller.Changed += ApplyAppearance;
            _captions = new CaptionWindow(_controller) { OpenSettings = ShowSettings, CloseSecondaryWindows = () =>
                { _settings?.Close(); _errors?.Close(); } };
            _captions.Opened += async (_, _) => { ShowSettings(); await LoadLocalConfigurationAsync(); };
            desktop.MainWindow = _captions;
            InstallSettingsShortcut(_captions);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyAppearance()
    {
        if (_controller is null || _controller.Settings.Appearance == _appearance) return;
        _appearance = _controller.Settings.Appearance;
        RequestedThemeVariant = _appearance == AppAppearance.Light
            ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        try { AppearancePreferences.Save(_appearance); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _controller.Errors.Add("Appearance changed, but could not be saved for the next launch. Check access to the CapFast application data folder."); }
    }

    private async Task LoadLocalConfigurationAsync()
    {
        if (_controller is not { } controller || _captions is not { } captions) return;
        var settings = controller.Settings;
        var originalGoogle = settings.GeminiKey;
        // Path discovery and file reads can wait for macOS authorization. Keep
        // both off the UI thread so the caption window is already usable.
        try
        {
            var read = Task.Run(() => new LocalConfiguration());
            if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))) != read && captions.IsVisible)
            {
                settings.IsLoadingConfiguration = false;
                settings.ConfigurationError = "Reading the local API file is taking longer than expected. Enter a key in Settings or check macOS file access.";
                controller.Errors.Add(settings.ConfigurationError);
                controller.NotifyLocalConfigurationLoaded();
            }
            var local = await read;
            if (!captions.IsVisible) return;
            settings.ApplyLocalConfiguration(local, originalGoogle);
            if (local.Error is { } error) controller.Errors.Add(error);
            controller.NotifyLocalConfigurationLoaded();
        }
        catch
        {
            if (!captions.IsVisible) return;
            settings.IsLoadingConfiguration = false;
            settings.ConfigurationError = "Could not load the local API configuration. Open Settings to enter a key or reload the file.";
            controller.Errors.Add(settings.ConfigurationError);
            controller.NotifyLocalConfigurationLoaded();
        }
    }

    private void InstallSettingsShortcut(Window window) => window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
    {
        if (e.Key != Key.OemComma || e.KeyModifiers != (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) return;
        e.Handled = true;
        ShowSettings();
    }, RoutingStrategies.Tunnel);

    private void ShowSettings()
    {
        if (_controller is null || _captions is null) return;
        if (_settings is null)
        {
            _settings = new SettingsWindow(_controller) { ToggleDisplayFullscreen = _captions.ToggleFullscreen };
            _settings.Closed += (_, _) => { _settings = null; _captions.Focus(); };
            InstallSettingsShortcut(_settings);
            _settings.Show(_captions);
        }
        _settings.Activate();
    }
    private void ShowErrors()
    {
        if (_controller is null || _captions is null) return;
        if (_errors is null)
        {
            _errors = new ErrorLogWindow(_controller.Errors);
            _errors.Closed += (_, _) => { _errors = null; _captions.Focus(); };
            InstallSettingsShortcut(_errors);
            _errors.Show(_captions);
        }
        _errors.Activate();
    }
}
