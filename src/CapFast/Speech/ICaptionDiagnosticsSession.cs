namespace CapFast.Speech;

// Fixed, locally authored nonfatal messages an operator can inspect in Errors.
public interface ICaptionDiagnosticsSession
{
    event Action<string>? Diagnostic;
}
