namespace CapFast.Speech;

public sealed record CaptionFailure(string SafeMessage, bool RequiresOperatorAction);

// Optional typed failure signal. Ended remains for sessions without classification.
public interface ICaptionFailureSession
{
    event Action<CaptionFailure>? Failure;
}
