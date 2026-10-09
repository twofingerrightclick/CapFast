# Continuous-speech latency validation — October 9, 2026

Two deterministic regressions reproduced the reported patterns. The actual session-to-pipeline acceptance seam produced no English work from stable complete interim sentences until server finalization. The presentation clock refused a large incoming paragraph despite a free row, and assigned the first outgoing row the reading deadline of its whole paragraph. Both regressions now pass alongside existing checks.

The revised pipeline submits agreed sentence boundaries from consecutive interim snapshots. Without punctuation it submits 6–18 agreed words after three seconds, retaining three mutable words. Provider finals reconcile submitted source prefixes and flush remaining text. There is no additional three-second coalescing wait in live mode. Flash receives recent source context. Display admission fills free rows, then advances one outgoing row after its own cumulative reading deadline. New words fade at ten words per second over 120 ms each; layout is reserved immediately and reading deadlines begin after reveal completion.

## Real audio replay

Replayed the supplied talk's middle 30-second sample, starting at 6:33, through the actual updated Google protocol, segmenter, ordered translation queue, and Flash translator. The harness bypasses microphone/BlackHole and the caption window. The first attempt received a stable sentence at about 3.6 seconds but the translation request failed; a repeat completed without service errors. The original failure category was inconclusive.

The successful replay produced six English outputs at elapsed seconds **5.28, 12.79, 17.50, 20.81, 28.52, and 33.84**. Translation calls averaged **1.58 seconds**; the largest gap between completions was **7.71 seconds**. One interim-derived fragment completed in English at 17.50 seconds before its provider-final source arrived at 19.63 seconds. These are receipt/completion times from the harness, not word-aligned speech-to-screen latency measurements. A historical final-only replay of the same sample produced five English outputs; separate cloud runs are not a controlled benchmark.

The six results had no outstanding accepted translation chunks. A reviewer subsequently corrected harness counting to count chunks at successful enqueue rather than batch start; code inspection and checks cover that correction, but the audio replay predates that counting-only change. Results are in ignored local artifacts `artifacts/sample-talk/incremental-latency-retry/results.json`; the failed attempt is preserved separately.

## Translation limitations

This sample exposed a real fragment-boundary error: `walienda mbali` translated as “went far,” and the following `na familia` initially became “with family.” The context instruction was tightened to produce a coherent continuation and resolve expressions spanning the boundary. A subsequent two-request test through the actual translator returned “Justin and Kennon Valdes went far” followed by “from the family so that they could more easily walk away from their faith.” The full audio replay was not repeated after that prompt adjustment.

The interim name `Kennon` differed from the later final `Kanan`. Already committed English cannot repair that correction. No aligned English gold reference exists for the sample, and this is not evidence of general translation accuracy. Earlier fragments can improve freshness while losing future context; source/model/network pauses and slow reader backlog remain possible. No claim is made that every gap is now bounded to three seconds.

## Build and display

Implementation and research agents worked on separate files; an independent agent reviewed segmentation, Unicode/edit alignment, queue cancellation and counting, word reveal, and per-row reading deadlines. Complete executable checks and the application build passed with zero warnings/errors. The Apple Silicon bundle was republished. A local sample preview confirmed the new Run-based caption rendering, full-height layout, and queued output after pause; fade timing is established by deterministic helper checks rather than a measured frame-rate test. Strict signature/property-list verification and final paused startup are checked separately before handoff.

Final verification passed: strict bundle signature, property-list validation, configured startup, and an empty paused window with BlackHole selected. The previous implementation remains committed as `fc85683`; the earlier fixed no-motion version remains tagged `no-motion-baseline`.
