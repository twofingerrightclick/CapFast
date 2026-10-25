namespace CapFast.Core;

// These messages are authored by the providers; raw service response bodies never enter this policy.
public static class CaptionFailurePolicy
{
    public static bool RequiresOperatorAction(string message) =>
        message.Contains("could not caption an accepted speech segment", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("denied access", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("tooManyRequests", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("badRequest", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("invalidArgument", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("billing", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("selected audio input stopped", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("selected Windows audio input", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("audio capture could not", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("supports macOS and Windows only", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("selected Mac audio input", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("selected audio input is unavailable", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("unsupported audio format", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Mac audio capture could not start", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("speech classification is unavailable", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("speech classification returned an invalid frame", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("audio helper is missing", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("supports macOS only", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("selected Google Live model", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("model or endpoint", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("session setup", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("setup fields", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("model or configuration", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("configuration", StringComparison.OrdinalIgnoreCase);
}
