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
    private readonly TextBox _gemini = new() { IsVisible = false, PasswordChar = '●' };
    private readonly SavedGoogleKeyStore _savedGoogleKey = new();
    private readonly Button _saveKey = new() { Content = "Save API key" };
    private readonly Button _removeKey = new() { Content = "Remove saved key" };
    private readonly TextBlock _keyStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private bool _persistingKey;
    private readonly TextBlock _googleDescription = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 };
    private readonly Slider _size = new() { Minimum = 28, Maximum = 72 };
    private readonly Slider _readingSpeed = new() { Minimum = 80, Maximum = 300, TickFrequency = 10, IsSnapToTickEnabled = true };
    private readonly CheckBox _catchUp = new() { Content = "Automatic caption catch-up" };
    private readonly TextBlock _displayQueue = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly DispatcherTimer _displayQueueTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBlock _readingLabel = new() { FontWeight = FontWeight.Medium };
    private readonly SelectableTextBlock _configurationPath = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Opacity = .75 };
    private readonly StackPanel _googlePanel;
    private readonly Button _demo = new() { Content = "Preview sample captions", Padding = new Thickness(10, 5),
        VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause = new() { Content = "▶ Start captions", HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        MinHeight = 72 };
    private readonly Button _resetLanguages = CommandRowButton("Reset output languages");

    private readonly StackPanel _languageConsole = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Dictionary<string, Button> _languageButtons = new();
    private readonly TextBlock _languageStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _clear = new() { Content = "Clear captions" };
    private readonly Button _fullscreen = new() { Content = "Fullscreen", Padding = new Thickness(10, 5),
        VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _audio = new() { MinWidth = 250 };
    private readonly Button _refreshAudio = new() { Content = "Refresh inputs" };
    private readonly TextBlock _audioAvailability = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly DispatcherTimer _preferencesSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly TextBlock _activity = new() { FontSize = 25, FontWeight = FontWeight.Bold, Width = 40,
        Foreground = new SolidColorBrush(Color.Parse("#59B8FF")) };
    private readonly TextBlock _sessionStatus = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private readonly Button _openFile = new() { Content = "Open API key file" };
    private readonly Button _reloadFile = new() { Content = "Reload file" };
    private readonly Button _microphone = new() { Content = "Check microphone permission" };
    private readonly TextBlock _diagnostic = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _diagnosticState = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _latestIssue = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium };
    private readonly StackPanel _diagnosticHistory = new() { Spacing = 7 };
    private bool _checkingMicrophone;
    private bool _updatingAudio;
    private bool _listingAudio;
    private bool _audioAvailable = true;
    private bool _loading;
    private bool _preferencesDirty;
    private bool _closed;
    private bool _reloadingFile;
    private Task<LocalConfiguration>? _reloadRead;
    private long _reloadRevision;
    private string? _reloadPath;
    private bool _openingFile;
    private Task? _prepareFile;
    private string? _preparePath;
    private CancellationTokenSource? _microphoneCheck;
    public Action? ToggleDisplayFullscreen { get; set; }

    public SettingsWindow(CaptionController controller, CaptionWindow audience)
    {
        _controller = controller;
        Title = "CapFast — Settings";
        Width = 1400; Height = 940; MinWidth = 1050; MinHeight = 740;
        _lightMode.IsChecked = controller.Settings.Appearance == AppAppearance.Light;
        _gemini.Text = controller.Settings.GeminiKey;
        _size.Value = controller.Settings.FontSize;
        _configurationPath.Text = controller.Settings.ConfigurationPath;
        _readingSpeed.Value = controller.Settings.ReadingWordsPerMinute;
        _readingLabel.Text = $"Default reading speed: {controller.Settings.ReadingWordsPerMinute} words/minute";
        _catchUp.IsChecked = controller.Settings.AutomaticCaptionCatchUp;
        var languageSpeeds = new StackPanel { Spacing = 12 };
        languageSpeeds.Children.Add(LanguageSpeedControl("English", () => controller.Settings.EnglishReadingWordsPerMinute,
            value => controller.Settings.EnglishReadingWordsPerMinute = value));
        languageSpeeds.Children.Add(LanguageSpeedControl("Swahili", () => controller.Settings.SwahiliReadingWordsPerMinute,
            value => controller.Settings.SwahiliReadingWordsPerMinute = value));
        languageSpeeds.Children.Add(LanguageSpeedControl("French", () => controller.Settings.FrenchReadingWordsPerMinute,
            value => controller.Settings.FrenchReadingWordsPerMinute = value));
        var editGoogleKey = new Button { Content = "Edit Google API key" };
        editGoogleKey.Click += (_, _) => _gemini.IsVisible = !_gemini.IsVisible;
        _googlePanel = new StackPanel { Spacing = 8, Children = {
            Label("Automatic transcription → two output languages"), _googleDescription,
            new TextBlock { Text = "Pause captions when music plays. Resume when speech begins.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 },
            Label("Gemini API key"), editGoogleKey, _gemini } };
        if (OperatingSystem.IsWindows())
        {
            _googlePanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { _saveKey, _removeKey } });
            _keyStatus.Text = "Save API key keeps this key encrypted for your Windows account. Unsaved edits last until the app closes." + EnvironmentKeyNotice;
            Avalonia.Automation.AutomationProperties.SetName(_gemini, "Gemini API key");
            Avalonia.Automation.AutomationProperties.SetLiveSetting(_keyStatus, Avalonia.Automation.AutomationLiveSetting.Polite);
            _googlePanel.Children.Add(_keyStatus);
        }
        _saveKey.Click += async (_, _) => await PersistGoogleKeyAsync(remove: false);
        _removeKey.Click += async (_, _) => await PersistGoogleKeyAsync(remove: true);
        _configuration = new StackPanel { Spacing = 14, Children = { _googlePanel } };
        foreach (var code in new[] { "en", "sw", "fr" })
        {
            var language = code;
            var button = new Button { MinWidth = 0, Padding = new Thickness(8, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => controller.HintInputLanguage(button.Tag as string ?? language);
            _languageButtons.Add(code, button); _languageConsole.Children.Add(button);
        }
        _resetLanguages.Click += (_, _) => controller.ResetLanguageDetection();
        _clear.Click += (_, _) => controller.ClearCaptions();
        _fullscreen.Click += (_, _) => ToggleDisplayFullscreen?.Invoke();
        _demo.Click += async (_, _) => { await controller.RunDemoAsync(); };
        _pause.Click += async (_, _) =>
        {
            if (_listingAudio) return;
            if (controller.IsPaused)
            {
                // Capture the visible choice before starting disables the selector.
                if (_audio.SelectedItem is AudioInput selected)
                {
                    if (controller.Settings.AudioDeviceId != selected.Id)
                    {
                        controller.Settings.AudioDeviceId = selected.Id;
                        SchedulePreferencesSave();
                        SavePreferences();
                    }
                }
                await controller.ToggleAsync();
            }
            else await controller.PauseAsync();
        };
        _openFile.Click += async (_, _) => await OpenConfigurationFileAsync();
        _reloadFile.Click += async (_, _) => await ReloadConfigurationAsync();
        _microphone.Click += async (_, _) => await CheckMicrophoneAsync();
        _audio.ItemTemplate = new FuncDataTemplate<AudioInput>((input, _) => new TextBlock { Text = input?.Name });
        var initialInputs = new List<AudioInput> { new("", "System default") };
        if (!string.IsNullOrEmpty(controller.Settings.AudioDeviceId))
            initialInputs.Add(new(controller.Settings.AudioDeviceId, "Saved input — checking availability"));
        _audio.ItemsSource = initialInputs;
        _audio.SelectedIndex = initialInputs.Count - 1;
        _audio.SelectionChanged += (_, _) =>
        {
            if (_updatingAudio || _audio.SelectedItem is not AudioInput input) return;
            if (!_controller.IsPaused)
            {
                RestoreAudioSelection();
                return;
            }
            _controller.Settings.AudioDeviceId = input.Id;
            _controller.SettingsChanged();
            SchedulePreferencesSave();
            SavePreferences();
            _audioAvailable = !input.Name.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
            UpdateAudioAvailability();
        };
        _refreshAudio.Click += async (_, _) => await RefreshAudioAsync();
        var clearHistory = new Button { Content = "Clear diagnostic history", HorizontalAlignment = HorizontalAlignment.Right };
        clearHistory.Click += (_, _) => { _controller.Errors.Clear(); _controller.Events.Clear(); };
        var diagnostics = new StackPanel { Spacing = 8, Children =
        {
            Label("Diagnostics"),
            _diagnosticState,
            _latestIssue,
            _diagnostic,
            new ScrollViewer { Height = 155, Content = _diagnosticHistory },
            clearHistory
        } };
        var slideshow = new SlideshowControls(controller.Slideshow);
        var transcriptionPreview = audience.CreatePreview(transcriptionOnly: true);
        var livePreview = audience.CreatePreview(transcriptionOnly: false);
        var transcription = new Button
        {
            Content = transcriptionPreview, Padding = new Thickness(3), BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = 190
        };
        Avalonia.Automation.AutomationProperties.SetName(transcription, "Show transcription on audience screen");
        ToolTip.SetTip(transcription, "Click to show transcription · Enter or Space when focused");
        transcription.Click += (_, _) => controller.Slideshow.SetShowing(false);
        var showing = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Medium };
        void UpdateSource()
        {
            SlideshowControls.SetOutputState(transcription, "Transcription", !controller.Slideshow.IsShowing);
            showing.Text = controller.Slideshow.IsShowing ? "Live output (Slideshow)" : "Live output (Transcription)";
        }
        controller.Slideshow.Changed += UpdateSource;
        UpdateSource();
        var pacing = new Expander { Header = "Reading speed & pacing", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = {
                _readingLabel, _readingSpeed, languageSpeeds, _catchUp, _displayQueue,
                new TextBlock { Text = "Reading speeds control display pacing. Catch-up speeds up delayed columns and may skip older queued text.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 }
            } } };
        var audioMonitor = new AudioMonitor(controller, this);
        var commands = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 16, 0), Children = {
            Label("Transcription"), transcription, audioMonitor.View,
            new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = {
                _activity, PlaceInColumn(_sessionStatus, 1) } },
            _languageConsole, _languageStatus, _resetLanguages, _pause, _clear,
            Label("Caption size"), _size, pacing
        } };
        var outputActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _demo, _fullscreen } };
        var outputStatus = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12,
            Children = { showing, PlaceInColumn(outputActions, 1) } };
        ToolTip.SetTip(_fullscreen, "Toggle display fullscreen");
        Avalonia.Automation.AutomationProperties.SetName(_fullscreen, "Toggle display fullscreen");
        var output = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10,
            Children = { outputStatus, livePreview, slideshow } };
        Grid.SetRow(livePreview, 1); Grid.SetRow(slideshow, 2);
        var transcriptionColumn = new ScrollViewer { Content = commands,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*"), ColumnSpacing = 28,
            Children = { transcriptionColumn, PlaceInColumn(output, 1) } };
        var configuration = new Expander { Header = "Audio, appearance & configuration", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 12, Children = {
                Label("Audio input"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _audio, _refreshAudio } },
                _audioAvailability, _microphone, Label("Appearance"), _lightMode,
                _notice, _configuration,
                new TextBlock { Text = OperatingSystem.IsWindows()
                    ? "This local file is a plaintext fallback, used when no environment variable or saved key is available. Save API key above stores an encrypted key instead."
                    : "Keys load from this local file on launch. Keys entered above stay in memory until the app closes.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 },
                _configurationPath,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _openFile, _reloadFile } }
            } } };
        var secondary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 28,
            Children = { configuration, PlaceInColumn(new Expander { Header = "Diagnostics", Content = diagnostics,
                HorizontalAlignment = HorizontalAlignment.Stretch }, 1) } };
        var footer = new Border { Background = new SolidColorBrush(Color.Parse("#0FFFFFFF")),
            Padding = new Thickness(12), Child = secondary };
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 18,
            Children = { main, footer } };
        Grid.SetRow(footer, 1);
        // Expanded configuration remains scrollable without squeezing the daily controls away.
        var configurationContent = configuration.Content;
        configuration.Content = null;
        configuration.Content = new ScrollViewer { MaxHeight = 300, Content = configurationContent };
        Content = layout;
        _lightMode.IsCheckedChanged += (_, _) =>
        {
            controller.Settings.Appearance = _lightMode.IsChecked == true ? AppAppearance.Light : AppAppearance.Dark;
            controller.SettingsChanged();
        };
        _gemini.TextChanged += (_, _) => Save();
        _size.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            controller.Settings.FontSize = _size.Value;
            controller.SettingsChanged();
            SchedulePreferencesSave();
        };
        _readingSpeed.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            controller.Settings.ReadingWordsPerMinute = (int)Math.Round(_readingSpeed.Value);
            _readingLabel.Text = $"Default reading speed: {controller.Settings.ReadingWordsPerMinute} words/minute";
            controller.SettingsChanged();
            SchedulePreferencesSave();
        };
        _catchUp.IsCheckedChanged += (_, _) =>
        {
            controller.Settings.AutomaticCaptionCatchUp = _catchUp.IsChecked == true;
            controller.SettingsChanged();
            SchedulePreferencesSave();
        };
        void UpdateDisplayQueue() => _displayQueue.Text = controller.DisplayQueueStatus?.Invoke() ?? "Display queue is empty.";
        _displayQueueTimer.Tick += (_, _) => UpdateDisplayQueue();
        Opened += (_, _) => { UpdateDisplayQueue(); _displayQueueTimer.Start(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        controller.Changed += Refresh;
        controller.LocalConfigurationLoaded += RefreshLocalConfiguration;
        controller.Errors.Changed += RefreshDiagnostics;
        controller.Events.Changed += RefreshDiagnostics;
        _preferencesSaveTimer.Tick += (_, _) =>
        {
            _preferencesSaveTimer.Stop();
            SavePreferences();
        };
        Opened += async (_, _) => await RefreshAudioAsync();
        Closed += (_, _) =>
        {
            audioMonitor.Dispose();
            slideshow.Dispose();
            transcriptionPreview.Dispose(); livePreview.Dispose();
            controller.Slideshow.Changed -= UpdateSource;
            _preferencesSaveTimer.Stop();
            _displayQueueTimer.Stop();
            SavePreferences();
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

    private Control LanguageSpeedControl(string language, Func<int?> get, Action<int?> set)
    {
        var useDefault = new CheckBox { Content = $"{language}: use default speed", IsChecked = get() is null };
        var speed = new Slider { Minimum = 80, Maximum = 300, TickFrequency = 10, IsSnapToTickEnabled = true,
            Value = get() ?? _controller.Settings.ReadingWordsPerMinute, IsEnabled = get() is not null };
        var label = new TextBlock { Text = $"{language}: {speed.Value:0} words/minute", IsVisible = get() is not null };
        useDefault.IsCheckedChanged += (_, _) =>
        {
            var inherit = useDefault.IsChecked == true;
            if (!inherit && get() is null) speed.Value = _controller.Settings.ReadingWordsPerMinute;
            label.Text = $"{language}: {speed.Value:0} words/minute";
            speed.IsEnabled = label.IsVisible = !inherit;
            set(inherit ? null : (int)Math.Round(speed.Value));
            _controller.SettingsChanged(); SchedulePreferencesSave();
        };
        speed.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || useDefault.IsChecked == true) return;
            set((int)Math.Round(speed.Value));
            label.Text = $"{language}: {speed.Value:0} words/minute";
            _controller.SettingsChanged(); SchedulePreferencesSave();
        };
        return new StackPanel { Spacing = 6, Children = { useDefault, label, speed } };
    }

    private void RefreshLocalConfiguration()
    {
        if (_closed) return;
        _loading = true;
        _gemini.Text = _controller.Settings.GeminiKey;
        _configurationPath.Text = _controller.Settings.ConfigurationPath;
        _diagnostic.Text = _controller.Settings.ConfigurationError ?? "";
        if (OperatingSystem.IsWindows() && !_persistingKey && _controller.Settings.IsGoogleKeyFromConfiguration)
            _keyStatus.Text = (_controller.Settings.GeminiKey.Length > 0
                ? "API key loaded from configuration. Save API key stores an encrypted copy for your Windows account."
                : "No API key is configured. Enter your key, then click Save API key to keep it for future launches.") + EnvironmentKeyNotice;
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
        var originalRevision = settings.GoogleKeyRevision;
        var path = settings.ConfigurationPath;
        Refresh();
        _diagnostic.Text = "Reading local API configuration…";
        try
        {
            // Reuse a blocked read rather than starting another on each click.
            if (_reloadRead is null || _reloadRead.IsCompleted || _reloadPath != path)
            { _reloadPath = path; _reloadRevision = originalRevision; _reloadRead = Task.Run(() => new LocalConfiguration(path)); }
            var local = await _reloadRead.WaitAsync(TimeSpan.FromSeconds(5));
            if (_closed) return;
            if (local.Error is { } error)
            {
                // A broken encrypted key can still leave a usable environment/.env
                // fallback. Keep the current key if no usable fallback was found.
                if (local.GoogleKey.Length > 0)
                {
                    settings.ApplyLocalConfiguration(local, originalGoogle, _reloadRevision);
                    _controller.NotifyLocalConfigurationLoaded();
                }
                _diagnostic.Text = error; _controller.Errors.Add(error); return;
            }
            settings.ApplyLocalConfiguration(local, originalGoogle, _reloadRevision);
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

    private static string EnvironmentKeyNotice => LocalConfiguration.HasEnvironmentGoogleKey
        ? " An environment API key is set; it takes priority over saved keys on startup and Reload file." : "";

    private async Task PersistGoogleKeyAsync(bool remove)
    {
        if (!OperatingSystem.IsWindows() || !_controller.IsPaused || _persistingKey || _reloadingFile) return;
        _persistingKey = true;
        var settings = _controller.Settings;
        settings.PreserveCurrentGoogleKey();
        var key = settings.GeminiKey;
        _keyStatus.Text = remove ? "Removing saved API key…" : "Saving encrypted API key…";
        Refresh();
        try
        {
            var error = await Task.Run(() => remove ? _savedGoogleKey.Remove() : _savedGoogleKey.Save(key));
            if (_closed) return;
            if (error is not null)
            {
                _keyStatus.Text = error;
                _controller.Errors.Add(error);
            }
            else _keyStatus.Text = (remove
                ? "Saved API key removed. The current key stays in memory until you close CapFast. Environment variables and the local file are still available on restart."
                : "API key saved, encrypted for your Windows account. It will load on your next launch.") + EnvironmentKeyNotice;
        }
        catch
        {
            if (!_closed)
            {
                const string error = "Could not update the saved API key. Check access to your CapFast application data folder and try again. Your current key remains in memory.";
                _keyStatus.Text = error;
                _controller.Errors.Add(error);
            }
        }
        finally { _persistingKey = false; if (!_closed) Refresh(); }
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
    private static Button CommandRowButton(string label)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = label, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 56,
            Padding = new Thickness(8)
        };
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
    private void RefreshDiagnostics()
    {
        if (_closed) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(RefreshDiagnostics); return; }
        var issues = _controller.Errors.Entries;
        _latestIssue.Text = issues.Count == 0 ? "Recorded issues: none" : $"Most recent recorded issue: {issues[^1].Message}";
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
        Refresh();
        try
        {
            var inputs = (await AudioDevices.ListInputsAsync()).ToList();
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
    private void SchedulePreferencesSave()
    {
        _preferencesDirty = true;
        _preferencesSaveTimer.Stop();
        _preferencesSaveTimer.Start();
    }
    private void SavePreferences()
    {
        _preferencesSaveTimer.Stop();
        if (!_preferencesDirty) return;
        try
        {
            CaptionPreferences.Save(_controller.Settings);
            _preferencesDirty = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _controller.Errors.Add("Could not save audio and caption preferences. Check access to the CapFast application data folder."); }
    }
    private void RestoreAudioSelection()
    {
        var selected = _audio.ItemsSource?.OfType<AudioInput>()
            .FirstOrDefault(input => input.Id == _controller.Settings.AudioDeviceId);
        if (selected is null || ReferenceEquals(_audio.SelectedItem, selected)) return;
        _updatingAudio = true;
        try { _audio.SelectedItem = selected; }
        finally { _updatingAudio = false; }
        UpdateAudioAvailability();
    }
    private static T PlaceInColumn<T>(T control, int column) where T : Control
    { Grid.SetColumn(control, column); return control; }
    private void Save()
    {
        if (_loading || !_controller.IsPaused) return;
        var settings = _controller.Settings;
        settings.GeminiKey = _gemini.Text ?? "";
        if (OperatingSystem.IsWindows())
            _keyStatus.Text = "Changes are in memory. Click Save API key to keep them for your next launch." + EnvironmentKeyNotice;
        _controller.SettingsChanged();
    }
    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Refresh); return; }
        _configuration.IsEnabled = _controller.IsPaused;
        _pause.Content = _controller.IsPaused ? "▶ Start captions"
            : _controller.State == CaptionState.Pausing ? "⏸ Pausing…" : "⏸ Pause captions";
        Avalonia.Automation.AutomationProperties.SetName(_pause,
            _controller.IsPaused ? "Start captions" : _controller.State == CaptionState.Pausing ? "Pausing captions" : "Pause captions");
        _pause.IsEnabled = _controller.State != CaptionState.Pausing && !_listingAudio;
        _demo.IsEnabled = _controller.IsPaused && !_checkingMicrophone;
        _audio.IsEnabled = _controller.IsPaused && !_listingAudio;
        if (!_controller.IsPaused && !_updatingAudio) RestoreAudioSelection();
        _activity.Text = _controller.AudioActivity == AudioActivity.Speech && _controller.State == CaptionState.Live ? "•••" : "·";
        Avalonia.Automation.AutomationProperties.SetName(_activity,
            _controller.State == CaptionState.Live ? (_controller.AudioActivity == AudioActivity.Speech ? "Speech detected" : "Listening — no speech") : "Transcription paused");
        _sessionStatus.Text = _controller.State == CaptionState.Live
            ? $"{LanguageSurfaceRouting.Status(_controller.LanguageAssignment)} · {_controller.Status}" : _controller.Status;
        _diagnosticState.Text = CommandCenterStatus.Describe(_controller.State, _controller.AudioActivity, _controller.Status);
        var assignment = _controller.LanguageAssignment;
        _resetLanguages.IsEnabled = _controller.CanSetLanguageAssignment;
        _languageStatus.Text = assignment is null ? "Detecting · Auto checks each speech segment. Click a language to hint the next utterance."
            : $"Mostly {LanguageSurfaceRouting.Label(assignment.Input)} · Each phrase uses its own translation direction. Click a language to hint the next utterance.";
        var consoleLanguages = assignment is null ? new[] { "en", "sw", "fr" }
            : new[] { assignment.Input, assignment.Primary, assignment.Secondary };
        var consoleIndex = 0;
        foreach (var pair in _languageButtons)
        {
            var code = consoleLanguages[consoleIndex++];
            pair.Value.Tag = code;
            var input = assignment?.Input == code;
            var output = assignment is not null && (assignment.Primary == code || assignment.Secondary == code);
            var role = input ? "MAIN INPUT" : output ? "OUTPUT" : "DETECTING";
            pair.Value.Content = $"{LanguageSurfaceRouting.Label(code)}\n{role}";
            pair.Value.Foreground = new SolidColorBrush(Color.Parse(input ? "#FF7979" : output ? "#73DFA1" : "#A6A6A6"));
            pair.Value.BorderBrush = pair.Value.Foreground;
            pair.Value.IsEnabled = _controller.CanSetLanguageAssignment;
            Avalonia.Automation.AutomationProperties.SetName(pair.Value, $"{LanguageSurfaceRouting.Label(code)} {role.ToLowerInvariant()} — hint input language");
        }
        _openFile.IsEnabled = _reloadFile.IsEnabled = _controller.IsPaused && !_checkingMicrophone && !_reloadingFile && !_openingFile && !_persistingKey;
        _saveKey.IsEnabled = _removeKey.IsEnabled = !_persistingKey && !_reloadingFile;
        _gemini.IsEnabled = !_persistingKey;
        _microphone.IsEnabled = _controller.IsPaused && !_checkingMicrophone;
        _microphone.IsVisible = OperatingSystem.IsMacOS();
        _googleDescription.Text = "Every run starts in Auto. Each speech fragment is translated in its own language; the input indicator follows sustained language changes. English and Swahili captions share the left column; French stays on the right. Pending captions stay queued during slow requests. If translation cannot recover, capture pauses with an issue. Five seconds without speech returns to Detecting. A language click hints the next utterance.";
        _notice.Text = _controller.IsPaused ? "Configure your Google key, then use Start captions above."
            : _controller.State == CaptionState.Live ? "Transcription is running. Pause to change your Google credentials."
            : "Caption service is connecting. Pause to change your Google credentials.";
    }
}
