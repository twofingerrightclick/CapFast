using System.IO.Compression;
using CapFast.Core;

public static class SlideshowChecks
{
    public static void Run()
    {
        void Check(bool condition, string message)
        { if (!condition) throw new Exception("Slideshow: " + message); }
        var show = new Slideshow();
        show.SetShowing(true); show.Move(1);
        Check(!show.IsShowing && show.Current is null, "empty decks cannot go live");
        show.Load("deck", [new("First", "Hello"), new("Second", "World")]);
        Check(!show.IsShowing && show.Index == 0, "loading prepares first slide");
        show.Move(-1); Check(show.Index == 0, "previous clamps at beginning");
        show.Move(1); show.SetShowing(true);
        Check(show.IsShowing && show.Current?.Title == "Second", "preview selection goes live");
        show.Move(1); Check(show.Index == 1, "next clamps at end");
        show.SetShowing(true);
        Check(show.IsShowing, "selecting the active output retains it");
        show.SetShowing(false); show.SetShowing(true);
        Check(show.Index == 1, "output toggling retains slide");
        try { show.Load("empty", []); throw new Exception("Empty deck accepted"); }
        catch (InvalidDataException) { }
        Check(show.IsShowing && show.Name == "deck", "failed load preserves output");
        show.Load("replacement", [new("New", "")]);
        Check(!show.IsShowing && show.Index == 0, "replacement prepares safely");

        var path = Path.Combine(Path.GetTempPath(), $"capfast-slides-{Guid.NewGuid():N}.pptx");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                void Entry(string name, string xml)
                { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(xml); }
                Entry("ppt/presentation.xml", """
                    <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst><p:sldId id="256" r:id="second"/><p:sldId id="257" r:id="first"/></p:sldIdLst></p:presentation>
                    """);
                Entry("ppt/_rels/presentation.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="first" Target="slides/slide1.xml"/><Relationship Id="second" Target="/ppt/slides/slide2.xml"/></Relationships>
                    """);
                Entry("ppt/slides/slide1.xml", """
                    <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Last</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>
                    """);
                Entry("ppt/slides/slide2.xml", """
                    <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree>
                    <p:sp><p:nvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>Wel</a:t></a:r><a:r><a:t>come</a:t></a:r></a:p></p:txBody></p:sp>
                    <p:sp><p:txBody><a:p><a:r><a:t>Bonjour 👋</a:t></a:r><a:br/><a:r><a:t>Karibu</a:t></a:r></a:p><a:p><a:r><a:t>Next paragraph</a:t></a:r></a:p></p:txBody></p:sp>
                    <p:sp><p:nvSpPr><p:nvPr><p:ph type="sldNum"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:fld><a:t>2</a:t></a:fld></a:p></p:txBody></p:sp>
                    </p:spTree></p:cSld></p:sld>
                    """);
            }
            var slides = PowerPointTextReader.Read(path);
            Check(slides.Count == 2 && slides[0].Title == "Welcome" && slides[1].Body == "Last", "presentation relationship order and split text runs");
            Check(slides[0].Body == "Bonjour 👋\nKaribu\n\nNext paragraph", "Unicode, breaks, paragraphs, and excluded slide numbers");
            Check(slides[0].Paragraphs.All(p => p.Alignment == SlideTextAlignment.Left), "missing alignment defaults to left");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                void Entry(string name, string xml)
                { zip.GetEntry(name)?.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(xml); }
                Entry("ppt/slides/_rels/slide2.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="layout" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/></Relationships>
                    """);
                Entry("ppt/slideLayouts/_rels/slideLayout1.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="master" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>
                    """);
                Entry("ppt/slideLayouts/slideLayout1.xml", """
                    <p:sldLayout xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree>
                    <p:sp><p:nvSpPr><p:nvPr><p:ph type="title" idx="0"/></p:nvPr></p:nvSpPr><p:txBody><a:lstStyle/><a:p><a:pPr algn="ctr"/></a:p></p:txBody></p:sp>
                    <p:sp><p:nvSpPr><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr><p:txBody><a:lstStyle><a:lvl2pPr algn="r"/></a:lstStyle><a:p/></p:txBody></p:sp>
                    </p:spTree></p:cSld></p:sldLayout>
                    """);
                Entry("ppt/slideMasters/slideMaster1.xml", """
                    <p:sldMaster xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree>
                    <p:sp><p:nvSpPr><p:nvPr><p:ph type="body" idx="9"/></p:nvPr></p:nvSpPr><p:txBody><a:lstStyle><a:lvl3pPr algn="ctr"/></a:lstStyle><a:p/></p:txBody></p:sp>
                    </p:spTree></p:cSld><p:txStyles><p:bodyStyle><a:lvl1pPr algn="just"/></p:bodyStyle></p:txStyles></p:sldMaster>
                    """);
                Entry("ppt/slides/slide2.xml", """
                    <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree>
                    <p:sp><p:nvSpPr><p:nvPr><p:ph idx="0"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>Inherited title</a:t></a:r></a:p></p:txBody></p:sp>
                    <p:sp><p:nvSpPr><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr><p:txBody><a:lstStyle><a:lvl4pPr algn="r"/></a:lstStyle>
                    <a:p><a:pPr algn="l"/><a:r><a:t>Explicit left</a:t></a:r></a:p>
                    <a:p><a:pPr algn="ctr"/><a:r><a:t>Explicit center</a:t></a:r></a:p>
                    <a:p><a:pPr algn="r"/><a:r><a:t>Explicit right</a:t></a:r></a:p>
                    <a:p><a:r><a:t>Master justified</a:t></a:r></a:p>
                    <a:p><a:pPr lvl="1"/><a:r><a:t>Layout right</a:t></a:r></a:p>
                    <a:p><a:pPr lvl="2"/><a:r><a:t>Master shape center</a:t></a:r></a:p>
                    <a:p><a:pPr lvl="3"/><a:r><a:t>Local list right</a:t></a:r></a:p>
                    </p:txBody></p:sp></p:spTree></p:cSld></p:sld>
                    """);
            }
            var aligned = PowerPointTextReader.Read(path)[0];
            Check(aligned.Title == "Inherited title", "placeholder type inherits from layout by index");
            Check(aligned.Paragraphs.Select(p => p.Alignment).SequenceEqual(new[]
            {
                SlideTextAlignment.Center, SlideTextAlignment.Left, SlideTextAlignment.Center,
                SlideTextAlignment.Right, SlideTextAlignment.Justify, SlideTextAlignment.Right,
                SlideTextAlignment.Center, SlideTextAlignment.Right
            }), "paragraph overrides, mixed alignment, local list style, layout and master inheritance");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.GetEntry("ppt/slides/slide2.xml")!.Delete();
            try { PowerPointTextReader.Read(path); throw new Exception("Broken presentation accepted"); }
            catch (InvalidDataException) { }
            File.WriteAllText(path, "not a zip file");
            try { PowerPointTextReader.Read(path); throw new Exception("Invalid presentation accepted"); }
            catch (InvalidDataException) { }
        }
        finally { File.Delete(path); }
        Console.WriteLine("Slideshow checks passed.");
    }
}
