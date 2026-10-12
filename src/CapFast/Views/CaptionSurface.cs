using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CapFast.Core;

namespace CapFast.Views;

// Each language retains the same bounded, paced presentation and animation.
internal sealed class CaptionSurface : Border
{
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 48,
        FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.NoWrap, MaxLines = 3,
        TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly CaptionPresentation _presentation = new();
    private readonly Canvas _captionCanvas = new() { ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top };
    private readonly System.Diagnostics.Stopwatch _presentationClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<TextBlock> _rowLabels = new();
    private readonly CaptionWordReveal _wordReveal = new();
    private readonly List<(CaptionWordReveal.Word Word, SolidColorBrush Brush)> _wordBrushes = new();
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly System.Diagnostics.Stopwatch _fadeClock = new();
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly System.Diagnostics.Stopwatch _scrollClock = new();
    private string[]? _scrollTarget;
    private string _geometry = "";
    private int _scrollRows;
    private int _presentationGeneration;
    private bool _waitingForReveal;
    private int _readingWordsPerMinute = 180;
    private Color _captionColor = Colors.White;
    public string QueueStatus => $"{_presentation.DisplayDelaySeconds(_presentationClock.Elapsed.TotalSeconds):0.0}s display wait"
        + (_presentation.IsCatchingUp ? " · catching up" : "")
        + (_presentation.SkippedPhrases > 0 ? $" · {_presentation.SkippedPhrases} older fragments skipped" : "");
    public bool CanAcceptCaptions => _presentation.CanAcceptCaptions;
    public bool HasUnreadCaptions => _presentation.HasUnreadCaptions(_presentationClock.Elapsed.TotalSeconds);

    public CaptionSurface()
    {
        Child = _captionCanvas;
        ClipToBounds = true;
        _fadeTimer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(_fadeClock.Elapsed.TotalMilliseconds / 800, 0, 1);
            _captionCanvas.Opacity = 1 - progress * progress * (3 - 2 * progress);
            if (progress >= 1) ClearCaption();
        };
        _scrollTimer.Tick += (_, _) => AnimateScroll();
        SizeChanged += (_, _) => FitCaption();
    }

    public void Configure(CaptionSettings settings)
    {
        _text.FontSize = settings.FontSize;
        _readingWordsPerMinute = settings.ReadingWordsPerMinute;
        _presentation.CatchUpEnabled = settings.AutomaticCaptionCatchUp;
        _presentation.ReadingSpeedForLanguage = settings.ReadingSpeedFor;
        FitCaption();
    }

    public void SetCaptionColor(Color color)
    {
        _captionColor = color;
        _text.Foreground = new SolidColorBrush(color);
        foreach (var row in _rowLabels) row.Foreground = _text.Foreground;
        foreach (var (_, brush) in _wordBrushes) brush.Color = color;
    }

    public void Stop()
    {
        ClearCaption();
        _rowLabels.Clear(); _wordBrushes.Clear();
    }

    public bool Submit(CaptionUpdate update)
    {
        if (_fadeClock.IsRunning)
        {
            if (string.IsNullOrWhiteSpace(update.Text)) return true;
            ClearCaption();
        }
        var ready = _presentation.Submit(update, _presentationClock.Elapsed.TotalSeconds);
        FitCaption();
        return ready;
    }

    public void FadeCaption()
    {
        _scrollTimer.Stop();
        _fadeClock.Restart(); _fadeTimer.Start();
    }

    public void ClearCaption()
    {
        _fadeTimer.Stop(); _fadeClock.Reset(); _captionCanvas.Opacity = 1;
        _presentation.Clear(); _scrollTarget = null; _waitingForReveal = false; _scrollTimer.Stop(); _wordReveal.Clear();
        _presentationGeneration = _presentation.Generation;
        ShowRows(Array.Empty<string>());
    }

    private void FitCaption()
    {
        var width = Bounds.Width;
        if (width <= 0 || Bounds.Height <= 0) return;
        var side = Math.Clamp(width * .05, 24, 80);
        Padding = new Thickness(side, 22, side, 22);
        _text.LineHeight = _text.FontSize * 1.25;
        var stageHeight = Bounds.Height;
        var rows = Math.Max(1, (int)Math.Floor(Math.Max(0, stageHeight - 44) / _text.LineHeight));
        _captionCanvas.Height = rows * _text.LineHeight;
        var typeface = new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch);
        double Measure(string value) => new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, _text.FontSize, _text.Foreground!).WidthIncludingTrailingWhitespace;
        var geometry = $"{width}:{rows}:{_text.FontSize}:{typeface}";
        _presentation.ReadingWordsPerMinute = _readingWordsPerMinute;
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
        var progress = Math.Clamp(_scrollClock.Elapsed.TotalMilliseconds / (_presentation.IsCatchingUp ? 100 : 300), 0, 1);
        var eased = progress * progress * (3 - 2 * progress);
        for (var index = 0; index < _rowLabels.Count; index++)
        {
            Canvas.SetTop(_rowLabels[index], (index - eased * _scrollRows) * _text.LineHeight);
            _rowLabels[index].Opacity = index < _scrollRows ? 1 - progress : 1;
        }
        if (progress < 1) return;
        var target = _scrollTarget; _scrollTarget = null;
        _wordReveal.RemoveLeadingRows(_scrollRows);
        DrawRows(target, preserveReveal: true);
        _waitingForReveal = true;
        StartNextFrame();
    }

    private void ShowRows(string[] lines, bool revealNewWords = true)
    {
        DrawRows(lines, revealNewWords: revealNewWords);
    }

    private void DrawRows(string[] lines, bool preserveReveal = false, bool revealNewWords = true)
    {
        var now = _presentationClock.Elapsed.TotalSeconds;
        if (!preserveReveal) _wordReveal.Update(lines, now, revealNewWords && !_presentation.IsCatchingUp);
        _captionCanvas.Children.Clear(); _rowLabels.Clear(); _wordBrushes.Clear();
        for (var index = 0; index < lines.Length; index++)
        {
            var label = new TextBlock { Foreground = _text.Foreground,
                FontSize = _text.FontSize, FontFamily = _text.FontFamily, FontWeight = _text.FontWeight,
                LineHeight = _text.LineHeight, Height = _text.LineHeight, TextWrapping = TextWrapping.NoWrap };
            AutomationProperties.SetName(label, lines[index]);
            foreach (var word in _wordReveal.Rows[index])
            {
                var brush = new SolidColorBrush(_captionColor, word.Opacity(now));
                label.Inlines!.Add(new Run(word.Text) { Foreground = brush });
                _wordBrushes.Add((word, brush));
            }
            Canvas.SetTop(label, index * _text.LineHeight);
            _captionCanvas.Children.Add(label); _rowLabels.Add(label);
        }
    }
}
