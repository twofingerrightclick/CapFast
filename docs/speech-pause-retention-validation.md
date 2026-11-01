# Speech retention across pauses — November 1, 2026

Keep the required **two accumulated seconds of detected speech** before the first
upload. Increase the quiet reset from 400 ms to **one second**. This retains
natural pauses of up to nine classified 100 ms frames, both before qualification
and while streaming an already qualified segment.

For example, one second of speech, a half-second pause, and another second of
speech now qualify. The entire 2.5-second buffered sequence is released once,
including the pause, in original PCM order. Quiet time never counts toward the
two-second speech requirement. A full second of classified quiet still clears an
unqualified candidate or ends an open segment. The existing 80-frame candidate
limit remains; buffering is bounded, not indefinite.

## Measurement and selection

The October 25 evaluation documented substantial speech lost by the 400 ms reset.
Before changing the gate, a fresh 45-case production replay reproduced all prior
speech classifications, upload ranges, and withheld durations exactly. The
baseline failed the under-3% loss goal on both full talks. A new regression also
failed because a half-second pause discarded an opening phrase before it could
accumulate two speech seconds.

A preliminary replay of the cached speech classifications compared pause
allowances while keeping the two-second qualification and candidate bound:

| Quiet reset | Full English loss | Full Swahili loss |
| --- | ---: | ---: |
| 0.4 s, original | 14.89% | 5.13% |
| 0.6 s | 9.05% | 3.33% |
| 0.8 s | 5.11% | 2.10% |
| 1.0 s, selected | 2.50% | 1.23% |
| 1.2 s | 1.15% | 0.71% |
| 1.5 s | 0.67% | 0.62% |

One second was the shortest tested allowance meeting the target on both full
talks. It adds at most 600 ms to the local end-of-stream decision relative to the
old rule; uninterrupted speech still qualifies after two speech seconds. This
does not establish actual provider or screen latency.

Fresh production Silero/framer/gate inference on all 45 PCM inputs confirmed
the selected results. Source PCM and model hashes matched the earlier QA
manifest, and speech-classification ranges stayed identical. No fixture's
withheld-speech duration increased.

| Speech set | Detected speech | Previously withheld | Now withheld | Before | After |
| --- | ---: | ---: | ---: | ---: | ---: |
| Full English talk | 524.0 s | 78.0 s | 13.1 s | 14.89% | 2.50% |
| Full Swahili talk | 577.0 s | 29.6 s | 7.1 s | 5.13% | 1.23% |
| All unnormalized natural-speech cases | 1512.9 s | 158.4 s | 33.7 s | 10.47% | 2.23% |

The aggregate includes overlapping excerpts/playlists and the full talks; these
are not independent recordings. It excludes normalized diagnostics, music
observations, and negative controls. Silence, seeded noise, and tone still
approve zero audio. The isolated music examples also approve zero audio;
speech/music mixtures remain observational, with no music-rejection guarantee.

## Reproduce and prevent regressions

With the documented audio fixtures available, run:

```sh
dotnet run --project tests/CapFast.Checks --no-restore -p:UseSharedCompilation=false
python3 scripts/evaluate-local-speech.py --full-talks --max-full-talk-loss-percent 3 --output artifacts/speech-pause-next-run
```

The full-talk evaluation now exits unsuccessfully if either talk withholds 3%
or more of its detected speech (or if it detects no speech). Existing checks
also reject any audio approved for silence/noise/tone. Regression checks cover
half-second pauses before qualification, original audio order, quiet time not
counting as speech, resumption without requalification, the one-second reset,
exactly-once stream end, and bounded candidates. Production framer/gate checks
retain an 800 ms gap and still discard an isolated short phrase across a longer
gap.

Local artifacts:

- `artifacts/qa/speech-pause-baseline-2026-11-01/`: fresh original-gate baseline.
- `artifacts/qa/speech-pause-one-second-2026-11-01/`: fresh production replay,
  before/after report, and source manifest.
- `artifacts/qa/speech-pause-full-script-2026-11-01/`: complete evaluation script
  run, including fresh decoding from source files.

## Limits

Loss means **speech-labeled frames withheld by the local gate**, not missing
words or end-to-end caption error. It excludes speech the detector itself
mistakes for quiet. These replays do not call Google or capture Windows hardware.

The under-3% target passes for both full talks and the aggregate, not every short
clip. The 12-second middle-English excerpt still loses 3.0/6.2 speech seconds
(48.39%), and the alternating playlists lose about 6.1%. Short speech segments
separated by at least one second of quiet, unfinished candidates at file end,
and the bounded-candidate safeguard can still withhold audio. Preserving every
isolated reply would require a separate change to the two-second qualification
policy. A live listening test remains necessary to evaluate recognition,
translation, finalization timing, and perceived caption continuity.
