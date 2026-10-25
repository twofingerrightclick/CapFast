using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CapFast.Core;

namespace CapFast.Views;

// Both windows use the same 16:9 stage so the preview matches the audience output.
public sealed class SlideSurface : Border
{
    private readonly StackPanel _text = new() { Width = 1120, Spacing = 24 };
    public SlideSurface()
    {
        Background = Brushes.Black;
        Child = new Viewbox { Stretch = Stretch.Uniform, Child = new Border
        {
            Width = 1280, Height = 720, Padding = new Thickness(80, 60), Background = Brushes.Black,
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = _text }
        } };
    }
    public void Show(TextSlide? slide)
    {
        _text.Children.Clear();
        if (slide is null) return;
        var paragraphs = slide.Paragraphs.Count > 0 ? slide.Paragraphs : new[]
        {
            new SlideParagraph(slide.Title, true, SlideTextAlignment.Left),
            new SlideParagraph(slide.Body, false, SlideTextAlignment.Left)
        };
        foreach (var paragraph in paragraphs.OrderByDescending(p => p.IsTitle))
        {
            if (string.IsNullOrWhiteSpace(paragraph.Text)) continue;
            _text.Children.Add(new TextBlock
            {
                Text = paragraph.Text, FontSize = paragraph.IsTitle ? 56 : 38,
                FontWeight = paragraph.IsTitle ? FontWeight.Bold : FontWeight.Normal,
                Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = paragraph.Alignment switch
                {
                    SlideTextAlignment.Center => TextAlignment.Center,
                    SlideTextAlignment.Right => TextAlignment.Right,
                    SlideTextAlignment.Justify => TextAlignment.Justify,
                    _ => TextAlignment.Left
                }
            });
        }
    }
}
