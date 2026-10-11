using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CapFast.Core;
using CapFast.Speech;

namespace CapFast.Views;

public sealed class SettingsWindow : Window
{
    private readonly CaptionController _controller;
    private readonly CheckBox _lightMode = new() { Content = "Light mode" };
    private readonly StackPanel _configuration;
    private readonly ComboBox _provider = new() { ItemsSource = new[] { "Google Gemini", "Azure Speech" } };
    private readonly TextBox _gemini = new() { PasswordChar = '●' };
    private readonly ComboBox _googleMode = new() { ItemsSource = new[]
        { "Swahili transcription → English translation", "Live Translate (comparison)" } };
    private readonly CheckBox _french = new() { Content = "Add French captions" };
    private readonly CheckBox _rejectMusic = new() { Content = "Reject detected music and singing" };
    private readonly TextBlock _googleDescription = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 };
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
    private readonly Button _pause = new() { Content = "Start captions" };
    private readonly Button _clear = new() { Content = "Clear captions" };
    private readonly Button _fullscreen = new() { Content = "Toggle display fullscreen" };
    private readonly ComboBox _audio = new() { MinWidth = 250 };
    private readonly Button _refreshAudio = new() { Content = "Refresh inputs" };
    private readonly TextBlock _audioAvailability = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _activity = new() { FontSize = 25, FontWeight = FontWeight.Bold, Width = 40,
        Foreground = new SolidColorBrush(Color.Parse("#59B8FF")) };
    private readonly TextBlock _sessionStatus = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private readonly Button _openFile = new() { Content = "Open API key file" };
    private readonly Button _reloadFile = new() { Content = "Reload file" };
    private readonly Button _microphone = new() { Content = "Check microphone permission" };
    private readonly TextBlock _diagnostic = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _latestIssue = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium };
    private readonly StackPanel _diagnosticHistory = new() { Spacing = 7 };
    private bool _checkingMicrophone;
    private bool _updatingAudio;
    private bool _listingAudio;
    private bool _audioAvailable = true;
    private bool _loading;
    private bool _closed;
    private bool _reloadingFile;
    private Task<LocalConfiguration>? _reloadRead;
    private string? _reloadPath;
    private bool _openingFile;
    private Task? _prepareFile;
    private string? _preparePath;
    private CancellationTokenSource? _microphoneCheck;
    public Action? ToggleDisplayFullscreen { get; set; }

    public SettingsWindow(CaptionController controller)
    {
        _controller = controller;
        Title = "CapFast — Settings";
        Width = 600; Height = 760; MinWidth = 440; MinHeight = 480;
        _lightMode.IsChecked = controller.Settings.Appearance == AppAppearance.Light;
        _provider.SelectedIndex = controller.Settings.Provider == SpeechProvider.Google ? 0 : 1;
        _gemini.Text = controller.Settings.GeminiKey;
        _googleMode.SelectedIndex = controller.Settings.GoogleMode == GoogleCaptionMode.TwoStage ? 0 : 1;
        _french.IsChecked = controller.Settings.FrenchEnabled;
        _rejectMusic.IsChecked = controller.Settings.RejectMusic;
        _azure.Text = controller.Settings.AzureKey;
        _region.Text = controller.Settings.AzureRegion;
        _locale.SelectedItem = controller.Settings.Locale;
        _stabilize.IsChecked = controller.Settings.Stabilize;
        _size.Value = controller.Settings.FontSize;
        _configurationPath.Text = controller.Settings.ConfigurationPath;
        _readingSpeed.Value = controller.Settings.ReadingWordsPerMinute;
        _readingLabel.Text = $"Reading speed: {controller.Settings.ReadingWordsPerMinute} words/minute";
        _googlePanel = new StackPanel { Spacing = 8, Children = { Label("Google caption mode"), _googleMode,
            _french, _googleDescription, _rejectMusic,
            new TextBlock { Text = "Off by default. Music-labeled audio can be captioned; pause manually when music plays. Change this while paused.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 },
            Label("Gemini API key"), _gemini } };
        _azurePanel = new StackPanel { Spacing = 8, Children = { Label("Azure Speech key"), _azure,
            Label("Azure region"), _region, Label("Swahili locale"), _locale, _stabilize } };
        _configuration = new StackPanel { Spacing = 14, Children = { Label("Translation service"), _provider,
            _googlePanel, _azurePanel } };
        var close = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        _clear.Click += (_, _) => controller.ClearCaptions();
        _fullscreen.Click += (_, _) => ToggleDisplayFullscreen?.Invoke();
        _demo.Click += async (_, _) => { await controller.RunDemoAsync(); };
        _pause.Click += async (_, _) =>
        {
            if (controller.IsPaused) await controller.ToggleAsync();
            else await controller.PauseAsync();
        };
        _openFile.Click += async (_, _) => await OpenConfigurationFileAsync();
        _reloadFile.Click += async (_, _) => await ReloadConfigurationAsync();
        _microphone.Click += async (_, _) => await CheckMicrophoneAsync();
        _audio.ItemTemplate = new FuncDataTemplate<AudioInput>((input, _) => new TextBlock { Text = input?.Name });
        _audio.ItemsSource = new[] { new AudioInput("", "System default") };
        _audio.SelectedIndex = 0;
        _audio.SelectionChanged += (_, _) =>
        {
            if (_updatingAudio || !_controller.IsPaused || _audio.SelectedItem is not AudioInput input) return;
            _controller.Settings.AudioDeviceId = input.Id;
            _controller.SettingsChanged();
            _audioAvailable = !input.Name.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
            UpdateAudioAvailability();
        };
        _refreshAudio.Click += async (_, _) => await RefreshAudioAsync();
        var commands = new StackPanel { Spacing = 10, Children =
        {
            Label("Commands"),
            new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children =
            {
                _activity,
                PlaceInColumn(_sessionStatus, 1)
            } },
            _pause, _demo, _clear, _fullscreen, _microphone
        } };
        var clearHistory = new Button { Content = "Clear diagnostic history", HorizontalAlignment = HorizontalAlignment.Right };
        clearHistory.Click += (_, _) => { _controller.Errors.Clear(); _controller.Events.Clear(); };
        var diagnostics = new StackPanel { Spacing = 8, Children =
        {
            Label("Diagnostics"),
            _latestIssue,
            _diagnostic,
            new ScrollViewer { Height = 155, Content = _diagnosticHistory },
            clearHistory
        } };
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(28), Spacing = 18,
            Children = { new TextBlock { Text = "Settings", FontSize = 28, FontWeight = FontWeight.SemiBold },
                commands, diagnostics,
                new Separator(), Label("Audio input"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _audio, _refreshAudio } },
                _audioAvailability,
                Label("Appearance"), _lightMode,
                _notice, _configuration,
                new TextBlock { Text = "Keys load from this local file on launch. Keys entered above stay in memory until the app closes.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 },
                _configurationPath,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _openFile, _reloadFile } },
                new Separator(), Label("Caption size"), _size,
                _readingLabel, _readingSpeed,
                new TextBlock { Text = "Text fills the window. This pace controls how soon older text can leave; a slower pace may delay captions behind the speaker.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 },
                new TextBlock { Text = "Commands stay here while the audience window displays captions. F11 toggles display fullscreen; Esc exits fullscreen.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 }, close } } };
        _lightMode.IsCheckedChanged += (_, _) =>
        {
            controller.Settings.Appearance = _lightMode.IsChecked == true ? AppAppearance.Light : AppAppearance.Dark;
            controller.SettingsChanged();
        };
        _provider.SelectionChanged += (_, _) => Save();
        _googleMode.SelectionChanged += (_, _) => Save();
        _french.IsCheckedChanged += (_, _) => Save();
        _rejectMusic.IsCheckedChanged += (_, _) => Save();
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
        controller.Errors.Changed += RefreshDiagnostics;
        controller.Events.Changed += RefreshDiagnostics;
        Opened += async (_, _) => await RefreshAudioAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            controller.Changed -= Refresh;
            controller.LocalConfigurationLoaded -= RefreshLocalConfiguration;
            controller.Errors.Changed -= RefreshDiagnostics;
            controller.Events.Changed -= RefreshDiagnostics;
            _microphoneCheck?.Cancel();
        };
        _diagnostic.Text = controller.Settings.ConfigurationError ?? "";
        RefreshDiagnostics();
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
            if (!_closed)
            {
                _diagnostic.Text = "The API file is still waiting for access. Check macOS file permissions or enter a key above, then try Reload again.";
                _controller.Errors.Add("Reading the local API file timed out. Check file permissions and try Reload again.");
            }
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
            _diagnostic.Text = "Microphone permission granted. Use Start captions above when ready.";
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
    private void RefreshDiagnostics()
    {
        if (_closed) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(RefreshDiagnostics); return; }
        var issues = _controller.Errors.Entries;
        _latestIssue.Text = issues.Count == 0 ? "Latest issue: none" : $"Latest issue: {issues[^1].Message}";
        var entries = issues.Select(entry => (entry.Time, entry.Message, Kind: "Issue"))
            .Concat(_controller.Events.Entries.Select(entry => (entry.Time, entry.Message, Kind: "Event")))
            .OrderByDescending(entry => entry.Time).Take(100).ToArray();
        _diagnosticHistory.Children.Clear();
        if (entries.Length == 0) _diagnosticHistory.Children.Add(new TextBlock { Text = "No events recorded yet.", Opacity = .65 });
        foreach (var entry in entries)
            _diagnosticHistory.Children.Add(new TextBlock
            {
                Text = $"{entry.Time.ToLocalTime():HH:mm:ss}  {entry.Kind} · {entry.Message}",
                TextWrapping = TextWrapping.Wrap, FontSize = 12
            });
    }
    private async Task RefreshAudioAsync()
    {
        if (_listingAudio) return;
        _listingAudio = true;
        _refreshAudio.IsEnabled = false;
        try
        {
            var inputs = (await MacAudioInputs.ListAsync()).ToList();
            if (_closed) return;
            _updatingAudio = true;
            var selected = inputs.FirstOrDefault(input => input.Id == _controller.Settings.AudioDeviceId);
            _audioAvailable = selected is not null;
            if (selected is null)
            {
                selected = new AudioInput(_controller.Settings.AudioDeviceId, "Selected input unavailable — choose another");
                inputs.Add(selected);
            }
            _audio.ItemsSource = inputs;
            _audio.SelectedItem = selected;
            UpdateAudioAvailability();
        }
        catch
        {
            if (_closed) return;
            _audioAvailable = false;
            _audioAvailability.Text = "Input availability could not be checked. Refresh inputs or check microphone permission.";
            _controller.Errors.Add("Could not list audio inputs. Check microphone permission and the audio helper.");
        }
        finally
        {
            _updatingAudio = false;
            _listingAudio = false;
            if (!_closed) { _refreshAudio.IsEnabled = true; Refresh(); }
        }
    }

    private void UpdateAudioAvailability()
    {
        var selected = _audio.SelectedItem as AudioInput;
        _audioAvailability.Text = _audioAvailable
            ? $"Selected: {selected?.Name ?? "System default"} · Available"
            : "Selected input unavailable. Pause captions and choose another input.";
    }
    private static T PlaceInColumn<T>(T control, int column) where T : Control
    { Grid.SetColumn(control, column); return control; }
    private void Save()
    {
        if (_loading || !_controller.IsPaused) return;
        var settings = _controller.Settings;
        if (_french.IsChecked == true && _googleMode.SelectedIndex != 0)
        {
            _loading = true;
            _googleMode.SelectedIndex = 0;
            _loading = false;
        }
        settings.Provider = _provider.SelectedIndex == 0 ? SpeechProvider.Google : SpeechProvider.Azure;
        settings.GoogleMode = _googleMode.SelectedIndex == 0 ? GoogleCaptionMode.TwoStage : GoogleCaptionMode.LiveTranslate;
        settings.FrenchEnabled = _french.IsChecked == true;
        settings.RejectMusic = _rejectMusic.IsChecked == true;
        settings.GeminiKey = _gemini.Text ?? "";
        settings.AzureKey = _azure.Text ?? "";
        settings.AzureRegion = _region.Text ?? "";
        settings.Locale = _locale.SelectedItem as string ?? "sw-KE";
        settings.Stabilize = _stabilize.IsChecked == true;
        _controller.SettingsChanged();
    }
    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Refresh); return; }
        _configuration.IsEnabled = _controller.IsPaused;
        _pause.Content = _controller.IsPaused ? "Start captions"
            : _controller.State == CaptionState.Pausing ? "Pausing…" : "Pause captions";
        _pause.IsEnabled = _controller.State != CaptionState.Pausing;
        _demo.IsEnabled = _controller.IsPaused && !_checkingMicrophone;
        _audio.IsEnabled = _controller.IsPaused && !_listingAudio;
        _activity.Text = _controller.AudioActivity == AudioActivity.Music && _controller.State == CaptionState.Live ? "♫"
            : _controller.State == CaptionState.Live ? "•••" : "·";
        Avalonia.Automation.AutomationProperties.SetName(_activity,
            _controller.AudioActivity == AudioActivity.Music && _controller.State == CaptionState.Live ? "Music detected"
            : _controller.State == CaptionState.Live ? "Transcription running" : "Transcription paused");
        _sessionStatus.Text = _controller.Status;
        _openFile.IsEnabled = _reloadFile.IsEnabled = _controller.IsPaused && !_checkingMicrophone && !_reloadingFile && !_openingFile;
        _microphone.IsEnabled = _controller.IsPaused && !_checkingMicrophone;
        _microphone.IsVisible = OperatingSystem.IsMacOS();
        _googlePanel.IsVisible = _provider.SelectedIndex == 0;
        _azurePanel.IsVisible = !_googlePanel.IsVisible;
        _googleMode.IsEnabled = _french.IsChecked != true;
        _googleDescription.Text = _french.IsChecked == true
            ? "Swahili speech is transcribed once and translated into English and French. English appears on the left and French on the right. This uses Google TwoStage and adds a Flash translation request for each fragment. Changing the caption languages clears existing captions."
            : "Two-stage mode translates stable Swahili fragments into English.";
        _notice.Text = _controller.IsPaused ? "Configure the service, then use Start captions above."
            : "Transcription is running. Pause to change the service or credentials.";
    }
}
