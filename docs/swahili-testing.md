# Swahili caption testing

Test translation first, then speech recognition and caption timing. The pack contains 12 parallel Swahili/English text pairs and a 157.16-second audio playlist. The audio hashes and RIFF durations were independently verified; recordings are mono 16 kHz float32 WAV.

## Text translation comparison

From the repository directory:

```sh
python3 scripts/evaluate-swahili.py --self-test
python3 scripts/evaluate-swahili.py --provider gemini --model gemini-3.8-flash --run
python3 scripts/evaluate-swahili.py --provider gemini --model gemini-3.1-pro-preview --run
```

The script reads the existing project `.env` internally, with environment variables taking precedence. It sends only each Swahili source sentence, independently, and saves the English reference beside the output for review. Without `--run`, it produces a dry-run report without loading credentials or contacting Google. Calls are capped at 12; there are no automatic retries. `--limit 1` tests access first. Each run writes `results.json` and `review.md` under `artifacts/swahili-eval/`.

Gemini requests use low thinking and a 4,096-token cap. The cap also covers reasoning: the initial 512-token test truncated several outputs and must not be used to rank translation quality. See Google's [thinking and token-limit documentation](https://ai.google.dev/gemini-api/docs/thinking).

For Translation LLM, supply the Cloud project associated with an appropriately authorized key:

```sh
python3 scripts/evaluate-swahili.py --provider tllm --project YOUR_PROJECT_ID --limit 1 --run
```

The script does not enable APIs, change key restrictions, or configure billing. A Gemini key may not have Cloud Translation access.

Review preservation of meaning, names, numbers, negation, omissions, and invented content. Different wording can be correct. These selected FLEURS sentences and their sometimes awkward source/reference text are a small comparison set, not a representative seminar benchmark. Request elapsed time is text API response time, not live caption delay.

## Audio through BlackHole

1. Open the bundled CapFast app, paused. Select **BlackHole 2ch** in the bottom-left audio input dropdown.
   In Settings, keep **Swahili transcription → English translation** for the new pipeline, or choose **Live Translate (comparison)** to repeat the direct service test.
2. In macOS Sound settings, select **BlackHole 2ch** as the output device for playback. Note the previous output so you can restore it afterward.
3. Focus the caption window and press **Space** to start. In Terminal, from the repository directory, play the playlist:

   ```sh
   afplay swahili-pack/playlist.wav
   ```

4. Watch captions through all 12 clips, then press **Space** to pause. Restore your previous Mac output device.

Use the clip offsets in `swahili-pack/manifest.json` and the numbered reference files to identify mistakes. Repeat individual clips with `afplay swahili-pack/NUMBERED_FILE.wav` when useful. Allow the final caption to finish before pausing.

Record dropped phrases, meaning errors, time from speech to usable English, repeated text, and disruptive changes to already displayed captions. The app does not export live transcripts, so this is a manual display check. The new default pipeline uses Transcribe Live and Flash; direct Live Translate remains selectable for comparison. Complete source sentences translate immediately after recognition finalizes; unfinished source fragments can wait up to three seconds for a continuation. Recognition finalization and text translation add delay beyond that buffering.

Dataset attribution and selection details are in [the pack README](../swahili-pack/README.md).

## First text run

The [2026-10-09 comparison](../artifacts/swahili-eval/comparison.md) contains all 12 references beside Gemini Flash and Pro outputs. Both corrected runs completed all 12 translations; median text response times were 1.65 seconds for Flash and 5.26 seconds for Pro. Flash looks worth testing as a separate translation stage, with ambiguous wording still requiring review. This does not measure live captions or establish a general quality winner. The single Translation LLM access check returned HTTP 403, so it has no translation results yet.

## Audio replay tools

The console harness links the app's actual setup, transcript parsers, source buffering, and Flash translator. It replays file audio into Google's WebSocket at real-time speed, independently per clip; this bypasses microphone capture and the UI.

```sh
dotnet build tools/CapFast.AudioEval/CapFast.AudioEval.csproj
dotnet tools/CapFast.AudioEval/bin/Debug/net10.0/CapFast.AudioEval.dll --run --mode live --output artifacts/my-live-test
dotnet tools/CapFast.AudioEval/bin/Debug/net10.0/CapFast.AudioEval.dll --run --mode twostage --output artifacts/my-two-stage-test
```

Output directories must be new. Without `--run`, audio validation performs no requests. `--clip-id 1581` selects one clip. `--normalize-audio` optionally raises/lowers the derived float samples to peak 0.8 before conversion; it preserves originals and is a diagnostic option, not an app feature. Some pack clips are very quiet (about −66 dBFS RMS), which can cause omissions in both models. Raw and amplified results must be reported separately.

For the supplied talk, [the extraction manifest](../sampleTalk/extracted/manifest.json) records the original file, MP3, sample offsets, levels, and exact commands. The three 30-second samples start at 0:30, 6:33, and 11:37. The supplied Swahili transcript is untimed and there is no aligned English reference.

```sh
dotnet tools/CapFast.AudioEval/bin/Debug/net10.0/CapFast.AudioEval.dll --run --mode twostage --manifest sampleTalk/extracted/manifest.json --output artifacts/my-talk-test
```

To play a sample into BlackHole without changing the Mac's default output, the optional local Swift helper is `artifacts/PlayToDevice` (source: `tools/PlayToDevice.swift`). After starting CapFast with BlackHole selected:

```sh
artifacts/PlayToDevice --play sampleTalk/extracted/middle-30s.wav BlackHole2ch_UID
```

The bundled app was tested through BlackHole with clip 1570: it displayed “It also had no authority to override interstate laws and tariff schedules.” Space then paused capture while retaining the caption. Longer seminar use and automatic reconnection are still unvalidated; Transcribe Live currently requires a restart at its session limit of up to 10 minutes.

The [audio-pack comparison](../artifacts/swahili-audio/comparison.md) records all 12 raw tests and separate amplified diagnostics. The [sample-talk comparison](../artifacts/sample-talk/comparison.md) records the three 30-second samples, timing, both English outputs, and recognized Swahili. All 13 source segments in the talk samples were translated; two-stage was generally more readable, with remaining proper-name recognition mistakes. The final bundled app also displayed captions from the 6:33 sample through BlackHole and paused successfully afterward.
