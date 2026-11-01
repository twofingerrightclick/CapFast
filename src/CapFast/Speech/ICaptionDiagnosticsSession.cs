namespace CapFast.Speech;

// Fixed, locally authored nonfatal messages an operator can inspect in Errors.
public interface ICaptionDiagnosticsSession
{
    event Action<string>? Diagnostic;
}

// Storage diagnostics can arrive while pause/shutdown finalizes a recording.
// They remain relevant after the live caption generation has been invalidated.
public interface ICaptionRecordingSession
{
    event Action<string>? RecordingDiagnostic;
}
