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
            var appearanceFile = file + ".appearance";
            try
            {
                if (AppearancePreferences.Load(appearanceFile) != AppAppearance.Dark)
                    throw new Exception("Missing appearance preference did not default to dark.");
                AppearancePreferences.Save(AppAppearance.Light, appearanceFile);
                if (AppearancePreferences.Load(appearanceFile) != AppAppearance.Light)
                    throw new Exception("Light appearance did not survive reload.");
                AppearancePreferences.Save(AppAppearance.Dark, appearanceFile);
                if (AppearancePreferences.Load(appearanceFile) != AppAppearance.Dark)
                    throw new Exception("Dark appearance did not survive reload.");
                File.WriteAllText(appearanceFile, "invalid");
                if (AppearancePreferences.Load(appearanceFile) != AppAppearance.Dark)
                    throw new Exception("Invalid appearance preference did not fall back safely.");
            }
            finally { File.Delete(appearanceFile); }
            var preferencesFile = file + ".settings.json";
            try
            {
                if (CaptionPreferences.Load(preferencesFile) != CaptionPreferences.Default)
                    throw new Exception("Missing caption preferences did not use defaults.");
                var saved = new CaptionSettings(loadLocalConfiguration: false)
                {
                    AudioDeviceId = "BlackHole2ch_UID", FontSize = 60, ReadingWordsPerMinute = 230,
                    GeminiKey = "secret-must-not-be-saved"
                };
                CaptionPreferences.Save(saved, preferencesFile);
                var reloaded = CaptionPreferences.Load(preferencesFile);
                if (reloaded.AudioDeviceId != saved.AudioDeviceId || reloaded.FontSize != saved.FontSize
                    || reloaded.ReadingWordsPerMinute != saved.ReadingWordsPerMinute)
                    throw new Exception("Audio input or caption display preferences did not survive reload.");
                if (File.ReadAllText(preferencesFile).Contains(saved.GeminiKey))
                    throw new Exception("API key leaked into caption preferences.");
                File.WriteAllText(preferencesFile, "{broken");
                if (CaptionPreferences.Load(preferencesFile) != CaptionPreferences.Default)
                    throw new Exception("Invalid caption preferences did not use defaults.");
                File.WriteAllText(preferencesFile,
                    "{\"audioDeviceId\":null,\"fontSize\":999,\"readingWordsPerMinute\":1}");
                if (CaptionPreferences.Load(preferencesFile) != CaptionPreferences.Default)
                    throw new Exception("Out-of-range caption preferences did not use defaults.");
            }
            finally { File.Delete(preferencesFile); }
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);
            File.WriteAllText(file, "# comment\nexport GEMINI_API_KEY = 'local-value'\nUNKNOWN=value\n");
            var config = new LocalConfiguration(file);
            if (config.GoogleKey != "local-value" || config.Get("UNKNOWN") != "")
                throw new Exception("Local config parse failed");
            if (new LocalConfiguration(file, readValues: false).GoogleKey != "") throw new Exception("Deferred configuration read local contents");
            var deferred = new CaptionSettings(loadLocalConfiguration: false) { IsLoadingConfiguration = true, GeminiKey = "manual-google" };
            if (deferred.RejectMusic || deferred.Copy().RejectMusic)
                throw new Exception("Music rejection did not default off in new settings.");
            deferred.RejectMusic = true;
            if (!deferred.Copy().RejectMusic)
                throw new Exception("Music rejection choice was lost from the session settings snapshot.");
            if (deferred.ConfigurationPath != LocalConfiguration.FallbackPath || !deferred.Copy().IsLoadingConfiguration)
                throw new Exception("Deferred startup did not retain a probe-free fallback/loading state");
            deferred.ApplyLocalConfiguration(config, "");
            if (deferred.GeminiKey != "manual-google"
                || deferred.IsLoadingConfiguration || deferred.ConfigurationPath != file)
                throw new Exception("Late configuration overwrote edits or lost untouched fields/path");
            var untouched = new CaptionSettings(loadLocalConfiguration: false);
            untouched.ApplyLocalConfiguration(config, "");
            if (untouched.GeminiKey != "local-value")
                throw new Exception("Late configuration did not load the untouched Google key");
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "environment-value");
            if (config.GoogleKey != "environment-value") throw new Exception("Process environment did not override file");
            File.WriteAllText(file, new string('x', 65537));
            if (new LocalConfiguration(file).Error == null) throw new Exception("Oversized config accepted");
            Console.WriteLine("Configuration checks passed: API key loading and caption preference round trip, validation, and key exclusion.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previousGemini);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", previousGoogle);
            File.Delete(file);
        }
    }
}
