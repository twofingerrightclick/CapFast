# Dynamic-language validation and remaining latency

**Current priority:** The user clarified that preserving intended meaning and speed matters more than verbatim word completeness. Typical use is about one minute of Swahili followed by thirty seconds of English; rapid alternating interpretation is secondary. Earlier failures and strict accounting remain below as evidence, not as a new verbatim requirement. Final tests will include this sustained-switch pattern and the app’s native audio classifier.

**Corrected ground truth:** The user confirmed that the Swahili video opens with a handful of English words before the Swahili dub starts. The earlier all-Swahili expectation for the opening was wrong. An initial English detection and Swahili output are therefore correct. Failure criteria are failure to adopt sustained Swahili, incorrect later routing, unstable language decisions, long gaps, and lost content. The test remains a failure on those grounds; the opening English transcription is not evidence of a recognition error.

Two real-provider replays tested the current build with only the early, middle and late 12-second Swahili excerpts from the previously authorized alternating fixture. Seven-second gaps separate the excerpts (50 seconds total, followed by 20 seconds of local quiet). The Google APIs and production session/segmentation/detection/translation pipeline were real. Native microphone classification and the audience UI were bypassed; existing application settings were not changed.

| Observation | Run 1 | Run 2 |
| --- | --- | --- |
| Displayed caption pairs | 6 | 7 |
| Pairs using automatic-language fallback | 3 | 2 |
| Displayed pairs routed outside English/French | 0 | 0 |
| Explicit skipped-segment notices | 3 | 2 |
| Confirmed non-Swahili detector answers | 0 | 1 (Italian) |
| Session stopped with failure | No | No |
| Overall result | Fail | Fail |

Run 2 captured the recognizer output `Quale hanno?`, which the detector classified as `it`. The source was still the Swahili-only recording. The supplied Swahili reference includes “Kwa wale wenye shaka” in this portion; the recognizer subsequently emitted `Kwale wano tili ashaka.`, which received an unknown language result. This demonstrates language drift beginning in the source transcription, not merely a caption-window routing error. It does not prove that the user's exact English/French misidentification follows the same path.

Every displayed pair in these two runs was assigned English/French. The Italian-classified fragment did not become a displayed caption. Consequently these tests do **not** reproduce the exact report of Swahili being displayed after an English/French guess. They do reproduce unreliable recognition/detection and missing captions, so neither run qualifies as stable Swahili-only behavior. A live connection alone is not a passing result.

The harness records source text presented to detection, detector answers, final caption pairs, timing and safe diagnostics. It never sends the expected language or reference transcript to the service. No production behavior was changed in this testing turn.

Artifacts: `artifacts/debug-swahili-only/{manifest.json,run-1.json,run-2.json,assessment.json}`. The exact fixture uses only Swahili excerpts from the previously authorized recording. `Program.cs` and `Replay.csproj` in that directory are the diagnostic runner. The repeat asserts coverage of all three audio blocks. Future runs also explicitly reject any non-Swahili detector answer even when that fragment never reaches a caption.

Recheck recorded results with `python3 artifacts/debug-swahili-only/assess.py` (exit 1 = failed). New live runs use `dotnet run --project artifacts/debug-swahili-only/Replay.csproj -- NEW_REPORT.json` from the repository root and incur Google API usage. Raw reports are retained rather than selecting only favorable outputs.

## Continuous passage from the identified video

After the user confirmed the Clark G. Gilbert video, the test decoded 00:30–02:00 directly from `sampleTalk/2026-04-1050-clark-g-gilbert-360p-swa.mp4` into mono 16 kHz PCM without inserted gaps or gain changes. The real-provider run produced 17 caption pairs, all assigned Swahili input and English/French output; all 19 completed detector answers were `sw`. One translation timed out and one segment exceeded the age budget. The test therefore still failed its no-skipped-captions criterion, while language routing passed for this passage. Source text contained garbled/mixed words (including `hueso, aquí`); there is no aligned gold transcript here, so this is not a numerical transcription-accuracy score.

Artifacts: `artifacts/debug-swahili-continuous/{manifest.json,run-1.json}`. This result does not reproduce the reported English/French assignment switch. The opening 00:00–01:30 is tested separately because the report specifically mentions the initial language guess.

## Video opening: English-to-Swahili transition (expectation corrected)

A separate run decoded 00:00–01:30 directly from the same identified Swahili video. At 7.84 seconds the recognizer supplied `Elder Patrick Kearon's remarks of`; the text detector returned `en`. At 9.74 seconds the resulting primary caption was Swahili (`maneno ya Mzee Patrick Kearon ya`) and the secondary caption French. The user subsequently confirmed these opening words really are English. This initial routing is correct and must not be classified as an error. The test must instead verify that subsequent sustained Swahili changes the dominant-language indicator and receives English/French captions without missing content.

Later detector answers were `sw`, but `SpeechLanguageAssignment.Current` remained anchored to the first `en` assignment until silence. That indicator therefore did not represent the language being detected for later individual captions. The transcript also contained duplicated/garbled fragments. This run does not reproduce the later French-input guess reported by the user.

Opening run totals: 18 caption pairs, one Swahili/French pair (correct for the actual English opening), four automatic-fallback pairs, three language-detection timeout notices, one connection, no terminal session failure. There was also a roughly 21-second gap between caption arrivals at 18.89s and 40.04s. These observations are failures, regardless of the session remaining live.

Evidence: `artifacts/debug-swahili-opening/{manifest.json,run-1.json}`. Original input, code, and app settings were preserved. As above, the replay uses the real services and production pipeline while bypassing the native microphone classifier and UI. This test establishes the received-text/detection/routing sequence, not a word-level acoustic transcription accuracy score.

## Combined-request prototype (not yet an end-to-end pass)

A direct Google probe on five transcripts from the opening run returned correct English/Swahili classifications and three structured language renderings. Durations were 7.923, 4.081, 2.380, 2.646, and 3.064 seconds; an earlier identical English request took 2.956 seconds. This variability rules out treating a six-second deadline as a reliable caption-preservation guarantee. These text-only requests do not validate recognition or the live queue.

The documented `responseFormat.text.mimeType: application/json` form was rejected by the deployed API with HTTP 400. The verified request uses `generationConfig.responseMimeType: application/json` and `responseJsonSchema`. No credentials or raw request headers were recorded. Probe artifacts: `artifacts/debug-combined-probe/results-legacy.json`.

## First combined-pipeline video replay — still failing

`artifacts/debug-transition-combined/checkpoint-1.json` replays the first 90 seconds through the new combined translator. All 18 submitted fragments produced caption pairs, without errors, skip notices, or connection restarts. The dominant indicator changed from English to Swahili at 41.51 seconds. Request durations were usually two to four seconds, with one 7.27-second response.

This is still a failed acceptance run: the largest caption gap was 12.52 seconds. At 23.82 seconds, recognition supplied mixed text `As Christine and I have felt the Kama vile`, which was classified as English; later source text contained `hueso, aquí y languvu kwa uaminifu`, yielding the meaningless English caption “bone, here and strength faithfully.” There were also duplicate fragments. Complete accounting of text submitted to translation does not prove complete or accurate recognition of the speaker’s audio. An isolated language-hint experiment is next; production recognition settings remain unchanged pending evidence.

## Recognition comparison and the revised baseline

The three-language hint experiment (`artifacts/debug-transition-hints/run-1.json`) produced 21/21 translated fragments without service errors, with a 9.24-second maximum gap between caption arrivals. It preserved the real English opening and replaced the earlier Spanish-like garbling with `uwezo, akili na nguvu kwa uaminifu`. The indicator adopted Swahili at 31.06 seconds. This is improvement, not a word-level accuracy certification.

The explicit-Swahili comparison (`artifacts/debug-transition-sw-baseline/run-1.json`) produced 19/19 fragments, but its first transcript contained only `Kama vile`: the actual English opening was omitted. Both hint variants still showed an English interim prefix around 19–22 seconds that was absent from the authoritative Swahili final. This remains a limitation of committing provisional recognition for low latency. The single-language baseline's initial automated green result was too weak; final opening tests now explicitly require preservation of the real English words.

The final production setup uses `languageCodes: [en-US, sw-KE, fr-FR]` as recognition hints, without forcing the detected source language. Google documents these as recognition biases and documents automatic multilingual recognition when hints are absent: [Live transcription configuration](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe).

Raw interim/final evidence also exposed a local duplication bug when recognition changed word boundaries, such as `yaushahidi` → `ya ushahidi` and `YesuKristo` → `Yesu Kristo`. The segmenter now matches exact concatenated lexical prefixes before approximate token alignment. A regression uses the recorded sequence and checks every character is emitted once. The final replay additionally rejects the known repeated `Kristo ulimwenguni kote` phrase.

## Frozen opening replay 1 — acceptance still failed

`artifacts/debug-transition-combined/frozen-opening-1.json` produced 19/19 fragments, no service failures/skips/restarts, sustained Swahili routing, and no repeated `Kristo ulimwenguni kote` phrase. It nevertheless failed: the mixed English-majority opening was classified `other`, which selected English/French instead of Swahili/French, and the maximum caption gap was 14.35 seconds. The source fragment itself preserved the real English words, unlike the single-language baseline. The next correction explicitly distinguishes a mixture of supported languages from a predominantly different language.

The large gap spans a roughly six-second pause in recognition arrivals plus provisional-fragment stabilization and translation; it is not a fourteen-second HTTP call. No source-to-audio alignment exists to claim a numerical end-to-end latency guarantee.

## Alternating-speaker production replay

`artifacts/debug-alternating-combined/frozen-1.json` passed the six expected source-language runs (`en, sw, en, sw, en, sw`): 21/21 submitted fragments delivered, no issues or leaked commentary, and two expected connections across the seven-second silence. Swahili captions resumed 5.62 seconds after speech resumed at fixture time 45 seconds. Maximum caption-to-caption gap was 9.59 seconds, including the silence. A short proper-name fragment was classified `und` and used the stable routing fallback. This replay predates only the stricter mixed-language classification wording.

Six real-provider probes using that revised wording (`artifacts/debug-mixed-classification/run-1.json`) correctly classified both mixed English-majority fragments as English and the Swahili-majority fragment as Swahili, in two repetitions. Five requests took 1.91–2.61 seconds; one took 9.35 seconds. The twelve-second attempt window preserved that valid delayed response. These results demonstrate variable service latency, not an instantaneous translation guarantee.

## Faster segmentation experiment rejected

A replay of the recorded recognition events tested a two-second wait, four stable words, and two mutable tail words. It reduced the maximum source-emission gap from 11.96 to 8.62 seconds but increased requests from 19 to 24 and lost `ushahidi` when previously emitted `ya` became part of `yaushahidi`. The production settings preserved all 64 normalized characters in that affected turn. Reducing only the timer to two seconds did not materially improve the largest gap. These experimental changes were **not** applied. Evidence is in `artifacts/debug-segment-thresholds/`. Preserving content takes precedence over making a latency counter look better.

## Revised prompt video run and native classifier discovery

`artifacts/debug-transition-combined/final-opening-2.json` preserved the opening English routing, sustained Swahili, all 18 translated fragments, and no known duplicated phrase. Its 12.95-second largest arrival gap still exceeded the harness’s provisional twelve-second cutoff. No request failed.

The app’s native classifier was then run offline on the identical opening audio. It identifies six seconds of quiet from 12–18s, enough for the real application to reset its speech block. Earlier diagnostic runners marked all nondigital-silent audio as speech and therefore did not exercise that real block transition. Final replay now uses native classifier packets: `artifacts/debug-native-opening/classified.frames` (900 packets: 753 speech, 147 quiet). Physical device capture and UI remain separately untested by the cloud replay harness.

## Speed-first adjustment after user clarification

The user explicitly accepted occasional word loss in exchange for speed and emphasized preserving intended meaning across longer language runs. Production now uses a two-second stabilization interval, a four-word stable minimum, and a two-word mutable tail. The newly exposed joining case is guarded by prior recognition evidence: a residual word is split out only when the preceding snapshot showed it as a separate unconsumed word. Ordinary corrections such as `cat` → `cats` therefore do not emit orphan suffixes. Independent review and the full checks passed.

The final typical-use fixture concatenates sixty seconds of Swahili (source30–90s) with thirty seconds of English (source393–423s), without inserted silence. It uses the native classifier before paced live replay. Its manifest is `artifacts/debug-long-switch/manifest.json`.

## Final typical-use result

`artifacts/debug-long-switch/final-1.json` passed on the final faster production snapshot with native-classified audio: 27/27 translation submissions reached captions, the input sequence changed from Swahili to English, English-source captions arrived 4.90 seconds after the audio switched, and there were no service errors, skips, restarts, commentary leaks, or routing errors. The dominant indicator switched later than the first correctly routed English caption, as intended. Maximum caption-arrival gap was 10.21 seconds.

This count is an accepted-text accounting check, not a word-perfect acoustic coverage claim. The recognizer did not emit a short heading between two English sentences; the existing two-second speech qualification can ignore short isolated speech. The user explicitly prioritized speed and intention over occasional word omissions. Larger omissions, mistranscriptions, and semantic accuracy still require human audio review.

The signed reviewed build is at `artifacts/macos-reviewed/osx-arm64/CapFast.app`. It was built separately because automatic approval review blocked closing the currently listening app, which could lose live caption/session state. The original running bundle was not replaced.

## Native opening and bounded slow-response acceleration

The first native-classified run (`artifacts/debug-native-opening/final-1.json`) produced 24/24 fragments with correct routing, sustained Swahili, and no service failure, but a single valid translation took 11.32 seconds and held later completed captions behind it. Maximum arrival gap was 15.43 seconds. A few initial English words were replaced by Google's final recognition; under the user's updated speed/meaning preference, that remains reported but no longer automatically fails acceptance. Large caption stalls remain a failure.

The final translator permits one spare identical request after four seconds. It uses the first valid result, cancels and observes the other request, and keeps strict limits of one reserve request and three actual HTTP requests in flight. Provider refusals and terminal service errors are not retried; an already available refusal or terminal result takes priority over concurrent success. This costs an extra Google call for some slow fragments. Fast requests retain one call. Both reviewers independently verified bounded concurrency, cancellation, late faults, and refusal/terminal behavior.

## Final reviewed snapshot

`artifacts/debug-native-opening/hedged-final-2.json` preserved the English opening and then sustained Swahili, with 24/24 translation submissions delivered, no wrong output routing, no known repeated phrase, no service error, and two planned connections. It made 26 HTTP requests: 24 successful originals plus two spare requests canceled after the original won. Completed translations took at most 4.82 seconds in this run. This does not isolate the duplicate-request benefit statistically; controlled tests establish the winner/cancellation behavior.

The strict twelve-second maximum caption-gap assertion **still failed**: the largest gap was 12.57 seconds, spanning the native classifier’s six-second quiet period and renewed recognition. Swahili captions arrived about 9.70 seconds after speech resumed at18s. The prior 11.32-second HTTP stall was not reproduced. The sixty-second Swahili → thirty-second English case passed at4.90s, but the app is **not proven instantaneous or seamless across every pause**. Do not present clean build/review results as evidence that this remaining latency is solved.

Final checks: complete `CapFast.Checks` pass, main app and both tool projects build, signed app/helper identity verification passes, and both independent reviewers report no actionable findings in the implemented changes. The rebuilt app remains a separate signed copy at `artifacts/macos-reviewed/osx-arm64/CapFast.app`; switching the active app requires approval because it ends the current listening session.
