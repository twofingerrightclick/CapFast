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
        var previousAzure = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var previousRegion = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);
            Environment.SetEnvironmentVariable("AZURE_SPEECH_KEY", null);
            Environment.SetEnvironmentVariable("AZURE_SPEECH_REGION", null);
            File.WriteAllText(file, "# comment\nexport GEMINI_API_KEY = 'local-value'\nAZURE_SPEECH_KEY=azure-from-file\nAZURE_SPEECH_REGION=westus\nUNKNOWN=value\n");
            var config = new LocalConfiguration(file);
            if (config.GoogleKey != "local-value" || config.Get("AZURE_SPEECH_REGION") != "westus" || config.Get("UNKNOWN") != "")
                throw new Exception("Local config parse failed");
            if (new LocalConfiguration(file, readValues: false).GoogleKey != "") throw new Exception("Deferred configuration read local contents");
            var deferred = new CaptionSettings(loadLocalConfiguration: false) { IsLoadingConfiguration = true, GeminiKey = "manual-google" };
            if (deferred.ConfigurationPath != LocalConfiguration.FallbackPath || !deferred.Copy().IsLoadingConfiguration)
                throw new Exception("Deferred startup did not retain a probe-free fallback/loading state");
            deferred.ApplyLocalConfiguration(config, "", "", "");
            if (deferred.GeminiKey != "manual-google" || deferred.AzureKey != "azure-from-file" || deferred.AzureRegion != "westus"
                || deferred.IsLoadingConfiguration || deferred.ConfigurationPath != file)
                throw new Exception("Late configuration overwrote edits or lost untouched fields/path");
            var azureEdited = new CaptionSettings(loadLocalConfiguration: false) { AzureKey = "manual-azure", AzureRegion = "manual-region" };
            azureEdited.ApplyLocalConfiguration(config, "", "", "");
            if (azureEdited.GeminiKey != "local-value" || azureEdited.AzureKey != "manual-azure" || azureEdited.AzureRegion != "manual-region")
                throw new Exception("Late configuration did not preserve each independently edited field");
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
            Environment.SetEnvironmentVariable("AZURE_SPEECH_KEY", previousAzure);
            Environment.SetEnvironmentVariable("AZURE_SPEECH_REGION", previousRegion);
            File.Delete(file);
        }
    }
}
