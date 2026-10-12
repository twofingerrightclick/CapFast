using CapFast.Core;

namespace CapFast.Speech;

// One provider result identifies the fragment and supplies both display surfaces.
public sealed record MultilingualCaptionResult(string SourceLanguage, string English, string Swahili, string French)
{
    public (string Primary, string Secondary) For(LanguageAssignment assignment) =>
        (assignment.Primary switch { "en" => English, "sw" => Swahili, "fr" => French, _ => English },
         assignment.Secondary switch { "en" => English, "sw" => Swahili, "fr" => French, _ => French });
}
