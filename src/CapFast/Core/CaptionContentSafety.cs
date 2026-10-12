namespace CapFast.Core;

// Provider text is untrusted even when it arrives in a successful response.
// Reject clearly structured diagnostics before they reach either caption surface.
public static class CaptionContentSafety
{
    private static readonly string[] DiagnosticPrefixes =
    [
        "json parsing error", "json parse error", "parse error:", "syntaxerror:",
        "exception:", "traceback (most recent call last)", "service error:",
        "api error:", "http error:", "error:"
    ];

    public static bool IsTechnicalOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.TrimStart();
        // These are private request fields, never audience-facing translation.
        // Check the entire response: model commentary can follow ordinary prose.
        if (value.Contains("previousSource", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("sourceToTranslate", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("sourceLanguage", StringComparison.OrdinalIgnoreCase)) return true;
        if (LooksLikeStructuredResponse(value) || value.StartsWith("```", StringComparison.Ordinal)) return true;
        foreach (var prefix in DiagnosticPrefixes)
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool HoldPartial(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.TrimStart();
        if (value is "{" or "[" or "`" or "``") return true;
        return value.Length >= 4 && DiagnosticPrefixes.Any(prefix => value.Length < prefix.Length &&
            prefix.StartsWith(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeStructuredResponse(string value)
    {
        if (value.Length < 2) return false;
        if (value[0] == '{') return value[1] is '"' or '}' or '\'';
        if (value[0] == '[') return value[1] is '{' or '[' or '"' or ']' or '-' or >= '0' and <= '9';
        return false;
    }
}
