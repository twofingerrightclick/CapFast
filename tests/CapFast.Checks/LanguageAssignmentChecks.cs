using CapFast.Core;
public static class LanguageAssignmentChecks
{
    public static void Run()
    {
        var state = new SpeechLanguageAssignment();
        void Check(bool condition) { if (!condition) throw new Exception("Language assignment failed."); }
        var first = state.Accept("en-US", 0)!;
        Check(first.Input == "en" && first.Primary == "sw" && first.Secondary == "fr");
        var switched = state.Accept("sw-KE", 2)!;
        Check(switched.Input == "sw" && switched.Primary == "en" && switched.Secondary == "fr" && switched.Epoch == first.Epoch && state.Current == first);
        state.ObserveAudio(AudioActivity.Idle, 6); Check(state.Current == first);
        state.ObserveAudio(AudioActivity.Idle, 7); Check(state.Current is null);
        state.ObserveAudio(AudioActivity.Speech, 8);
        var second = state.Accept("sw", 8)!;
        Check(second.Primary == "en" && second.Secondary == "fr" && second.Epoch > first.Epoch);
        state.Reset(); state.ObserveAudio(AudioActivity.Speech, 10); var outside = state.Accept("de-DE", 10)!;
        Check(outside.Primary == "en" && outside.Secondary == "fr");
        state.Hint("fr"); state.ObserveAudio(AudioActivity.Speech, 12); Check(state.Accept("en", 11)?.Input == "fr");
        Check(state.Accept("fr", 12)!.Primary == "en" && state.Current!.Secondary == "sw");
        state.ObserveAudio(AudioActivity.Idle, 17); Check(state.Current is null);
        Check(state.Accept(null, 18) is null);
    }
}
