# Silero sample-audio validation — October 25, 2026

**Follow-up, November 1:** the two-second qualification remains, but the quiet
reset is now one second. Fresh replay reduced full-talk gate loss to 2.50% for
English and 1.23% for Swahili. See [pause-retention validation](speech-pause-retention-validation.md)
for the change, regression checks, and remaining short-clip losses. The results
below describe the original 400 ms reset.

The local detector works on the supplied recordings, but the retained two-second
speech gate discards substantial amounts of audio that Silero labels as speech.
The complete English talk loses 78.0 seconds of detected speech at the gate
(14.9% of its speech labels); the Swahili talk loses 29.6 seconds (5.1%). This is
not a measured transcription error rate and does not prove the cause of earlier
live caption gaps. It is a reproducible local source of missing input audio.

No production thresholds or gate behavior were changed during this evaluation.

## What to use

| Set | Location | Purpose |
| --- | --- | --- |
| 12 human-read Swahili clips and parallel text references | `swahili-pack/manifest.json`, numbered WAVs and reference JSONs | Different voices/topics/levels; test originals first |
| Swahili playlist, 157.16 s | `swahili-pack/playlist.wav` | Continuous detector state across quiet and loud speakers; one-second inserted gaps |
| Six 12 s English/Swahili excerpts | `sampleTalk/alternating-snippets/manifest.json` | Fast per-language checks, early/middle/late positions |
| 77 s alternating playlist | `sampleTalk/alternating-snippets/alternating-eng-swa-77s.wav` | Turn changes with one-second gaps |
| 83 s alternating playlist | `sampleTalk/alternating-snippets/alternating-eng-swa-long-pause-83s.wav` | Seven-second gap at 38–45 s; long enough to exercise the five-second language inactivity rule in a full pipeline test |
| Three 30 s Swahili seminar excerpts | `sampleTalk/swa-extracted/manifest.json` | Longer utterances and natural pauses |
| Both complete talks, about 13:07 each | `sampleTalk/2026-04-1050-clark-g-gilbert-360p-{eng,swa}.mp4` | Sustained detector/gate state; avoids relying only on cut excerpts |
| Silence, noise, tone, licensed music and speech/music mixtures | `artifacts/speech-gate/*.wav` | Negative controls and observational mixed-audio checks |

There is no French source in these sets. The excerpts can start/end mid-sentence,
and matching source offsets do not establish aligned translations. The supplied
Swahili transcript is untimed. Do not treat speech-label percentages as accuracy.
Audio under `artifacts/` is local test material, not a tracked download fixture;
see the historical gate research for music attribution and preparation details.
The old `sampleTalk/extracted/` location referenced by earlier reports is absent
in this checkout; use the existing `sampleTalk/swa-extracted/` files.

## Reproduce

```sh
dotnet restore tests/CapFast.Checks
python3 scripts/evaluate-local-speech.py --full-talks --output artifacts/local-speech-next-run
```

Use a new output directory each time. Omit `--full-talks` for a shorter run.
The script uses ffmpeg from PATH or the existing `artifacts/pythonmedia`
installation; `--ffmpeg /path/to/ffmpeg` overrides it. It fails on missing source
files and verifies manifest checksums for the numbered FLEURS and six excerpts.

The run generates fresh PCM derivatives, source hashes/levels, model checksum,
per-frame speech/upload ranges, gate opening/end times, and a Markdown summary.
It calls the production Silero adapter, PCM framer, and `SpeechAudioGate` through
the executable checks. It does not upload audio, read credentials, invoke Google,
or capture a microphone. “Approved” means the gate would submit that audio.

Each independent fixture starts with fresh model and gate state; playlists and
full talks preserve state throughout. All original audio is retained unchanged.
The 12 normalized diagnostics apply peak gain to 0.8 before PCM16 quantization;
normalization is not enabled in the app. Native Mac helper conversion and the
replay decoder produced byte-identical PCM for the late-English excerpt checked.

The reporting check independently verifies the expected 1.6 s withheld versus
2.4 s approved in a controlled short/long speech sequence. For every actual gate
output, it compares returned PCM bytes to the original frame indicated by the
report, rejecting duplicate, excess, or incorrectly mapped audio.

## Results

45 cases, totaling **39.79 minutes of replay input** (includes repeated audio and
normalized derivatives), completed without inference or framing errors.

| Set | Input s | Silero speech s | Gate-approved audio s | Detected speech withheld s |
| --- | ---: | ---: | ---: | ---: |
| 12 FLEURS originals | 146.16 | 108.3 | 109.3 | 3.2 |
| Same 12, normalized diagnostic | 146.16 | 112.6 | 112.8 | 3.0 |
| FLEURS playlist | 157.16 | 94.3 | 92.2 | 5.0 |
| Six 12 s excerpts | 72.0 | 48.5 | 37.3 | 11.6 |
| Alternating playlist | 77.0 | 48.9 | 37.6 | 11.7 |
| Alternating, long pause | 83.0 | 49.0 | 39.1 | 10.6 |
| Three 30 s Swahili excerpts | 90.0 | 62.9 | 55.8 | 8.7 |
| Full English talk | 787.53 | 524.0 | 452.9 | 78.0 |
| Full Swahili talk | 787.00 | 577.0 | 556.7 | 29.6 |

Approved audio includes buffered short gaps, so it can exceed the speech-label
duration. Withheld speech counts only frames labeled speech that the gate never
returned; it excludes audio labeled quiet. Incomplete final frames are recorded
separately, and pending unqualified speech at file end is not flushed artificially.

- **All 12 original FLEURS clips qualified some audio.** This is a basic coverage
  check, not proof that all words were preserved.
- **Silence, seeded noise, and 440 Hz tone:** zero speech labels and zero approved
  audio across their combined 18 seconds.
- **Quiet speech remains sensitive to level.** Clip 1654 at −66.0 dBFS RMS increased
  from 7.4 s detected / 7.8 s approved to 10.1 s / 10.6 s after diagnostic gain.
- **Continuous context matters.** The raw FLEURS playlist has 94.3 s of speech
  labels versus 108.3 s summed across separately initialized clips. These differ
  in detector context, transition gaps, and frame alignment; do not substitute
  individual-clip success for playlist testing.
- **Long-pause fixture:** the last pre-gap speech/upload range ends at 38.1 s;
  the next begins at 45.0 s. A 0.1 s boundary overlap is expected from 32 ms
  classification windows aggregated into 100 ms frames. This checks local audio
  inactivity, not actual language reassignment by the cloud pipeline.
- **Music is observational.** The tested isolated trumpet and song produced zero
  approved audio; speech/music mixtures approved 5.0 and 5.8 s of their 6 s inputs.
  Silero is not a music-rejection guarantee. Operators must still pause for music.

The most severe short excerpt was late English: 6.7 s detected, 2.7 s approved,
4.0 s of detected speech withheld. The full English result establishes that the
problem is not confined to excerpt boundaries. The gate requires two accumulated
speech seconds and resets after 400 ms quiet; repeated short stretches therefore
never qualify. It can also reset a pathological candidate at its bounded limit.

Full per-file results and timelines are in
[the replay report](../artifacts/local-speech-2026-10-25/report.md) and
[measurements](../artifacts/local-speech-2026-10-25/measurements.json).

## Next decision and limits

Evaluate a shorter qualification interval and/or more tolerant pause handling
against these same fixtures before changing production behavior. Preserve
negative-control checks and report the speech-retention/false-trigger tradeoff.
A passing “some audio uploaded” assertion alone is insufficient for captions.

This run does not measure recognition accuracy, translations, real-time caption
latency, display fading, Windows WASAPI hardware, or microphone gain. Use the
[Swahili testing guide](swahili-testing.md) for the separate cloud replay and
BlackHole display tests. Windows device/privacy/unplug/shutdown checks remain
required on Windows hardware.
