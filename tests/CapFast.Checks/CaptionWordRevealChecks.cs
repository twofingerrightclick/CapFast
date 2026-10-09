using CapFast.Core;

internal static class CaptionWordRevealChecks
{
    public static void Run()
    {
        var reveal = new CaptionWordReveal();
        reveal.Update(new[] { "One two three.", "Four five." }, 10);
        var words = reveal.Rows.SelectMany(row => row).ToArray();
        if (string.Concat(reveal.Rows[0].Select(word => word.Text)) != "One two three." ||
            Math.Abs(words[4].StartsAt - 10.4) > .00001 || words[0].Opacity(10) != 0 ||
            words[0].Opacity(10.12) < .999 || !reveal.IsAnimating(10.3) || reveal.IsAnimating(10.6))
            throw new Exception("Word reveal must preserve full layout and reveal ten words per second.");

        var firstStart = words[0].StartsAt;
        reveal.Update(new[] { "One two three.", "Four five. Six" }, 10.5);
        if (reveal.Rows[0][0].StartsAt != firstStart || reveal.Rows[1][1].StartsAt != words[4].StartsAt ||
            reveal.Rows[1][2].StartsAt < 10.5)
            throw new Exception("Unchanged words must not animate again when a partial grows.");
        var retained = reveal.Rows[1][0];
        reveal.RemoveLeadingRows(1);
        if (reveal.Rows[0][0] != retained)
            throw new Exception("Scrolling must retain reveal timing for surviving rows.");

        reveal.Update(new[] { "Reflowed content remains visible." }, 10.6, revealNewWords: false);
        if (reveal.IsAnimating(10.6)) throw new Exception("Geometry changes must not replay sealed words.");
        reveal.Clear(); reveal.Update(new[] { "Fresh start" }, 1);
        if (reveal.Rows[0][0].StartsAt != 1) throw new Exception("Clear must reset reveal scheduling.");
    }
}
