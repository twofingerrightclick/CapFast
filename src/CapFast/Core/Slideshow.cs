using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace CapFast.Core;

public enum SlideTextAlignment { Left, Center, Right, Justify }
public sealed record SlideParagraph(string Text, bool IsTitle, SlideTextAlignment Alignment);
public sealed record TextSlide(string Title, string Body)
{
    public IReadOnlyList<SlideParagraph> Paragraphs { get; init; } = Array.Empty<SlideParagraph>();
}

// Shared by Settings and the audience window; mutations run on the UI thread.
public sealed class Slideshow
{
    private IReadOnlyList<TextSlide> _slides = Array.Empty<TextSlide>();
    public event Action? Changed;
    public string Name { get; private set; } = "";
    public int Count => _slides.Count;
    public int Index { get; private set; }
    public bool IsShowing { get; private set; }
    public TextSlide? Current => Count == 0 ? null : _slides[Index];
    public void Load(string name, IReadOnlyList<TextSlide> slides)
    {
        if (slides.Count == 0) throw new InvalidDataException("The presentation has no slides.");
        _slides = slides.ToArray();
        Name = name;
        Index = 0;
        // Loading a deck prepares it without unexpectedly replacing live translations.
        IsShowing = false;
        Changed?.Invoke();
    }
    public void Move(int delta)
    {
        if (Count == 0) return;
        var next = (int)Math.Clamp((long)Index + delta, 0, Count - 1);
        if (next == Index) return;
        Index = next;
        Changed?.Invoke();
    }
    public void SetShowing(bool showing)
    {
        if ((showing && Count == 0) || IsShowing == showing) return;
        IsShowing = showing;
        Changed?.Invoke();
    }
}

public static class PowerPointTextReader
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int MaxSlides = 500;

    public static IReadOnlyList<TextSlide> Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".pptx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a PowerPoint (.pptx) presentation with text slides.");
        using var file = File.OpenRead(path);
        if (file.Length > 100 * 1024 * 1024) throw new InvalidDataException("Choose a presentation smaller than 100 MB.");
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var presentation = ReadXml(archive, "ppt/presentation.xml");
        var relationships = ReadXml(archive, "ppt/_rels/presentation.xml.rels").Root?.Elements()
            .Where(e => (string?)e.Attribute("TargetMode") != "External")
            .ToDictionary(e => (string?)e.Attribute("Id") ?? "", e => (string?)e.Attribute("Target") ?? "")
            ?? throw new InvalidDataException("The PowerPoint slide list is missing.");
        var ids = presentation.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId").ToArray() ?? [];
        if (ids.Length == 0 || ids.Length > MaxSlides)
            throw new InvalidDataException($"Choose a presentation with 1–{MaxSlides} slides.");
        var slides = new List<TextSlide>();
        foreach (var id in ids)
        {
            if (!relationships.TryGetValue((string?)id.Attribute(R + "id") ?? "", out var target))
                throw new InvalidDataException("A PowerPoint slide is missing.");
            var uri = new Uri(new Uri("https://pptx.local/ppt/presentation.xml"), target);
            if (uri.Host != "pptx.local") throw new InvalidDataException("The presentation contains an invalid slide reference.");
            var slidePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var slide = ReadXml(archive, slidePath);
            var layout = RelatedPart(archive, slidePath, "slideLayout");
            var master = layout is { } layoutPart ? RelatedPart(archive, layoutPart.Path, "slideMaster") : null;
            var paragraphs = new List<SlideParagraph>();
            var titles = new List<string>();
            var body = new List<string>();
            // Paragraph runs are joined without adding spaces: PowerPoint often splits a word across runs.
            foreach (var block in slide.Descendants().Where(e => e.Name == P + "sp" || e.Name == A + "tc"))
            {
                var ph = block.Descendants(P + "ph").FirstOrDefault();
                var layoutShape = ph is null ? null : layout?.Document.Descendants(P + "sp").FirstOrDefault(shape =>
                    shape.Descendants(P + "ph").Any(candidate =>
                        ((string?)candidate.Attribute("idx") ?? "0") == ((string?)ph.Attribute("idx") ?? "0")));
                var layoutPh = layoutShape?.Descendants(P + "ph").FirstOrDefault();
                var placeholder = (string?)ph?.Attribute("type") ?? (string?)layoutPh?.Attribute("type");
                var masterType = placeholder is "title" or "ctrTitle" ? "title" : "body";
                var masterShape = ph is null ? null : master?.Document.Descendants(P + "sp").FirstOrDefault(shape =>
                    shape.Descendants(P + "ph").Any(candidate => (string?)candidate.Attribute("type") == masterType));
                if (placeholder is "sldNum" or "dt" or "ftr" or "hdr") continue;
                var destination = placeholder is "title" or "ctrTitle" ? titles : body;
                foreach (var paragraph in block.Descendants(A + "p"))
                {
                    var text = string.Concat(paragraph.Descendants().Where(e => e.Name == A + "t" || e.Name == A + "br")
                        .Select(e => e.Name == A + "br" ? "\n" : e.Value));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        destination.Add(text);
                        var isTitle = placeholder is "title" or "ctrTitle";
                        var styleName = isTitle ? "titleStyle" : ph is not null ? "bodyStyle" : "otherStyle";
                        var alignment = ResolveAlignment(paragraph, block, layoutShape, masterShape,
                            master?.Document.Root?.Element(P + "txStyles")?.Element(P + styleName),
                            presentation.Root?.Element(P + "defaultTextStyle"));
                        paragraphs.Add(new(text, isTitle, alignment));
                    }
                }
            }
            var title = string.Join("\n", titles);
            var content = string.Join("\n\n", body);
            if (title.Length + content.Length > 20000)
                throw new InvalidDataException("A slide has too much text. Split it into smaller slides.");
            slides.Add(new(title, content) { Paragraphs = paragraphs.AsReadOnly() });
        }
        if (slides.All(s => string.IsNullOrWhiteSpace(s.Title + s.Body)))
            throw new InvalidDataException("No slide text was found. Use text boxes in the PowerPoint; image-only slides are not supported.");
        return slides;
    }

    private static SlideTextAlignment ResolveAlignment(XElement paragraph, XElement block,
        XElement? layoutShape, XElement? masterShape, XElement? masterStyle, XElement? defaultStyle)
    {
        var properties = paragraph.Element(A + "pPr");
        var level = int.TryParse((string?)properties?.Attribute("lvl"), out var parsed) ? Math.Clamp(parsed, 0, 8) : 0;
        string? ListAlignment(XElement? style) =>
            (string?)style?.Element(A + $"lvl{level + 1}pPr")?.Attribute("algn") ??
            (string?)style?.Element(A + "defPPr")?.Attribute("algn");
        string? ShapeAlignment(XElement? shape)
        {
            var body = shape?.Element(P + "txBody");
            var inherited = body?.Elements(A + "p").Select(p => p.Element(A + "pPr"))
                .FirstOrDefault(p => ((int?)p?.Attribute("lvl") ?? 0) == level && p?.Attribute("algn") is not null);
            return (string?)inherited?.Attribute("algn") ?? ListAlignment(body?.Element(A + "lstStyle"));
        }
        var body = block.Element(P + "txBody") ?? block.Element(A + "txBody");
        var alignment = (string?)properties?.Attribute("algn") ?? ListAlignment(body?.Element(A + "lstStyle")) ??
            ShapeAlignment(layoutShape) ?? ShapeAlignment(masterShape) ?? ListAlignment(masterStyle) ?? ListAlignment(defaultStyle);
        return alignment switch
        {
            "ctr" => SlideTextAlignment.Center,
            "r" => SlideTextAlignment.Right,
            "just" or "justLow" or "dist" or "thaiDist" => SlideTextAlignment.Justify,
            _ => SlideTextAlignment.Left
        };
    }

    private static (string Path, XDocument Document)? RelatedPart(ZipArchive archive, string source, string type)
    {
        var separator = source.LastIndexOf('/');
        var relationshipsPath = source[..(separator + 1)] + "_rels/" + source[(separator + 1)..] + ".rels";
        if (archive.GetEntry(relationshipsPath) is null) return null;
        var relationship = ReadXml(archive, relationshipsPath).Root?.Elements().FirstOrDefault(e =>
            ((string?)e.Attribute("Type"))?.EndsWith("/" + type, StringComparison.Ordinal) == true &&
            (string?)e.Attribute("TargetMode") != "External");
        if ((string?)relationship?.Attribute("Target") is not { } target) return null;
        var uri = new Uri(new Uri("https://pptx.local/" + source), target);
        if (uri.Host != "pptx.local") throw new InvalidDataException("The presentation contains an invalid style reference.");
        var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        return (path, ReadXml(archive, path));
    }

    private static XDocument ReadXml(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("The PowerPoint file is incomplete or invalid.");
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("The PowerPoint contains an oversized slide or slide list.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
}
