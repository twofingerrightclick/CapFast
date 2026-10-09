using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CapFast.Core;
using CapFast.Speech;

namespace CapFast.Views;

public sealed class SettingsWindow : Window
{
    private readonly CaptionController _controller;
    private readonly StackPanel _configuration;
    private readonly ComboBox _provider = new() { ItemsSource = new[] { "Google Gemini", "Azure Speech" } };
    private readonly TextBox _gemini = new() { PasswordChar = '●' };
    private readonly ComboBox _googleMode = new() { ItemsSource = new[]
        { "Swahili transcription → English translation", "Live Translate (comparison)" } };
    private readonly TextBox _azure = new() { PasswordChar = '●' };
    private readonly TextBox _region = new();
    private readonly ComboBox _locale = new() { ItemsSource = new[] { "sw-KE", "sw-TZ" } };
    private readonly CheckBox _stabilize = new() { Content = "Stabilize provisional translations" };
    private readonly Slider _size = new() { Minimum = 28, Maximum = 72 };
    private readonly Slider _readingSpeed = new() { Minimum = 80, Maximum = 300, TickFrequency = 10, IsSnapToTickEnabled = true };
    private readonly TextBlock _readingLabel = new() { FontWeight = FontWeight.Medium };
    private readonly SelectableTextBlock _configurationPath = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Opacity = .75 };
    private readonly StackPanel _googlePanel;
    private readonly StackPanel _azurePanel;
    private readonly Button _demo = new() { Content = "Preview sample captions" };
    private readonly Button _pause = new() { Content = "Pause transcription to edit settings" };

    private readonly Button _openFile = new() { Content = "Open API key file" };
    private readonly Button _reloadFile = new() { Content = "Reload file" };
    private readonly Button _microphone = new() { Content = "Check microphone permission" };
    private readonly TextBlock _diagnostic = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private bool _checkingMicrophone;
    private bool _loading;
    private bool _closed;
    private bool _reloadingFile;
    private Task<LocalConfiguration>? _reloadRead;
    private string? _reloadPath;
    private bool _openingFile;
    private Task? _prepareFile;
    private string? _preparePath;
    private CancellationTokenSource? _microphoneCheck;

    public SettingsWindow(CaptionController controller)
    {
        _controller = controller;
        Title = "CapFast — Settings";
        Width = 520; Height = 680; MinWidth = 420; MinHeight = 420;
        _provider.SelectedIndex = controller.Settings.Provider == SpeechProvider.Google ? 0 : 1;
        _gemini.Text = controller.Settings.GeminiKey;
        _googleMode.SelectedIndex = controller.Settings.GoogleMode == GoogleCaptionMode.TwoStage ? 0 : 1;
        _azure.Text = controller.Settings.AzureKey;
        _region.Text = controller.Settings.AzureRegion;
        _locale.SelectedItem = controller.Settings.Locale;
        _stabilize.IsChecked = controller.Settings.Stabilize;
        _size.Value = controller.Settings.FontSize;
        _configurationPath.Text = controller.Settings.ConfigurationPath;
        _readingSpeed.Value = controller.Settings.ReadingWordsPerMinute;
        _readingLabel.Text = $"Reading speed: {controller.Settings.ReadingWordsPerMinute} words/minute";
        _googlePanel = new StackPanel { Spacing = 8, Children = { Label("Google caption mode"), _googleMode,
            new TextBlock { Text = "Two-stage mode translates completed speech segments. Live transcription sessions last up to 10 minutes; restart when the session ends.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 }, Label("Gemini API key"), _gemini } };
        _azurePanel = new StackPanel { Spacing = 8, Children = { Label("Azure Speech key"), _azure,
            Label("Azure region"), _region, Label("Swahili locale"), _locale, _stabilize } };
        _configuration = new StackPanel { Spacing = 14, Children = { Label("Translation service"), _provider,
            _googlePanel, _azurePanel } };
        var close = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var clear = new Button { Content = "Clear captions" };
        clear.Click += (_, _) => controller.ClearCaptions();
        _demo.Click += async (_, _) => { await controller.RunDemoAsync(); };
        _pause.Click += async (_, _) => await controller.PauseAsync();
        _openFile.Click += async (_, _) => await OpenConfigurationFileAsync();
        _reloadFile.Click += async (_, _) => await ReloadConfigurationAsync();
        _microphone.Click += async (_, _) => await CheckMicrophoneAsync();
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(28), Spacing = 18,
            Children = { new TextBlock { Text = "Settings", FontSize = 28, FontWeight = FontWeight.SemiBold },
                _notice, _pause, _configuration,
                new TextBlock { Text = "Keys load from this local file on launch. Keys entered above stay in memory until the app closes.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 },
                _configurationPath,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _openFile, _reloadFile } },
                _microphone, _diagnostic,
                new Separator(), Label("Caption size"), _size,
                _readingLabel, _readingSpeed,
                new TextBlock { Text = "Text fills the window. This pace controls how soon older text can leave; a slower pace may delay captions behind the speaker.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _demo, clear } },
                new TextBlock { Text = "Press Space in the caption window to start or pause. F11 toggles fullscreen; Esc exits fullscreen.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 }, close } } };
        _provider.SelectionChanged += (_, _) => Save();
        _googleMode.SelectionChanged += (_, _) => Save();
        _gemini.TextChanged += (_, _) => Save();
        _azure.TextChanged += (_, _) => Save();
        _region.TextChanged += (_, _) => Save();
        _locale.SelectionChanged += (_, _) => Save();
        _stabilize.IsCheckedChanged += (_, _) => Save();
        _size.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            controller.Settings.FontSize = _size.Value;
            controller.SettingsChanged();
        };
        _readingSpeed.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            controller.Settings.ReadingWordsPerMinute = (int)Math.Round(_readingSpeed.Value);
            _readingLabel.Text = $"Reading speed: {controller.Settings.ReadingWordsPerMinute} words/minute";
            controller.SettingsChanged();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        controller.Changed += Refresh;
        controller.LocalConfigurationLoaded += RefreshLocalConfiguration;
        Closed += (_, _) =>
        {
            _closed = true;
            controller.Changed -= Refresh;
            controller.LocalConfigurationLoaded -= RefreshLocalConfiguration;
            _microphoneCheck?.Cancel();
        };
        _diagnostic.Text = controller.Settings.ConfigurationError ?? "";
        Refresh();
    }

    private void RefreshLocalConfiguration()
    {
        if (_closed) return;
        _loading = true;
        _gemini.Text = _controller.Settings.GeminiKey;
        _azure.Text = _controller.Settings.AzureKey;
        _region.Text = _controller.Settings.AzureRegion;
        _configurationPath.Text = _controller.Settings.ConfigurationPath;
        _diagnostic.Text = _controller.Settings.ConfigurationError ?? "";
        _loading = false;
    }

    private async Task OpenConfigurationFileAsync()
    {
        if (_openingFile) return;
        _openingFile = true;
        Refresh();
        try
        {
            var path = _controller.Settings.ConfigurationPath;
            if (_prepareFile is null || _prepareFile.IsCompleted || _preparePath != path)
            {
                _preparePath = path;
                _prepareFile = Task.Run(() =>
                {
                    if (File.Exists(path)) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using var file = new FileStream(path, options);
                    using var writer = new StreamWriter(file);
                    writer.WriteLine("# Add your Google API key, save, then click Reload file in CapFast.");
                    writer.WriteLine("GEMINI_API_KEY=");
                });
            }
            await _prepareFile.WaitAsync(TimeSpan.FromSeconds(5));
            if (_closed) return;
            var info = new ProcessStartInfo { UseShellExecute = true };
            if (OperatingSystem.IsMacOS())
            {
                info.FileName = "/usr/bin/open";
                info.ArgumentList.Add("-t");
                info.ArgumentList.Add(path);
            }
            else info.FileName = path;
            using var process = Process.Start(info);
            _diagnostic.Text = "Save the API key file, then click Reload file.";
        }
        catch
        {
            if (_closed) return;
            const string message = "Could not open the API key file. Open the displayed path in your text editor.";
            _diagnostic.Text = message;
            _controller.Errors.Add(message);
        }
        finally { _openingFile = false; if (!_closed) Refresh(); }
    }

    private async Task ReloadConfigurationAsync()
    {
        if (!_controller.IsPaused || _reloadingFile) return;
        _reloadingFile = true;
        var settings = _controller.Settings;
        var originalGoogle = settings.GeminiKey;
        var originalAzure = settings.AzureKey;
        var originalRegion = settings.AzureRegion;
        var path = settings.ConfigurationPath;
        Refresh();
        _diagnostic.Text = "Reading local API configuration…";
        try
        {
            // Reuse a blocked read rather than starting another on each click.
            if (_reloadRead is null || _reloadRead.IsCompleted || _reloadPath != path)
            { _reloadPath = path; _reloadRead = Task.Run(() => new LocalConfiguration(path)); }
            var local = await _reloadRead.WaitAsync(TimeSpan.FromSeconds(5));
            if (_closed) return;
            if (local.Error is { } error)
            { _diagnostic.Text = error; _controller.Errors.Add(error); return; }
            settings.ApplyLocalConfiguration(local, originalGoogle, originalAzure, originalRegion);
            _controller.NotifyLocalConfigurationLoaded();
            _diagnostic.Text = "Local API configuration reloaded.";
        }
        catch (TimeoutException)
        {
            if (!_closed) _diagnostic.Text = "The API file is still waiting for access. Check macOS file permissions or enter a key above, then try Reload again.";
        }
        catch
        {
            if (!_closed)
            { const string message = "Could not reload the local API configuration."; _diagnostic.Text = message; _controller.Errors.Add(message); }
        }
        finally { _reloadingFile = false; if (!_closed) Refresh(); }
    }

    private async Task CheckMicrophoneAsync()
    {
        if (_checkingMicrophone || !_controller.IsPaused) return;
        _checkingMicrophone = true;
        using var cancellation = new CancellationTokenSource();
        _microphoneCheck = cancellation;
        Refresh();
        _diagnostic.Text = "Checking microphone permission…";
        try
        {
            await GeminiCaptionSession.RequestMicrophonePermissionAsync(cancellation.Token);
            if (_closed) return;
            _diagnostic.Text = "Microphone permission granted. Return to captions and press Space to start.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_closed) return;
            var message = error is CaptionSessionException ? error.Message
                : "Could not check microphone permission. Check macOS System Settings → Privacy & Security → Microphone.";
            _diagnostic.Text = message;
            _controller.Errors.Add(message);
        }
        finally
        {
            _microphoneCheck = null;
            _checkingMicrophone = false;
            if (!_closed) Refresh();
        }
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.Medium };
    private void Save()
    {
        if (_loading || !_controller.IsPaused) return;
        var settings = _controller.Settings;
        settings.Provider = _provider.SelectedIndex == 0 ? SpeechProvider.Google : SpeechProvider.Azure;
        settings.GoogleMode = _googleMode.SelectedIndex == 0 ? GoogleCaptionMode.TwoStage : GoogleCaptionMode.LiveTranslate;
        settings.GeminiKey = _gemini.Text ?? "";
        settings.AzureKey = _azure.Text ?? "";
        settings.AzureRegion = _region.Text ?? "";
        settings.Locale = _locale.SelectedItem as string ?? "sw-KE";
        settings.Stabilize = _stabilize.IsChecked == true;
        _controller.SettingsChanged();
    }
    private void Refresh()
    {
        _configuration.IsEnabled = _controller.IsPaused;
        _pause.IsVisible = !_controller.IsPaused;
        _demo.IsEnabled = _controller.IsPaused && !_checkingMicrophone;
        _openFile.IsEnabled = _reloadFile.IsEnabled = _controller.IsPaused && !_checkingMicrophone && !_reloadingFile && !_openingFile;
        _microphone.IsEnabled = _controller.IsPaused && !_checkingMicrophone;
        _microphone.IsVisible = OperatingSystem.IsMacOS();
        _googlePanel.IsVisible = _provider.SelectedIndex == 0;
        _azurePanel.IsVisible = !_googlePanel.IsVisible;
        _notice.Text = _controller.IsPaused ? "Configure the service, then return to the caption window and press Space."
            : "Transcription is running. Pause to change the service or credentials.";
    }
}
