using System.Net;
using CapFast.Speech;

internal static class TranslationRecoveryChecks
{
    private const string Success = """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"English result"}]}}]}""";
    public static async Task RunAsync()
    {
        var retry = new FakeHttp(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.OK);
        using (var translator = new FlashTextTranslator("test-key", retry))
        {
            Require(await translator.TranslateAsync("Tunakutana leo.", default) == "English result", "Transient translation did not recover");
            Require(retry.Bodies.Count == 2 && retry.Bodies[0] == retry.Bodies[1], "Retry changed the fragment or prematurely advanced context");
            await translator.TranslateAsync("Tunaendelea.", default);
            Require(retry.Bodies[2].Contains("Tunakutana leo."), "Successful translation context did not persist");
        }
        var transport = new FakeHttp(HttpStatusCode.OK) { ThrowFirstRequest = true };
        using (var translator = new FlashTextTranslator("test-key", transport))
        {
            Require(await translator.TranslateAsync("source", default) == "English result" && transport.Bodies.Count == 2,
                "Temporary HTTP transport interruption did not recover");
        }
        foreach (var code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.BadRequest })
        {
            var rejected = new FakeHttp(code);
            using var translator = new FlashTextTranslator("test-key", rejected);
            try { await translator.TranslateAsync("source", default); throw new Exception("Rejected translation succeeded"); }
            catch (CaptionSessionException) { }
            Require(rejected.Bodies.Count == 1, "Authentication, quota, or configuration error was retried");
        }
        var failing = new FakeHttp(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway);
        using (var translator = new FlashTextTranslator("test-key", failing))
        {
            try { await translator.TranslateAsync("source", default); throw new Exception("Exhausted translation succeeded"); }
            catch (CaptionSessionException error) { Require(error.Message.Contains("502"), "Retry exhaustion lost its safe service code"); }
            Require(failing.Bodies.Count == 3, "Retry budget was not bounded to three attempts");
        }
        var cancelled = new FakeHttp(HttpStatusCode.ServiceUnavailable);
        using (var translator = new FlashTextTranslator("test-key", cancelled))
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = translator.TranslateAsync("source", cancellation.Token);
            cancellation.Cancel();
            try { await pending; throw new Exception("Cancelled translation succeeded"); }
            catch (OperationCanceledException) { }
            Require(cancelled.Bodies.Count == 1, "Cancellation continued retrying translation");
        }
        Console.WriteLine("Translation recovery checks passed: unchanged retries, persistent context, terminal errors, bounded exhaustion, and cancellation.");
    }
    private static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    private sealed class FakeHttp(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        public bool ThrowFirstRequest { get; init; }
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (ThrowFirstRequest && Bodies.Count == 1) throw new HttpRequestException("secret-sentinel");
            var code = codes[Math.Min(Bodies.Count - 1, codes.Length - 1)];
            return new HttpResponseMessage(code) { Content = new StringContent(code == HttpStatusCode.OK ? Success : "private provider body") };
        }
    }
}
