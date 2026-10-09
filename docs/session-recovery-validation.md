# Google connection recovery and sparse long-duration test

Connections rotate at eight minutes or when `goAway` arrives. There are at most three transient recovery attempts with 0.5/1/2-second backoff and eight-second connection/setup deadlines. Startup retains a twenty-second total deadline. A bounded 200-frame channel holds at most twenty seconds of classified audio; exhausted retries or queue overflow produce a clear terminal error. Fresh connections use fresh transcript IDs.

This change separates the lifetime of local capture and translation from a Google Live WebSocket connection. Session rollover must retain the current speech gate, buffered qualified audio, queued text translations, and visible captions. Authentication, quota, configuration, and unknown policy errors remain terminal. Transport retries and queued audio must have finite bounds; this is recovery, not a promise of lossless ASR through an outage.

## Sparse test protocol

Run the rebuilt Mac app with BlackHole selected and transcription active. The following scheduler plays the supplied 30-second seminar excerpt into BlackHole at minutes 0, 5, 10, …, 60, and finishes at minute 65:

```sh
python3 scripts/replay-session-soak.py --run --output artifacts/session-soak/run.json
```

Omitting `--run` only validates the schedule and writes its plan. Reports must use a new output path. The player addresses `BlackHole2ch_UID` directly; it does not change the Mac's default input/output or capture a physical microphone. The schedule contains thirteen bursts and at most 390 seconds of source audio. Silence stays local. The report records playback timing and helper exit codes, without credentials or transcripts.

The scheduler does not inspect app health. Observe the caption window separately after each burst: it should produce new English, return to the idle indicator, retain operator control, and remain active. A completed playback schedule alone is insufficient to declare the app test passed.

Because planned idle closure releases each connection between these bursts, the sparse test does not exercise eight or ten minutes of continuous speech on one connection. Deterministic transport/time tests must separately cover rollover, goAway, transient interruption, cancellation, stale events, opening audio order, and retry exhaustion. Unexpected disconnects can leave provider acceptance of the last audio unknown; replaying it can duplicate content and discarding it can lose content. Record this limitation instead of claiming lossless recovery.

## Translation requests

The existing bounded translation prompt and source context remain in use. Temporary HTTP transport errors and selected 5xx responses can retry at most twice after the initial attempt, within the original twenty-second request deadline. Authentication, quota, invalid configuration, incomplete/blocked model output, and invalid response bodies are not retried. Translation context advances only after a valid successful response. The same source can reach Google more than once during a retry; if an interrupted response was already generated remotely, it can be billed even though only one caption is accepted locally.

Research on session support, context limits, caching, and cost is in [google-session-caching-research.md](google-session-caching-research.md). The short instruction plus bounded context is below the current cache minimum; retaining a 100,000-token conversation would increase processing rather than make those tokens free.

## Verification record

Complete executable checks and the app build passed, with zero warnings/errors. Independent review approved the lifecycle and translation recovery. Fake transports drive the actual audio pump through eight-minute rotation, goAway, ambiguous sends, transient receive loss, terminal auth/quota/policy errors, stale captions, fresh utterance IDs, finite retry exhaustion, and pause during connection setup. HTTP handler checks exercise unchanged translation retries, preserved context, terminal errors, cancellation, and transport interruption.

The packaged sparse test completed on October 9, 2026 in 3,900.001 seconds. All thirteen scheduled 30-second BlackHole playbacks exited successfully, for 390 seconds of source audio. Forty separate UI observations recorded seven speech states, thirty-two idle states, and one brief music classification; none recorded an unexpected paused state. Caption text was observed changing across the scheduled run (eighteen changes after signature monitoring began). The app produced fresh English through the final burst, returned to idle, and its error viewer showed “No errors recorded.” These observations establish sustained operation and repeated caption output, not completeness of every utterance or a latency benchmark.

The same app process remained running for over sixty-seven minutes without manual restart. Around minute fifty-two a stale computer-use window handle failed; rebinding the monitor restored inspection, and process uptime confirmed that the app itself had not restarted. The capture helper stopped normally when transcription was manually paused after completion. Local artifacts are `artifacts/session-soak/20261009-recovery-65min.json` (playback schedule) and `artifacts/session-soak/20261009-real-rollover.json` (targeted probe); artifacts contain no keys or transcript text and are not committed.

## Targeted real-cloud rollover probe

After the sparse schedule finishes, a separate opt-in probe uses at most thirty seconds of the production classifier's framed seminar audio. It uses real Google sockets, the production audio pump, and the real text translator. After seventy successfully uploaded 100 ms frames, the injected local session clock advances by 481 seconds. This forces proactive rollover during speech without streaming eight minutes of paid audio. It checks for two connections and English captions from both unique input-parser sessions, then disposes workers before saving a report of counts and fixed diagnostics. No microphone, transcript text, credentials, or raw service responses are recorded. Local quiet frames allow finalization and are never uploaded. This tests real fresh-session handoff, not actual server expiry or session-resumption compatibility. The same injected clock also drives source-stability deadlines, so the probe does not validate caption latency or segmentation timing.

```sh
dotnet run --project tools/CapFast.RolloverProbe/CapFast.RolloverProbe.csproj -- \
  --run --output artifacts/session-soak/rollover-probe.json
```

Omit `--run` for input validation without cloud requests. Run this at the next five-minute slot after the sparse test's last burst, so the audio budget remains thirty seconds per five minutes. Together, thirteen sparse bursts and one targeted burst contain at most seven minutes of source audio.

The real-cloud probe passed on October 9, 2026: two connections, 23 seconds of qualified audio uploaded from a 30-second source, and five accepted English captions originating from both parser sessions. It completed in 55.175 seconds with no terminal failure. It recorded one expected diagnostic: “Google connection refreshed; unfinished transcription may be incomplete.” Thus a real fresh-connection handoff was verified, but unfinished ASR remains best effort; this is not evidence of lossless rollover or actual ten-minute server expiry.

The latest Apple Silicon bundle passed strict signature and property-list verification and was reopened empty and paused, with BlackHole selected. Independent review approved the implementation and probe; executable checks and builds passed.
