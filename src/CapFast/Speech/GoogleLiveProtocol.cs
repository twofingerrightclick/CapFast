using CapFast.Core;
using System.Net.WebSockets;
using System.Text.Json;

namespace CapFast.Speech;

public static class GoogleLiveProtocol
{
    public static JsonDocument ParseMessage(ReadOnlyMemory<byte> message, WebSocketMessageType type)
    {
        if (type is not (WebSocketMessageType.Text or WebSocketMessageType.Binary) || message.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Invalid Google Live message.");
        // Google may send UTF-8 JSON in binary frames, not just text frames.
        return JsonDocument.Parse(message);
    }

    public static string LiveModel() => "gemini-3.5-transcribe-live";

    public static object CreateSetup() => new { setup = new { model = "models/gemini-3.5-transcribe-live",
        generationConfig = new { responseModalities = new[] { "TEXT" } },
        // These are recognition biases, not a fixed source assignment. The
        // model may still identify switches between the three speech languages.
        inputAudioTranscription = new { mode = "VERBATIM", languageCodes = new[] { "en-US", "sw-KE", "fr-FR" } } } };
}
