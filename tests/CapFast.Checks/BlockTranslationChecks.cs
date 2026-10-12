using System.Net;
using System.Text.Json;
using CapFast.Core;
using CapFast.Speech;

public static class BlockTranslationChecks
{
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Inputs = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Inputs.Add(json.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"Translated.\"}]}}]}") };
        }
    }
    public static async Task RunAsync()
    {
        var handlers = new Dictionary<(long Epoch, string Target), Handler>();
        using var translator = new BlockTextTranslator((assignment, target) =>
        {
            var handler = new Handler(); handlers.Add((assignment.Epoch, target), handler);
            return new FlashTextTranslator("fake-test-key", handler, sourceLanguage: SpeechLanguageAssignment.Name(assignment.Input), targetLanguage: target);
        });
        var old = new LanguageAssignment("sw", "en", "fr", 1);
        var fresh = new LanguageAssignment("en", "sw", "fr", 2);
        await translator.TranslateAsync("Tuko mbali", old, default); // accepted interim chunk
        await translator.TranslateAsync("New source.", fresh, default);
        await translator.TranslateAsync("na nyumbani.", old, default); // late authoritative suffix
        await translator.TranslateAsync("Another sentence.", old, default);
        foreach (var target in new[] { "en", "fr" })
        {
            var input = handlers[(1, target)].Inputs;
            if (input.Count != 3 || input[0] != "Tuko mbali") throw new Exception("Old block recreated its translator for a final suffix.");
            using var suffix = JsonDocument.Parse(input[1]);
            if (suffix.RootElement.GetProperty("previousSource").GetString() != "Tuko mbali"
                || suffix.RootElement.GetProperty("sourceToTranslate").GetString() != "na nyumbani.")
                throw new Exception("Drained suffix lost previous original-language context.");
            using var chain = JsonDocument.Parse(input[2]);
            if (chain.RootElement.GetProperty("previousSource").GetString() != "na nyumbani.")
                throw new Exception("Drained chunk chain lost ordered context.");
        }
        if (handlers[(2, "sw")].Inputs.Single() != "New source." || handlers[(2, "fr")].Inputs.Single() != "New source.")
            throw new Exception("Old block context leaked into a new assignment.");
        var alternatingHandlers = new List<(LanguageAssignment Assignment, string Target, Handler Handler)>();
        using var alternating = new BlockTextTranslator((assignment, target) =>
        {
            var handler = new Handler(); alternatingHandlers.Add((assignment, target, handler));
            return new FlashTextTranslator("fake-test-key", handler,
                sourceLanguage: SpeechLanguageAssignment.Name(assignment.Input), targetLanguage: target);
        });
        var english = new LanguageAssignment("en", "sw", "fr", 3);
        var swahili = new LanguageAssignment("sw", "en", "fr", 3);
        await alternating.TranslateAsync("Welcome everyone.", english, default);
        await alternating.TranslateAsync("Karibuni nyote.", swahili, default);
        await alternating.TranslateAsync("Let's begin.", english, default);
        foreach (var target in new[] { "sw", "fr" })
        {
            var englishInputs = alternatingHandlers.Where(item => item.Assignment == english && item.Target == target)
                .SelectMany(item => item.Handler.Inputs).ToArray();
            if (englishInputs.Length != 2 || englishInputs[1] != "Let's begin.")
                throw new Exception("An earlier speaker's source context leaked across a language switch.");
        }
    }
}
