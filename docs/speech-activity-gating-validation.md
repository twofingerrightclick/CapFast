# Speech activity gate — October 9, 2026

> Historical note: music detection, rejection, and caption suppression have been removed. Only local speech detection remains; operators pause captions during music. Music-related findings below describe the earlier implementation.

The previous working caption implementation is preserved in commit `b079627`. Google Mac capture now uses Apple SoundAnalysis on-device, with one-second classification windows overlapping by 50%. Classifier availability and the exact labels `speech`, `music`, `singing`, and `choir_singing` were verified with a compiled metadata probe. CoreML execution requires running outside the tool sandbox; the packaged app runs normally. No separate downloaded inference model or cloud classification service is used.

Each 100 ms PCM frame is released only after its overlapping classification windows report. Speech confidence must reach 0.5. Music/singing confidence of 0.5 independently vetoes a frame, even when speech is also detected. A very-low-energy check only rejects silence; it never qualifies speech by itself. Unknown/noise is rejected. A classifier initialization/inference failure stops capture with a curated error, without reverting to an energy-only filter.

The C# gate requires 20 classified speech frames, buffers the opening, and allows up to three quiet frames between speech frames. Quiet time does not count toward qualification. Four quiet frames end an accepted audio segment; music ends it immediately and clears any candidate. An `audioStreamEnd` message is sent once to request finalization. Final transcription and pending translation may finish during idle; no new silence-origin audio or translation is sent.

After 20 seconds of idle, an unused connection closes locally. A used connection may close once no partial remains and a final has arrived after the last upload. The next qualified speech opens a fresh connection before uploading its retained opening. Capture, queued translations, and translation context survive the planned close. Reconnect setup is bounded to ten seconds. Missing finalization is reported after 30 seconds from the end marker; unknown policy/authentication failures are not retried automatically. Final receipt timing is a drain heuristic, not proof that every audio segment finalized: Google does not document a correlated stream-end acknowledgement, and a delayed earlier final could conceal an unreported later segment. See the research note for this limitation.

## Local classifier and actual gate fixtures

The actual compiled native helper classified local WAV files without microphone access or cloud transmission. Its framed output was replayed through the production C# gate using:

```
dotnet run --project tests/CapFast.Checks/CapFast.Checks.csproj --no-restore -- --speech-gate-fixtures artifacts/speech-gate
```

| Fixture | Duration classified | Audio approved for upload |
| --- | ---: | ---: |
| Synthetic silence | 6.0 s | 0.0 s |
| Seeded white noise | 6.0 s | 0.0 s |
| 440 Hz tone | 6.0 s | 0.0 s |
| Licensed instrumental trumpet | 5.3 s | 0.0 s |
| Licensed vocal song | 6.0 s | 0.0 s |
| Speech + instrumental, equal RMS | 6.0 s | 0.0 s |
| Speech + instrumental, quarter music RMS | 6.0 s | 0.0 s |
| Seminar middle sample | 30.0 s | 23.0 s |
| Seminar early sample | 30.0 s | 27.0 s |
| Seminar late sample | 30.0 s | 26.5 s |

Approved durations include brief retained gaps, not just voiced phonemes. Originals are preserved. Test fixtures and framed output are ignored artifacts under `artifacts/speech-gate`; public music attribution/license details are in the research note. The mixture fixtures use the supplied Swahili talk and licensed trumpet with RMS-normalized ratios. These are limited examples, not accuracy rates.

The initial 0.15 music threshold rejected much of the middle talk. Raising it to 0.65 avoided a false music interval but permitted mixed music to qualify for upload, so that threshold was rejected. The selected 0.5 threshold rejected the tested mixtures, but falsely flagged one second of the middle talk as music. Under the user's strict policy that interval is skipped. All 12 normalized Swahili-pack recordings had a qualifying speech span; several unusually quiet originals did not. There is no automatic audio gain normalization in capture.

## Verification and limits

Pure checks cover silence, two-second qualification, opening retention/order, music reset, short pauses, exactly-once stream end, malformed frames, and bounded storage. Connection checks cover startup idle, pending partials, missing finals, stale finals, and resumed speech. Controller checks cover activity delivery and stale callbacks after pause. Independent review covered native overlap accounting, exact singing labels, offline EOF/completion, fixed classifier errors, and indicator timer cleanup. Complete checks and application builds passed with zero warnings/errors.

Classifier and qualification buffering add latency. Replies shorter than two seconds are intentionally discarded. Music detection is probabilistic; faint or unfamiliar music can be missed and speech can be rejected. Google Mac capture is covered; Windows/Linux capture remains unsupported.

## Packaged runtime

The Apple Silicon app opened paused with its local configuration loaded. BlackHole playback of the vocal song produced no captions. A supplied 30-second Swahili seminar excerpt produced English captions; speech showed three blue dots, and quiet showed the single blue dot. An initial longer test encountered Google WebSocket 1008 after setup; its exact cause was not established and is not claimed as a confirmed idle timeout.

After introducing planned idle closure, the app remained listening through startup silence, captioned the 30-second seminar sample, stayed active through a longer quiet interval, and captioned a subsequent six-second speech sample without manually restarting. These observations verify the tested path, not full seminar operation or guaranteed drain coverage. Independent lifecycle review found no additional blocking race or resource issue and accepted the documented drain heuristic. The final bundle passed strict code-signature and Info.plist verification.

## Music display timeout

Detected music now has its own activity state and a blue music-note icon in the same corner. Ten continuous seconds of that state clear both the controller history and the presentation queue, including animation state. Pending translations cannot refill the display while music continues after the timeout. Returning to silence or speech resets the timer and allows captions again; pausing cancels the timer and retains captions. Fake-clock controller checks cover the ten-second boundary, repeated music notifications, interrupted music, late translations, speech resumption, and pause cancellation.

The rebuilt Apple Silicon app was seeded with the offline caption preview, then given 15 seconds of licensed music through BlackHole. The blue music-note icon appeared, the caption surface became empty during music, and the app returned to its idle dot after playback. It was left empty and paused with BlackHole selected. Additional checks cover an already queued timer callback invalidated by pause or by a newer music interval.
