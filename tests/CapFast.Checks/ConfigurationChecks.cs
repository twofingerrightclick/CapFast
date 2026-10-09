using CapFast.Core;
using CapFast.Speech;

internal static class ConfigurationChecks
{
    public static void Run()
    {
        var rejected = GeminiCaptionSession.DescribeCloseFailure(1011, "Model not found secret-value", false);
        if (!rejected.Contains("model is unavailable") || rejected.Contains("secret-value")) throw new Exception("Close reason classification leaked or lost model error");
        var unknown = GeminiCaptionSession.DescribeCloseFailure(1011, "secret-value", true);
        if (!unknown.Contains("after setup") || unknown.Contains("secret-value")) throw new Exception("Unknown close reason leaked or lost setup stage");
        var file = Path.Combine(Path.GetTempPath(), "capfast-env-" + Guid.NewGuid() + ".env");
        var previousGemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var previousGoogle = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);
            File.WriteAllText(file, "# comment\nexport GEMINI_API_KEY = 'local-value'\nAZURE_SPEECH_REGION=westus\nUNKNOWN=value\n");
            var config = new LocalConfiguration(file);
            if (config.GoogleKey != "local-value" || config.Get("AZURE_SPEECH_REGION") != "westus" || config.Get("UNKNOWN") != "")
                throw new Exception("Local config parse failed");
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "environment-value");
            if (config.GoogleKey != "environment-value") throw new Exception("Process environment did not override file");
            File.WriteAllText(file, new string('x', 65537));
            if (new LocalConfiguration(file).Error == null) throw new Exception("Oversized config accepted");
            Console.WriteLine("Configuration checks passed: quotes, comments, allowlist, environment precedence, file size bound.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previousGemini);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", previousGoogle);
            File.Delete(file);
        }
    }
}
