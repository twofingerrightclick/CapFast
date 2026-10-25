using System.Diagnostics;
using System.Globalization;
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

// Local capture is independent of cloud transcription and never uploads audio.
public sealed class AudioMonitor : IDisposable
{
    private readonly CaptionController _controller;
    private readonly Window _owner;
    private readonly ProgressBar _meter = new() { Minimum = -60, Maximum = 0, Value = -60, Height = 8 };
    private readonly TextBlock _level = new() { Text = "Input · paused", FontSize = 12 };
    private readonly Button _open = new() { Content = "Monitor", VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Process? _process;
    private CancellationTokenSource? _windowsCancellation;
    private Task _windowsTask = Task.CompletedTask;
    private Window? _dialog;
    private Button? _listen;
    private Button? _refreshOutputs;
    private bool _loadingOutputs;
    private ComboBox? _outputs;
    private TextBlock? _status;
    private string? _configuration;
    private string _output = "";
    private bool _listening;
    private bool _disposed;
    private bool _ready;
    private double _db = -60;
    private AudioMonitorHealth? _health;
    private string? _error;
    public Control View { get; }

    public AudioMonitor(CaptionController controller, Window owner)
    {
        _controller = controller; _owner = owner;
        var levels = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { _level, _meter } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14, Children = { levels, _open } };
        Grid.SetColumn(_open, 1);
        Avalonia.Automation.AutomationProperties.SetName(_meter, "Input audio level");
        _open.IsEnabled = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();
        _open.Click += async (_, _) => await OpenAsync();
        View = row;
        _timer.Tick += (_, _) => Update();
        owner.Opened += (_, _) => _timer.Start();
    }

    private void Update()
    {
        if (_disposed) return;
        var active = (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) && (_controller.HasActiveAudioSession || _dialog is not null);
        var configuration = active ? $"{_controller.Settings.AudioDeviceId}\n{_output}\n{_listening}" : null;
        if (configuration != _configuration)
        {
            Stop(); _configuration = configuration; _error = null;
            if (active) Start();
        }
        if ((_process is not null || _windowsCancellation is not null) && _health?.HasTimedOut(Environment.TickCount64) == true)
        {
            Stop(); _listening = false;
            _configuration = $"{_controller.Settings.AudioDeviceId}\n{_output}\nFalse";
            _error = "No input audio received. Check your devices, then click Listen to retry.";
            _controller.Errors.Add("Audio monitor timed out waiting for input audio. Check the selected device and microphone permission.");
        }
        var fresh = _health?.HasFreshLevel(Environment.TickCount64) == true;
        _meter.Value = fresh ? Volatile.Read(ref _db) : -60;
        _meter.Foreground = new SolidColorBrush(_meter.Value >= -1 ? Colors.OrangeRed : Colors.MediumSeaGreen);
        _level.Text = _error is not null ? "Input · unavailable" : !active ? "Input · paused" : !fresh ? "Input · connecting…"
            : _meter.Value <= -60 ? "Input · silent" : $"Input · {_meter.Value:0} dBFS";
        ToolTip.SetTip(_level, _error ?? "Peak input level before speech detection");
        if (_listen is not null) _listen.Content = _listening ? "■ Stop listening" : "▶ Listen";
        if (_outputs is not null) _outputs.IsEnabled = !_listening && !_loadingOutputs;
        if (_refreshOutputs is not null) _refreshOutputs.IsEnabled = !_listening && !_loadingOutputs;
        if (_status is not null) _status.Text = _error ?? (_listening ? (fresh ? "Listening through the selected output." : "Starting audio… You can stop listening at any time.") : "Listening is off. Audio stays on this computer.");
    }

    private void Start()
    {
        if (OperatingSystem.IsWindows()) { StartWindows(); return; }
        try
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CapFast.AudioCapture"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
            info.ArgumentList.Add("--monitor"); info.ArgumentList.Add(_controller.Settings.AudioDeviceId ?? ""); info.ArgumentList.Add(_output);
            if (_listening) info.ArgumentList.Add("--listen");
            var process = new Process { StartInfo = info };
            _process = process;
            _health = new AudioMonitorHealth(Environment.TickCount64);
            if (!process.Start()) throw new IOException();
            _ = ReadAsync(process);
        }
        catch
        {
            Stop(); _listening = false;
            _configuration = $"{_controller.Settings.AudioDeviceId}\n{_output}\nFalse";
            _error = "Audio monitor could not start. Launch the packaged Mac app and check microphone permission.";
        }
    }

    private void StartWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _windowsCancellation = cancellation;
        _health = new AudioMonitorHealth(Environment.TickCount64);
        var previous = _windowsTask;
        var input = _controller.Settings.AudioDeviceId ?? "";
        var output = _output;
        var listen = _listening;
        void Dispatch(Action action) => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && ReferenceEquals(_windowsCancellation, cancellation) && !token.IsCancellationRequested) action();
        });
        _windowsTask = Task.Run(async () =>
        {
            try
            {
                // A rapid toggle cannot open a second endpoint before teardown finishes.
                await previous;
                token.ThrowIfCancellationRequested();
                if (!OperatingSystem.IsWindows()) return;
                await WindowsAudioMonitor.RunAsync(input, output, listen,
                    () => Dispatch(() => _health?.Ready(Environment.TickCount64)),
                    db => Dispatch(() => { _db = db; _health?.ObserveLevel(Environment.TickCount64); }), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Dispatch(() =>
                {
                    _controller.Errors.Add($"Windows audio monitor failed (HRESULT 0x{exception.HResult:X8}): {exception}");
                    Stop(); _listening = false;
                    _configuration = $"{_controller.Settings.AudioDeviceId}\n{_output}\nFalse";
                    _error = "Monitoring stopped. Check your input/output devices and Windows microphone privacy settings, then click Listen to retry.";
                    Update();
                });
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_windowsCancellation, cancellation)) _windowsCancellation = null;
                    cancellation.Dispose();
                });
            }
        });
    }

    private async Task ReadAsync(Process process)
    {
        try
        {
            var errors = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!ReferenceEquals(_process, process)) return;
                if (line == "MONITOR_READY") { _health?.Ready(Environment.TickCount64); continue; }
                if (!double.TryParse(line, CultureInfo.InvariantCulture, out var db) || !double.IsFinite(db)) continue;
                Volatile.Write(ref _db, Math.Clamp(db, -60, 0));
                _health?.ObserveLevel(Environment.TickCount64);
            }
            await process.WaitForExitAsync();
            var code = await errors;
            var exitCode = process.ExitCode;
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_process, process) || _disposed) return;
                _controller.Errors.Add($"Mac audio monitor exited ({exitCode}): {code.Trim()}");
                Stop(); _listening = false;
                _configuration = $"{_controller.Settings.AudioDeviceId}\n{_output}\nFalse";
                _error = code.Contains("PERMISSION") ? "Allow microphone access in System Settings → Privacy & Security → Microphone."
                    : "Monitoring stopped. Check your input/output devices, then click Listen to retry.";
                Update();
            });
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_process, process) || _disposed) return;
                Stop(); _listening = false;
                _configuration = $"{_controller.Settings.AudioDeviceId}\n{_output}\nFalse";
                _error = "Audio monitor disconnected. Check your devices, then click Listen to retry.";
            });
        }
    }

    private async Task OpenAsync()
    {
        if (_dialog is not null) { _dialog.Activate(); return; }
        _listen = new Button { Content = "▶ Listen", MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = false };
        _outputs = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemTemplate = new FuncDataTemplate<AudioInput>((item, _) => new TextBlock { Text = item?.Name }) };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var refresh = new Button { Content = "Refresh outputs" };
        _refreshOutputs = refresh;
        var dialog = new Window { Title = "Audio monitor", Width = 400, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(22), Spacing = 14, Children = {
                new TextBlock { Text = "Audio monitor", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Use headphones to avoid microphone feedback.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Audio output" }, _outputs, refresh, _listen, _status } } };
        _dialog = dialog;
        async Task LoadOutputs()
        {
            _loadingOutputs = true;
            _ready = false; _listen!.IsEnabled = false; refresh.IsEnabled = false;
            try
            {
                var outputs = await AudioDevices.ListOutputsAsync();
                if (_dialog != dialog) return;
                _outputs!.ItemsSource = outputs;
                _outputs.SelectedItem = outputs.FirstOrDefault(x => x.Id == _output) ?? outputs[0];
                _ready = true; _listen.IsEnabled = true;
            }
            catch { if (_dialog == dialog) _error = "Could not list outputs. Check connected devices and refresh outputs."; }
            finally { if (_dialog == dialog) _loadingOutputs = false; refresh.IsEnabled = true; }
        }
        _outputs.SelectionChanged += (_, _) => { if (_outputs?.SelectedItem is AudioInput input) _output = input.Id; };
        _listen.Click += (_, _) => { if (!_ready) return; _listening = !_listening; _configuration = null; Update(); refresh.IsEnabled = !_listening; };
        refresh.Click += async (_, _) => await LoadOutputs();
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };
        dialog.Closed += (_, _) =>
        {
            Stop(); _listening = false; _dialog = null; _listen = null; _outputs = null; _status = null; _refreshOutputs = null; _loadingOutputs = false; _configuration = null; Update();
        };
        _configuration = null; Update();
        dialog.Opened += async (_, _) => await LoadOutputs();
        await dialog.ShowDialog(_owner);
    }

    private void Stop()
    {
        var windows = _windowsCancellation; _windowsCancellation = null;
        windows?.Cancel();
        var process = _process; _process = null;
        _health = null;
        if (process is null) return;
        try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(); } catch { }
        process.Dispose();
    }

    public void Dispose() { _disposed = true; _timer.Stop(); _dialog?.Close(); Stop(); }
}
