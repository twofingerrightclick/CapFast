using CapFast.Core;
namespace CapFast.Speech;
public interface ILanguageAssignmentSession
{
    LanguageAssignment? LanguageAssignment { get; }
    event Action<LanguageAssignment?>? LanguageAssignmentChanged;
    bool CanPresentCaption(CaptionUpdate update) => LanguageAssignment?.Epoch == update.AssignmentEpoch;
    void ResetLanguageDetection();
    void HintInputLanguage(string code);
}
