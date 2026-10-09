using CapFast.Core;

public static class AudioConnectionPolicyChecks
{
    public static void Run()
    {
        static void Expect(AudioConnectionPolicy p, double now, AudioConnectionDecision expected, string reason)
        {
            if (p.Poll(now) != expected) throw new Exception(reason);
        }
        var silent = new AudioConnectionPolicy(0);
        Expect(silent, 19.9, AudioConnectionDecision.Keep, "Startup silence closed too soon.");
        Expect(silent, 20, AudioConnectionDecision.CloseIdle, "Startup silence retained an unused cloud connection.");
        var speech = new AudioConnectionPolicy(0);
        speech.ObserveActivity(AudioActivity.Speech, 19);
        Expect(speech, 30, AudioConnectionDecision.Keep, "Qualifying speech was interrupted by idle close.");
        speech.Uploaded(30);
        speech.Transcript(false, 30.1);
        speech.Ended(31);
        speech.ObserveActivity(AudioActivity.Idle, 31);
        Expect(speech, 51, AudioConnectionDecision.Keep, "Unfinished ASR was discarded after idle.");
        speech.Transcript(true, 52);
        Expect(speech, 52, AudioConnectionDecision.CloseIdle, "Completed speech did not release an idle connection.");
        var missingFinal = new AudioConnectionPolicy(0);
        missingFinal.Uploaded(1);
        missingFinal.Ended(2);
        missingFinal.ObserveActivity(AudioActivity.Idle, 2);
        Expect(missingFinal, 31.9, AudioConnectionDecision.Keep, "ASR drain timed out too soon.");
        Expect(missingFinal, 32, AudioConnectionDecision.TranscriptionTimeout, "Missing final silently lost accepted speech.");
        var staleFinal = new AudioConnectionPolicy(0);
        staleFinal.Uploaded(1);
        staleFinal.Transcript(true, 2);
        staleFinal.Uploaded(3);
        staleFinal.Ended(4);
        staleFinal.ObserveActivity(AudioActivity.Idle, 4);
        Expect(staleFinal, 24, AudioConnectionDecision.Keep, "Earlier final incorrectly covered newly uploaded speech.");
        staleFinal.Transcript(true, 25);
        Expect(staleFinal, 25, AudioConnectionDecision.CloseIdle, "Fresh final failed to complete pending speech.");
        var preEndFinal = new AudioConnectionPolicy(0);
        preEndFinal.Uploaded(1);
        preEndFinal.Transcript(true, 1.5);
        preEndFinal.Ended(2);
        preEndFinal.ObserveActivity(AudioActivity.Idle, 2);
        Expect(preEndFinal, 22, AudioConnectionDecision.CloseIdle, "Final preceding the end marker was incorrectly rejected.");
        preEndFinal.ObserveActivity(AudioActivity.Speech, 21);
        Expect(preEndFinal, 23, AudioConnectionDecision.Keep, "Resumed speech was interrupted by the prior idle deadline.");
        Console.WriteLine("Audio connection checks passed: idle close, ASR drain, timeout, fresh final coverage, and resumed speech.");
    }
}
