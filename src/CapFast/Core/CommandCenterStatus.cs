namespace CapFast.Core;

public static class CommandCenterStatus
{
    public static string Describe(CaptionState state, AudioActivity activity, string status) => state switch
    {
        CaptionState.Starting when status.StartsWith("Automatic recovery", StringComparison.Ordinal) =>
            $"Current state: recovering · {status}",
        CaptionState.Starting => $"Current state: connecting · {status}",
        CaptionState.Pausing => "Current state: stopping capture.",
        CaptionState.Paused => $"Current state: paused · {status}",
        _ when status == "Google connection interrupted — reconnecting…" =>
            "Current state: recovering the caption service connection.",
        _ when status == "Listening locally — Google reconnects when speech resumes." =>
            "Current state: listening locally. Google connects when speech resumes.",
        _ when activity == AudioActivity.Speech =>
            "Current state: live. Speech activity detected; a caption may follow.",
        _ when activity == AudioActivity.Music =>
            "Current state: live. Music activity detected.",
        _ => "Current state: live and listening. Captions appear after accepted speech and translation."
    };
}
