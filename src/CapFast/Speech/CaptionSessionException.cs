namespace CapFast.Speech;

// Only locally authored, sanitized messages belong in this exception.
public sealed class CaptionSessionException(string safeMessage, bool isTransientTranslation = false,
    bool isTerminalService = false, bool isContentBlocked = false) : Exception(safeMessage)
{
    public bool IsTransientTranslation { get; } = isTransientTranslation;
    public bool IsTerminalService { get; } = isTerminalService;
    public bool IsContentBlocked { get; } = isContentBlocked;
}
