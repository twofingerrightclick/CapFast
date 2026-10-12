using System.Net;
using System.Text.Json;
using CapFast.Core;
using CapFast.Speech;

internal static class TranslationCommentaryChecks
{
    private const string Commentary = "English text into Swahili. Swahili text in both `previousSource` and `sourceToTranslate`: mistake and provide Swahili, or is it English? No, it's clearly Swahili.";
    public static async Task RunAsync()
    {
        using var translator = new FlashTextTranslator("test-key", new Handler());
        await using var pipeline = new SourceTranslationPipeline(async (text, _, token) =>
            (await translator.TranslateAsync(text, token), "secondary"));
        var captions = new List<CaptionUpdate>();
        var issue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.Caption += update => { lock (captions) captions.Add(update); };
        pipeline.Failed += _ => issue.TrySetResult(true);
        pipeline.Diagnostic += _ => issue.TrySetResult(false);
        async Task Accept(string id, string text, string language)
        {
            await pipeline.CompleteReceivedFinalAsync(new(id, text, true, SourceLanguage: language),
                pipeline.CaptureReceivedBlock(pipeline.AudioGeneration), 0);
        }
        await Accept("english", "Welcome everyone.", "en");
        await Accept("swahili", "Karibuni nyote.", "sw");
        lock (captions)
            if (captions.Any(c => c.Text.Contains("sourceToTranslate")))
                throw new Exception("Alternating speakers exposed translation commentary on the audience surface");
        if (!issue.Task.IsCompleted || await issue.Task)
            throw new Exception("Translation commentary must be withheld without stopping capture");
        await Accept("english-next", "We will begin now.", "en");
        lock (captions)
            if (captions.Count != 2) throw new Exception("Valid speech did not continue after withheld commentary");
        foreach (var technical in new[] { Commentary, "`sourceToTranslate` is already in Swahili.", "The previousSource contains English." })
            if (!CaptionContentSafety.IsTechnicalOutput(technical)) throw new Exception("Translation payload commentary escaped the content guard");
        foreach (var speech in new[] { "He speaks English and Swahili.", "No, it's clearly Swahili.", "Please provide a translation.", "We corrected our previous source of information." })
            if (CaptionContentSafety.IsTechnicalOutput(speech)) throw new Exception("Ordinary speech was mistaken for translation commentary");
    }
    private sealed class Handler : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var text = ++_calls == 2 ? Commentary : "Valid translated speech.";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } } })) });
        }
    }
}
