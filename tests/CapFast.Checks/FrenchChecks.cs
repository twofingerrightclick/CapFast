using System.Net;
using System.Text.Json;
using CapFast.Core;
using CapFast.Speech;

internal static class FrenchChecks
{
    public static async Task RunAsync()
    {
        var settings = new CaptionSettings(loadLocalConfiguration: false);
        Require(settings.Provider == SpeechProvider.Google && settings.GoogleMode == GoogleCaptionMode.TwoStage
            && !settings.ShowFrench && settings.Locale == "sw-KE", "Default Swahili to English flow changed.");
        settings.GoogleMode = GoogleCaptionMode.LiveTranslate;
        settings.FrenchEnabled = true;
        Require(settings.ShowFrench && settings.EffectiveGoogleMode == GoogleCaptionMode.TwoStage,
            "French captions did not select the two-stage pipeline.");
        settings.Provider = SpeechProvider.Azure;
        Require(!settings.ShowFrench, "Azure displayed a French column without French translations.");

        var frenchHttp = new FrenchHttp();
        using (var translator = new FlashTextTranslator("test-key", frenchHttp, targetLanguage: "French"))
        {
            Require(await translator.TranslateAsync("Tunakutana leo.", default) == "Nous nous réunissons aujourd'hui.",
                "French text response was lost.");
            await translator.TranslateAsync("Tunaendelea.", default);
            var prompt = frenchHttp.Bodies[0].GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
            Require(prompt.Contains("Swahili text into French") && prompt.Contains("only the French translation"),
                "French translator used the wrong source or target language.");
            var context = JsonDocument.Parse(frenchHttp.Bodies[1].GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
            using (context)
                Require(context.RootElement.GetProperty("previousSource").GetString() == "Tunakutana leo."
                    && context.RootElement.GetProperty("sourceToTranslate").GetString() == "Tunaendelea.",
                    "French fragments lost their original Swahili context.");
        }

        var englishEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frenchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFrench = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captions = new List<CaptionUpdate>();
        var englishSources = new List<string>();
        var frenchSources = new List<string>();
        await using (var pipeline = new SourceTranslationPipeline((source, _) =>
        {
            englishSources.Add(source); englishEntered.TrySetResult(); return Task.FromResult("English " + source);
        }, coalesceWindow: TimeSpan.Zero, translateFrench: (source, token) =>
        {
            frenchSources.Add(source); frenchEntered.TrySetResult();
            return source == "one" ? releaseFrench.Task.WaitAsync(token) : Task.FromResult("Français " + source);
        }))
        {
            pipeline.Caption += update => { captions.Add(update); if (captions.Count == 2) complete.TrySetResult(); };
            pipeline.TryEnqueue(new("1", "one", true));
            await Task.WhenAll(englishEntered.Task, frenchEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
            pipeline.TryEnqueue(new("1", "duplicate", true));
            pipeline.TryEnqueue(new("2", "two", true));
            Require(captions.Count == 0 && englishSources.Count == 1,
                "A caption appeared before both translations finished, or paired batches ran out of order.");
            releaseFrench.SetResult("Français one");
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(englishSources.SequenceEqual(new[] { "one", "two" }) && englishSources.SequenceEqual(frenchSources),
                "French translated an English result instead of the same original source, or a duplicate was translated.");
            Require(captions.SequenceEqual(new[]
            {
                new CaptionUpdate("1", "English one", true, "Français one"),
                new CaptionUpdate("2", "English two", true, "Français two")
            }), "Paired captions lost their language, identity, or source order.");
        }

        var waitingFrench = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFrench = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleCaptions = 0;
        var canceled = new SourceTranslationPipeline((_, _) => Task.FromResult("English"), coalesceWindow: TimeSpan.Zero,
            translateFrench: (_, _) => { waitingFrench.TrySetResult(); return lateFrench.Task; });
        canceled.Caption += _ => staleCaptions++;
        canceled.TryEnqueue(new("cancel", "Swahili", true));
        await waitingFrench.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await canceled.DisposeAsync();
        lateFrench.SetResult("Français");
        await canceled.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Require(staleCaptions == 0, "A French translation arriving after pause refilled the display.");

        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incompleteCaptions = 0;
        await using (var pipeline = new SourceTranslationPipeline((_, _) => Task.FromResult("English"),
            coalesceWindow: TimeSpan.Zero, translateFrench: (_, _) => throw new CaptionSessionException("French unavailable")))
        {
            pipeline.Caption += _ => incompleteCaptions++;
            pipeline.Failed += message => failed.TrySetResult(message);
            pipeline.TryEnqueue(new("failure", "Swahili", true));
            Require(await failed.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "French unavailable" && incompleteCaptions == 0,
                "French failure published an incomplete caption pair or lost the safe error.");
        }
        Console.WriteLine("French checks passed: defaults, target language, Swahili context, ordered caption pairs, cancellation, and failures.");
    }

    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
    private sealed class FrenchHttp : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Bodies.Add(body.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"Nous nous réunissons aujourd'hui."}]}}]}""") };
        }
    }
}
