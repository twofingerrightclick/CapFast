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

    public static string LiveModel(GoogleCaptionMode mode) => mode == GoogleCaptionMode.TwoStage
        ? "gemini-3.5-transcribe-live" : "gemini-3.5-live-translate-preview";

    public static object CreateSetup(GoogleCaptionMode mode) => mode == GoogleCaptionMode.TwoStage
        ? new { setup = new { model = "models/gemini-3.5-transcribe-live",
            generationConfig = new { responseModalities = new[] { "TEXT" } },
            inputAudioTranscription = new { languageCodes = new[] { "sw-KE" }, mode = "VERBATIM" } } }
        : (object)new { setup = new { model = "models/gemini-3.5-live-translate-preview",
            generationConfig = new { responseModalities = new[] { "AUDIO" },
                translationConfig = new { targetLanguageCode = "en", echoTargetLanguage = true } },
            outputAudioTranscription = new { } } };
}
