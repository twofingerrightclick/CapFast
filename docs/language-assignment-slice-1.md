# FEAT-007 slice 1 validation

Google TwoStage now requests automatic language detection by omitting `languageCodes`, retains transcript `languageCode`, assigns the first detected source per speech block, and translates into the other two languages among English, Swahili, and French. Inputs outside that trio use English and French. Other-language transcripts keep speech activity alive but are excluded from translation. Five seconds without speech (including music activity) returns assignment to Auto. Reset and one-block input hint hooks are available on the session and controller.

Output metadata includes source, primary and secondary language codes plus assignment epoch. Slice 2 adds the Settings role console, Reset command, Detecting state, and two dynamic audience surfaces. A continuing output language keeps its physical surface, content and animation; only a vacated output clears. Queued and in-flight results from obsolete assignments are withheld. Translation contexts are fresh per assignment.

## Checks

`dotnet run --project tests/CapFast.Checks --no-restore` passed, including assignment normalization, trio and outside-trio routing, translator exclusion, music/silence reset, hint expiry, unknown language withholding, parser metadata, and actual pipeline routing. Existing caption, recovery, French, and controller checks also passed.

`dotnet build src/CapFast/CapFast.csproj --no-restore -p:UsedAvaloniaProducts=` passed. Release osx-arm64 publish into the existing app bundle also passed. Existing NuGet audit warning reports unreachable api.nuget.org. The telemetry opt-out property avoids Avalonia writing outside sandbox roots.

## Review regression fixes

Assignment now observes the raw audio classification independently of the permissive capture gate. Segment alignment survives reset/hint while submitted chunk IDs include assignment epoch. Missing language metadata inherits only a known language for that utterance. Expired utterances and transcripts received after the audio block expires are excluded. Transcript receipt does not extend the local speech clock. Assignment cancellation releases obsolete in-flight work and isolates its eventual faults, including translators that ignore cancellation.

Focused regression checks cover music with permissive gate behavior, delayed final rejection, obsolete known utterances after speech resumes, ignored cancellation, obsolete failures, missing-language continuation/final, translator exclusion, and reset alignment without repeated consumed words or colliding IDs.

At the five-second gap, TwoStage advances the audio generation and retires the recognition connection. Every transcript carries its connection’s originating generation into pipeline acceptance. A previously unseen late final therefore cannot seize the next block. The app waits locally for raw speech, then requalifies and connects a fresh stream. Finals arriving within the five-second grace remain eligible; slower obsolete finals are withheld once the block ends. This adds fresh-connection setup cost after each long gap, but consumes no failure retry budget and prevents cross-block ambiguity. A fake transport regression verifies old stream disposal, correct next-language assignment, and preservation of all qualified speech frames. Actual provider event timing remains unvalidated because app preflight stalled.

## App attempt

Launched the updated bundled app using computer control, selected BlackHole 2ch and started captions. The app remained at “Checking Mac microphone permission…” without a visible permission prompt, further event, or captions. Paused successfully afterward. Sandbox playback initially returned `OUTPUT_DEVICE_UNAVAILABLE`; authorized escalated playback of `early-eng-12s.wav` returned `PLAYBACK_COMPLETE`. No provider transcript events were observed, so live languageCode metadata and translation behavior remain unvalidated.

## Sources

[Google Live transcription guide](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe) confirms Auto when language codes are omitted or empty. [Official Google Gen AI SDK reference](https://googleapis.github.io/python-genai/) lists `Transcription.language_code`.
