namespace CapFast.Speech;

// Only locally authored, sanitized messages belong in this exception.
public sealed class CaptionSessionException(string safeMessage) : Exception(safeMessage);
