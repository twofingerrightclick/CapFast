using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CapFast.Views;

// Capture the existing audience visual tree; never create a second pacing queue.
internal sealed class AudiencePreview : Control, IDisposable
{
    private readonly Control _source;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private RenderTargetBitmap? _frame;

    public AudiencePreview(Control source)
    {
        _source = source;
        IsHitTestVisible = false;
        _timer.Tick += Refresh;
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private void Refresh(object? sender, EventArgs e)
    {
        if (_source.Bounds.Width <= 0 || _source.Bounds.Height <= 0) return;
        var scale = Math.Min(1, 1280 / _source.Bounds.Width);
        var next = new RenderTargetBitmap(new PixelSize(
            Math.Max(1, (int)Math.Ceiling(_source.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(_source.Bounds.Height * scale))), new Vector(96 * scale, 96 * scale));
        next.Render(_source);
        var previous = _frame;
        _frame = next;
        InvalidateVisual();
        previous?.Dispose();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_frame is null) return;
        var width = Math.Min(Bounds.Width, Bounds.Height * 16 / 9);
        var height = width * 9 / 16;
        context.DrawImage(_frame, new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height));
    }

    public void Dispose() { _timer.Stop(); _timer.Tick -= Refresh; _frame?.Dispose(); _frame = null; }
}
