using System.Text.Json;
using CapFast.Core;
using CapFast.Speech;
public static class TranscriptLanguageChecks
{
    private sealed class DetectionHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var instruction = body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
            if (body.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32() < 1024
                || !instruction.Contains("Identify the language"))
                throw new Exception("Detection used a translation prompt or a budget too small for Gemini thinking tokens.");
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"fr\"}]}}]}") };
        }
    }
    public static async Task RunAsync()
    {
        using var detector = new FlashTextTranslator("test-key", new DetectionHttp(), targetLanguage: "language code", languageDetection: true);
        if (await detector.DetectLanguageAsync("Bonjour tout le monde", default) != "fr") throw new Exception("Flash language detection did not parse language code.");
        static string Failure(string json)
        {
            using var response = JsonDocument.Parse(json);
            try { FlashTextTranslator.ParseResponse(response.RootElement, "language code"); throw new Exception("Incomplete detection was accepted."); }
            catch (CaptionSessionException error) { return error.Message; }
        }
        if (!Failure("""{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"fr"}]}}]}""").Contains("finishReason: MAX_TOKENS"))
            throw new Exception("Detection failure log omitted the provider finish reason.");
        if (!Failure("""{"candidates":[{"finishReason":"secret-response-value"}]}""").Contains("finishReason: UNKNOWN") ||
            Failure("""{"candidates":[{"finishReason":"secret-response-value"}]}""").Contains("secret-response-value"))
            throw new Exception("Unrecognized provider finish reason leaked into diagnostics.");
        if (!Failure("""{"promptFeedback":{"blockReason":"SAFETY"}}""").Contains("blockReason: SAFETY"))
            throw new Exception("Blocked prompt log omitted the provider block reason.");
        var parser = new GeminiInputTranscript();
        var resolver = new TranscriptLanguageResolver();
        var calls = 0;
        Task<string?> Detect(string text, CancellationToken token) { calls++; return Task.FromResult<string?>(text.Contains("Bonjour") ? "fr" : "en"); }
        CaptionUpdate Read(string field, string text) { using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { serverContent = new Dictionary<string, object> { [field] = new { text } } })); return parser.Apply(json.RootElement).Single(); }
        void Check(bool value) { if (!value) throw new Exception("Text-only language fallback failed."); }
        Check(await resolver.ResolveAsync(Read("interimInputTranscription", "I'm grateful"), Detect, default) is null && calls == 0);
        Check(await resolver.ResolveAsync(Read("interimInputTranscription", "I'm grateful for"), Detect, default) is null && calls == 0);
        var first = await resolver.ResolveAsync(Read("interimInputTranscription", "I'm grateful for the prayers"), Detect, default);
        Check(first?.SourceLanguage == "en" && calls == 1);
        var final = await resolver.ResolveAsync(Read("inputTranscription", "I'm grateful for the prayers and your support."), Detect, default);
        Check(final?.SourceLanguage == "en" && calls == 1);
        var french = await resolver.ResolveAsync(Read("inputTranscription", "Bonjour à tous."), Detect, default);
        Check(french?.SourceLanguage == "fr" && calls == 2);
        var provider = await resolver.ResolveAsync(new("provider", "Habari leo", false, SourceLanguage: "sw-KE"), Detect, default);
        Check(provider?.SourceLanguage == "sw" && calls == 2);
        var unidentified = await resolver.ResolveAsync(new("unidentified", "Bonjour tout le monde.", true, SourceLanguage: "und"), Detect, default);
        Check(unidentified?.SourceLanguage == "fr" && calls == 3);
        try { await resolver.ResolveAsync(new("failure", "Four words of speech", true), (_, _) => Task.FromException<string?>(new IOException()), default); throw new Exception("Detection failure was swallowed."); }
        catch (IOException) { }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await resolver.ResolveAsync(new("canceled", "Four words of speech", true), (_, _) => Task.FromResult<string?>("de"), canceled.Token); throw new Exception("Canceled detection assigned language."); }
        catch (OperationCanceledException) { }
        var revisions = new TranscriptLanguageResolver();
        var revisionCalls = 0;
        Task<string?> DetectRevision(string text, CancellationToken token)
        {
            revisionCalls++;
            return Task.FromResult<string?>(text.StartsWith("Welcome") ? "en" : "sw");
        }
        await revisions.ResolveAsync(new("speaker-change", "Welcome everyone to the meeting", false), DetectRevision, default);
        var revisedSpeaker = await revisions.ResolveAsync(new("speaker-change", "Karibuni nyote kwenye mkutano wa leo.", true), DetectRevision, default);
        if (revisedSpeaker?.SourceLanguage != "sw" || revisionCalls != 2)
            throw new Exception("Replaced transcript retained the previous speaker's detected language");
        var interimRevisions = new TranscriptLanguageResolver();
        await interimRevisions.ResolveAsync(new("interim-switch", "Welcome everyone to the meeting", false), DetectRevision, default);
        var revisedInterim = await interimRevisions.ResolveAsync(new("interim-switch", "Karibuni nyote kwenye mkutano wa leo", false), DetectRevision, default);
        if (revisedInterim?.SourceLanguage != "sw" || revisionCalls != 4)
            throw new Exception("Replaced interim transcript retained the previous speaker's detected language");
        var unknownCalls = 0;
        Task<string?> Unknown(string text, CancellationToken token) { unknownCalls++; return Task.FromResult<string?>(null); }
        await resolver.ResolveAsync(new("unknown", "yes yes yes yes yes", false), Unknown, default);
        await resolver.ResolveAsync(new("unknown", "yes yes yes yes yes yes", false), Unknown, default);
        await resolver.ResolveAsync(new("unknown", "yes yes yes yes yes yes", true), Unknown, default);
        await resolver.ResolveAsync(new("unknown", "yes yes yes yes yes yes", true), Unknown, default);
        Check(unknownCalls == 2);
    }
}
