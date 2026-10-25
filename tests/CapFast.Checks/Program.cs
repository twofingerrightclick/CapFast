using CapFast.Core;

if (args.Length >= 3 && args[0] == "--local-speech-report")
{
    var reports = args.Skip(2).Select(path => LocalSpeechChecks.MeasurePcm(path)).ToArray();
    using var output = new FileStream(args[1], FileMode.CreateNew);
    System.Text.Json.JsonSerializer.Serialize(output, reports, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine($"Measured {reports.Length} PCM fixtures: {args[1]}");
    return;
}

if (args.Length > 0 && args[0] == "--local-speech-checks")
{
    LocalSpeechChecks.Run();
    foreach (var path in args.Skip(1)) LocalSpeechChecks.RunPcm(path);
    return;
}
LocalSpeechChecks.Run();

AudioMonitorChecks.Run();
SlideshowChecks.Run();

if (args.Length == 1 && args[0] == "--configuration-checks")
{
    ConfigurationChecks.Run();
    return;
}

if (args.Length == 2 && args[0] == "--speech-gate-fixtures")
{
    SpeechAudioGateChecks.RunFixtures(args[1]);
    return;
}

static void Equal(string expected, string actual)
{
    if (actual != expected) throw new Exception($"Expected '{expected}', got '{actual}'");
}

var buffer = new CaptionBuffer();
buffer.Apply(new("1", "Welcome to the meeting", false));
buffer.Apply(new("1", "Welcome to the seminar", false));
Equal("Welcome to the seminar", buffer.Text);
buffer.Apply(new("1", "Welcome to the seminar.", true));
Equal("Welcome to the seminar.", buffer.Text);
buffer.Apply(new("1", "Welcome to the seminar.", true));
buffer.Apply(new("1", "late partial", false));
Equal("Welcome to the seminar.", buffer.Text);
buffer.Apply(new("2", "We are", false));
Equal("Welcome to the seminar. We are", buffer.Text);
// A final for an earlier utterance must not erase the newer active phrase.
buffer.Apply(new("other", "Thank you.", true));
Equal("Welcome to the seminar. Thank you. We are", buffer.Text);
buffer.Apply(new("2", "We are ready.", true));
Equal("Welcome to the seminar. Thank you. We are ready.", buffer.Text);
buffer.Apply(new("no-match", "Uncertain provisional words", false));
buffer.Apply(new("no-match", "", true));
Equal("Welcome to the seminar. Thank you. We are ready.", buffer.Text);
Equal("three four", CaptionBuffer.Tail("one two three four", 2));
buffer.Apply(new("3", string.Join(" ", Enumerable.Range(1, 100)), false));
if (buffer.Text.Split(' ').Length > 42) throw new Exception("Caption history is unbounded.");
buffer.Clear();
Equal("", buffer.Text);
buffer.Apply(new("2", "duplicate final after clear", true));
Equal("", buffer.Text);
var emojiTail = CaptionBuffer.Tail(string.Concat(Enumerable.Repeat("👋🏽", 1000)), 42);
if (System.Globalization.StringInfo.ParseCombiningCharacters(emojiTail).Length != 512)
    throw new Exception("Long token limit split a text element or failed to bound the tail.");
CaptionFilterChecks.Run();
CaptionLayoutChecks.Run();
CaptionWordRevealChecks.Run();
SpeechAudioGateChecks.Run();
AudioConnectionPolicyChecks.Run();
await SourceReaderChecks.RunAsync();
await TwoStageChecks.RunAsync();
await LiveRecoveryChecks.RunAsync();
await TranslationRecoveryChecks.RunAsync();
await CombinedTranslationChecks.RunAsync();
await TranslationCommentaryChecks.RunAsync();
await ControllerChecks.RunAsync();
ConfigurationChecks.Run();
Console.WriteLine("Caption checks passed: revisions, finalization, duplicate results, bounded Unicode history, and clear.");
