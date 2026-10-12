namespace CapFast.Core;

// Keep both physical surfaces stable across source-language switches.
public sealed class LanguageSurfaceRouting
{
    public bool HasAutoAssignment { get; private set; }
    public string First => "en/sw";
    public string Second => "fr";
    public (bool ClearFirst, bool ClearSecond) Apply(LanguageAssignment? assignment)
    {
        if (assignment is null) return (false, false); // Detecting retains captions.
        HasAutoAssignment = true;
        return (false, false);
    }
    public void BeginRun() => HasAutoAssignment = false; // Retain surfaces while detecting a fresh run.
    public (bool ClearFirst, bool ClearSecond) ApplyDrain(LanguageAssignment assignment)
        => HasAutoAssignment ? (false, false) : Apply(assignment);
    public int Slot(string code) => code is "en" or "sw" ? 0 : code == "fr" ? 1 : -1;
    public static string Label(string code) => code switch { "en/sw" => "English / Swahili", "en" => "English", "sw" => "Swahili", "fr" => "Français", "auto" => "Automatic", _ => SpeechLanguageAssignment.Name(code) };
    public static string Status(LanguageAssignment? assignment) => assignment is null ? "Detecting input language…"
        : $"{Label(assignment.Input)} → {Label(assignment.Primary)} + {Label(assignment.Secondary)}";
}
