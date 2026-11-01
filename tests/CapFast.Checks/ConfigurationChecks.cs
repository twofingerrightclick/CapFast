using CapFast.Core;
using CapFast.Speech;

internal static class ConfigurationChecks
{
    public static void Run()
    {
        SecureKeyChecks.Run();
        var rejected = GeminiCaptionSession.DescribeCloseFailure(1011, "Model not found secret-value", false);
        if (!rejected.Contains("model is unavailable") || rejected.Contains("secret-value")) throw new Exception("Close reason classification leaked or lost model error");
        var unknown = GeminiCaptionSession.DescribeCloseFailure(1011, "secret-value", true);
        if (!unknown.Contains("after setup") || unknown.Contains("secret-value")) throw new Exception("Unknown close reason leaked or lost setup stage");
        var file = Path.Combine(Path.GetTempPath(), "capfast-env-" + Guid.NewGuid() + ".env");
        var savedKeys = new SavedGoogleKeyStore(file + ".dpapi");
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
                    EnglishReadingWordsPerMinute = 200, SwahiliReadingWordsPerMinute = 160,
                    FrenchReadingWordsPerMinute = 190, AutomaticCaptionCatchUp = false,
                    SpeechQualificationSeconds = 1.5, SilenceResetSeconds = 1.2,
                    GeminiKey = "secret-must-not-be-saved"
                };
                CaptionPreferences.Save(saved, preferencesFile);
                var reloaded = CaptionPreferences.Load(preferencesFile);
                if (reloaded.AudioDeviceId != saved.AudioDeviceId || reloaded.FontSize != saved.FontSize
                    || reloaded.ReadingWordsPerMinute != saved.ReadingWordsPerMinute
                    || reloaded.EnglishReadingWordsPerMinute != 200 || reloaded.SwahiliReadingWordsPerMinute != 160
                    || reloaded.FrenchReadingWordsPerMinute != 190 || reloaded.AutomaticCaptionCatchUp
                    || reloaded.SpeechQualificationSeconds != 1.5 || reloaded.SilenceResetSeconds != 1.2)
                    throw new Exception("Audio input or caption display preferences did not survive reload.");
                if (File.ReadAllText(preferencesFile).Contains(saved.GeminiKey))
                    throw new Exception("API key leaked into caption preferences.");
                File.WriteAllText(preferencesFile,
                    """{"audioDeviceId":"old-input","fontSize":52,"readingWordsPerMinute":210}""");
                var legacy = CaptionPreferences.Load(preferencesFile);
                if (legacy.AudioDeviceId != "old-input" || legacy.FontSize != 52 || legacy.ReadingWordsPerMinute != 210
                    || legacy.EnglishReadingWordsPerMinute is not null || legacy.SwahiliReadingWordsPerMinute is not null
                    || legacy.FrenchReadingWordsPerMinute is not null || !legacy.AutomaticCaptionCatchUp
                    || legacy.SpeechQualificationSeconds != 2 || legacy.SilenceResetSeconds != 1)
                    throw new Exception("Older saved preferences lost shared settings or did not enable the new defaults.");
                File.WriteAllText(preferencesFile,
                    """{"audioDeviceId":"input","fontSize":48,"readingWordsPerMinute":210,"englishReadingWordsPerMinute":79,"swahiliReadingWordsPerMinute":301,"frenchReadingWordsPerMinute":null,"automaticCaptionCatchUp":false}""");
                var invalidOverrides = CaptionPreferences.Load(preferencesFile);
                if (invalidOverrides.ReadingWordsPerMinute != 210 || invalidOverrides.EnglishReadingWordsPerMinute is not null
                    || invalidOverrides.SwahiliReadingWordsPerMinute is not null || invalidOverrides.FrenchReadingWordsPerMinute is not null
                    || invalidOverrides.AutomaticCaptionCatchUp)
                    throw new Exception("Invalid language overrides did not fall back independently to the shared speed.");
                var speeds = new CaptionSettings(loadLocalConfiguration: false) { ReadingWordsPerMinute = 210 };
                if (!speeds.AutomaticCaptionCatchUp || new[] { "en", "sw", "fr", "de", "auto", null }
                    .Any(code => speeds.ReadingSpeedFor(code) != 210))
                    throw new Exception("Default language reading speeds did not inherit the shared speed.");
                speeds.EnglishReadingWordsPerMinute = 80;
                speeds.SwahiliReadingWordsPerMinute = 300;
                speeds.FrenchReadingWordsPerMinute = 190;
                speeds.ReadingWordsPerMinute = 225;
                if (speeds.ReadingSpeedFor("EN-us") != 80 || speeds.ReadingSpeedFor("sw_KE") != 300
                    || speeds.ReadingSpeedFor("fr-FR") != 190 || speeds.ReadingSpeedFor(null) != 225)
                    throw new Exception("Language override resolution or boundary validation failed.");
                var copied = speeds.Copy();
                speeds.EnglishReadingWordsPerMinute = 79;
                speeds.SwahiliReadingWordsPerMinute = 301;
                speeds.FrenchReadingWordsPerMinute = null;
                if (speeds.EnglishReadingWordsPerMinute is not null || speeds.SwahiliReadingWordsPerMinute is not null
                    || speeds.ReadingSpeedFor("en") != 225 || speeds.ReadingSpeedFor("fr") != 225
                    || copied.ReadingSpeedFor("en") != 80 || copied.ReadingSpeedFor("sw") != 300
                    || copied.ReadingSpeedFor("fr") != 190 || !copied.AutomaticCaptionCatchUp)
                    throw new Exception("Invalid overrides or settings snapshots changed independent reading speeds.");
                CaptionPreferences.Save(speeds, preferencesFile);
                var inherited = CaptionPreferences.Load(preferencesFile);
                if (inherited.EnglishReadingWordsPerMinute is not null || inherited.SwahiliReadingWordsPerMinute is not null
                    || inherited.FrenchReadingWordsPerMinute is not null || !inherited.AutomaticCaptionCatchUp)
                    throw new Exception("Inherited reading speeds or enabled catch-up did not survive reload.");
                File.WriteAllText(preferencesFile,
                    """{"audioDeviceId":"input","fontSize":48,"readingWordsPerMinute":210,"speechQualificationSeconds":0,"silenceResetSeconds":99}""");
                var invalidTiming = CaptionPreferences.Load(preferencesFile);
                if (invalidTiming.SpeechQualificationSeconds != 2 || invalidTiming.SilenceResetSeconds != 1
                    || invalidTiming.ReadingWordsPerMinute != 210 || invalidTiming.AudioDeviceId != "input")
                    throw new Exception("Invalid speech timings lost valid preferences or failed to use tested defaults.");
                var timing = new CaptionSettings(loadLocalConfiguration: false)
                    { SpeechQualificationSeconds = .5, SilenceResetSeconds = 3 };
                var timingCopy = timing.Copy();
                timing.SpeechQualificationSeconds = 4;
                timing.SilenceResetSeconds = .2;
                CaptionPreferences.Save(timing, preferencesFile);
                var timingLimits = CaptionPreferences.Load(preferencesFile);
                if (timingLimits.SpeechQualificationSeconds != 4 || timingLimits.SilenceResetSeconds != .2
                    || timingCopy.SpeechQualificationSeconds != .5 || timingCopy.SilenceResetSeconds != 3)
                    throw new Exception("Speech timing limits did not persist or the session snapshot changed after an edit.");
                timing.SpeechQualificationSeconds = 1.56;
                timing.SilenceResetSeconds = .56;
                if (timing.SpeechQualificationSeconds != 1.6 || timing.SilenceResetSeconds != .6)
                    throw new Exception("Speech timing values did not align to classified 100 ms frames.");
                timing.SpeechQualificationSeconds = double.NaN;
                timing.SilenceResetSeconds = double.PositiveInfinity;
                if (timing.SpeechQualificationSeconds != 2 || timing.SilenceResetSeconds != 1)
                    throw new Exception("Non-finite speech timings did not fall back to tested defaults.");
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
            var config = new LocalConfiguration(file, savedKeyStore: savedKeys);
            if (config.GoogleKey != "local-value" || config.Get("UNKNOWN") != "")
                throw new Exception("Local config parse failed");
            if (new LocalConfiguration(file, readValues: false).GoogleKey != "") throw new Exception("Deferred configuration read local contents");
            var deferred = new CaptionSettings(loadLocalConfiguration: false) { IsLoadingConfiguration = true, GeminiKey = "manual-google" };
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
            if (new LocalConfiguration(file, savedKeyStore: savedKeys).Error == null) throw new Exception("Oversized config accepted");
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
