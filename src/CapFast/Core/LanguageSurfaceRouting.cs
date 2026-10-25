namespace CapFast.Core;

// English/Swahili switching shares the first surface; French speech needs
// separate English and Swahili surfaces. Route by the delivered fragment,
// not the slower dominant-language indicator.
public sealed class LanguageSurfaceRouting
{
    public bool HasAutoAssignment { get; private set; }
    public string First { get; private set; } = "en/sw";
    public string Second { get; private set; } = "fr";
    public (bool ClearFirst, bool ClearSecond) Apply(LanguageAssignment? assignment)
    {
        if (assignment is null) return (false, false); // Detecting retains captions.
        HasAutoAssignment = true;
        var englishAndSwahili = assignment.Primary == "en" && assignment.Secondary == "sw";
        First = englishAndSwahili ? "en" : "en/sw";
        Second = englishAndSwahili ? "sw" : "fr";
        return (false, false);
    }
    public void BeginRun() => HasAutoAssignment = false; // Retain surfaces while detecting a fresh run.
    public (bool ClearFirst, bool ClearSecond) ApplyDrain(LanguageAssignment assignment)
        => Apply(assignment);
    public int Slot(string code) => code == Second ? 1
        : code == First || First == "en/sw" && code is "en" or "sw" ? 0 : -1;
    public static string Label(string code) => code switch { "en/sw" => "English / Swahili", "en" => "English", "sw" => "Swahili", "fr" => "Français", "auto" => "Automatic", _ => SpeechLanguageAssignment.Name(code) };
    public static string Status(LanguageAssignment? assignment) => assignment is null ? "Detecting input language…"
        : $"{Label(assignment.Input)} → {Label(assignment.Primary)} + {Label(assignment.Secondary)}";
}
