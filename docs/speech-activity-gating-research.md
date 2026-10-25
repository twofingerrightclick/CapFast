# Local speech gating research

> Historical note: music detection, rejection, and caption suppression have been removed. Only local speech detection remains; operators pause captions during music. Music-related findings below describe the earlier implementation.

Researched 2026-10-09 for CapFast’s Mac audio capture. The user’s selected policy is strict: suppress music and singing even when speech occurs over music. Findings below distinguish sourced capabilities, local observations, and proposed policy.

## Recommendation

Use Apple SoundAnalysis in the existing Swift audio helper, with a bounded local PCM buffer and a separate deterministic two-second speech gate. It is the smallest dependency change for the currently supported Mac Google capture path. Keep the classifier behind an audio-activity interface so another platform can supply a different implementation later. This recommendation is an engineering inference from the available APIs and current code, not a measured accuracy comparison.

Apple’s built-in classifier runs on-device, detects speech/music/singing, and supports over 300 labels. It accepts live audio through `SNAudioStreamAnalyzer`; query `knownClassifications` rather than assuming labels. Its scores are independent: do not compare speech score against music score or interpret them as probabilities summing to one. Window size and thresholds affect accuracy. [Apple WWDC21](https://developer.apple.com/videos/play/wwdc2021/10036/), [live input sample](https://developer.apple.com/documentation/soundanalysis/classifying_live_audio_input_with_a_built-in_sound_classifier).

Choose a one-second classification window and 50% overlap initially. This trades some detection delay for enough acoustic context to reject music. Higher overlap increases computation; 50% is the documented default. These settings and thresholds must be tested on seminar material. [Apple overlap documentation](https://developer.apple.com/documentation/soundanalysis/snclassifysoundrequest/overlapfactor).

## Verified locally

A metadata-only compiled Swift probe successfully constructed `SNClassifySoundRequest(classifierIdentifier: .version1)` outside the tool sandbox. It returned exact labels `speech`, `singing`, `choir_singing`, and `music`. The default window was three seconds; setting one second with overlap 0.5 succeeded. No audio or credentials were read by this probe.

The same probe failed inside the tool sandbox with a CoreML execution-plan initialization error, code -5. A failed classifier must produce a clear fixed error and suppress upload; it must not fall back silently to a volume threshold. The packaged app subsequently classified BlackHole input successfully, rejected the singing fixture, and captioned the supplied Swahili talk.

Do not match identifiers by substrings: the label list also contains `singing_bowl` and `car_passing_by`, which are unrelated to sung vocals.

## Alternatives

| Option | Useful capabilities | Integration and limitations |
| --- | --- | --- |
| Apple SoundAnalysis | Separate speech/music/singing scores; local classification | Already compatible with the Swift Mac helper and macOS 12 deployment target. OS framework/model, no extra distributed model or runtime. Not a Windows/Linux implementation. CPU cost and Swahili false negatives remain to measure. |
| Silero VAD through ONNX Runtime | Low-latency speech probability; robust noisy-audio speech detection | MIT licensed model. Upstream reports a roughly 2 MB JIT model and under 1 ms per 30+ ms chunk on a CPU thread; this is an upstream benchmark, not a CapFast measurement. VAD is not a separate music/singing classifier, so alone it cannot meet the selected policy. [Silero](https://github.com/snakers4/silero-vad). |
| WebRTC VAD | Lightweight native binary speech/non-speech decision | Native C API, 10/20/30 ms PCM frames, aggressiveness modes. Source uses a Gaussian mixture speech/noise test, not music/singing labels. BSD-style source plus PATENTS notice. Requires native packaging per platform; insufficient alone for strict music exclusion. [API](https://webrtc.googlesource.com/src/+/refs/heads/main/common_audio/vad/include/webrtc_vad.h), [implementation](https://chromium.googlesource.com/external/webrtc/+/branch-heads/4606/common_audio/vad/vad_core.c). |
| YAMNet event classifier | 521 AudioSet classes including speech, music, and singing | MobileNet-based TensorFlow reference implementation; 16 kHz mono input, 0.96-second windows every 0.48 seconds. More deployment work: distributed weights, feature preprocessing, inference runtime, and a verified conversion if using ONNX. The reference source is Apache 2.0; check the exact downloaded weight artifact’s license. [README](https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/README.md), [parameters](https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/params.py), [class map](https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/yamnet_class_map.csv). |

Silero’s official ONNX wrapper uses 512 samples at 16 kHz (32 ms), recurrent state, and 64 samples of context. A .NET port must retain these states across frames and reset them consistently. [Upstream wrapper](https://github.com/snakers4/silero-vad/blob/master/src/silero_vad/utils_vad.py). ONNX Runtime provides C# inference bindings, but the exact native runtime assets must be verified for `osx-arm64`, `osx-x64`, Windows, and Linux; a generic cross-platform claim is not a deployment check. [C# documentation](https://onnxruntime.ai/docs/get-started/with-csharp.html).

## Proposed gate semantics

1. Continue local device capture and local classification during idle meetings. Send no audio frames to Google while idle. A WebSocket connection can remain alive; control/keepalive traffic is distinct from paid audio or translation requests.
2. Require at least two seconds of accepted speech before uploading a new utterance. Use classified audio duration, not wall-clock time since any noise or overlapping-window duration counted twice. Short pauses may preserve a candidate, but must not count as speech. An utterance ending below two seconds is discarded by this requested rule, including meaningful short replies such as “yes.”
3. Retain the candidate’s opening PCM locally. After qualification, upload it once in chronological order and stream accepted continuation. A two-second rule necessarily adds at least two seconds to first-caption latency; classification/inference adds further delay.
4. Treat detected music/singing as a veto even when speech is also detected. Clear unqualified candidates and close qualified upload when veto occurs. Thresholds are separate for each label. Imperfect classification means neither zero music leakage nor zero lost speech can be guaranteed.
5. Use timestamped classification intervals and delay audio release until classified. Otherwise music may already reach Google before the later classifier verdict. Bound buffers and classification work, fail closed if the classifier stalls, and never accumulate a large silent backlog.
6. End the accepted audio segment cleanly, then return to idle. Allow final transcription and translation of previously accepted speech to finish during silence. Preventing those delayed requests would truncate valid captions and lose the end of a talk. Idle means no *new silence-origin* translation, not cancelling existing work.
7. Show a blue idle dot while listening without qualifying speech, animated ellipsis during candidate/accepted speech, and the existing pause indication while manually paused. Music/noise rejection should remain idle or show a restrained non-speech state rather than imply captioned speech.

These are proposed product semantics, not Apple classifier guarantees. Real meeting tests should report uploaded duration, rejected duration, discarded short replies, opening-word retention, end-of-phrase retention, and silence latency—not merely whether a detector toggles.

## Gemini stream boundaries

Before this change, CapFast read raw helper stdout in 100 ms PCM16 chunks and sent every chunk from `GeminiCaptionSession.SendAudioAsync`, without a local classifier. Its source translation pipeline only sees Google transcription results. The new gate runs before base64 upload; a framed helper protocol carries audio plus classified activity without sending metadata as PCM.

The dedicated `gemini-3.5-transcribe-live` documentation explicitly supports `realtimeInput.audioStreamEnd: true` and hybrid VAD: keep automatic VAD enabled, signal local speech end, then consume final transcription. Manual VAD instead uses `activityStart`/`activityEnd` with automatic VAD disabled. [Live Transcribe documentation](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe).

The general Live API guide says to send `audioStreamEnd` when pausing an automatic-VAD audio stream for more than a second; audio can resume later on the same session. Send this once when closing an uploaded segment, not repeatedly throughout idle silence. Simply dropping samples without a boundary can leave server audio buffered. [Live API capabilities](https://ai.google.dev/gemini-api/docs/live-api/capabilities#automatic-vad).

## Validation still required

The local probe verified initialization and configuration, not audio-classification quality. Before claiming reliable behavior, test the existing Swahili pack and seminar clips plus silence, fans, taps, instrumental music, vocal music, choir, speech over music, quiet speech, and short replies. Choose conservative thresholds from those observations. Preserve a manual pause and a clear error if classifier initialization fails.

## Downloaded local evaluation fixtures

Two small compressed examples were retrieved from the official librosa example-data server; no account or API credentials were used. Generated audio remains under ignored `artifacts/speech-gate` and is not part of the app bundle. Both excerpts were resampled to 16 kHz mono PCM16 using the existing local FFmpeg binary. These transformations are the only modifications.

- `instrumental-trumpet.wav`: first six seconds (or the complete shorter recording) of Mihai Sorohan’s “Jazz Trumpet Loops Pack in F 90 bpm,” example `sorohanro_-_solo-trumpet-06.ogg` (27,019 bytes). Artist/source: [Mihai Sorohan on Freesound](https://freesound.org/s/77711/). [Librosa attribution](https://raw.githubusercontent.com/librosa/data/main/audio/sorohanro_-_solo-trumpet-06.txt), [download](https://librosa.org/data/audio/sorohanro_-_solo-trumpet-06.ogg).
- `vocal-song.wav`: six seconds starting at 00:30 of Karissa Hobbs’ “Let’s Go Fishin’,” example `Karissa_Hobbs_-_Lets_Go_Fishin.ogg` (734,402 bytes). Artist/source: [Karissa Hobbs on Free Music Archive](https://freemusicarchive.org/music/Karissa_Hobbs/Age_of_Flowers/09_Lets_Go_Fishin). [Librosa attribution](https://raw.githubusercontent.com/librosa/data/main/audio/Karissa_Hobbs_-_Lets_Go_Fishin.txt), [download](https://librosa.org/data/audio/Karissa_Hobbs_-_Lets_Go_Fishin.ogg).

Both recordings are identified as [Creative Commons Attribution 3.0](https://creativecommons.org/licenses/by/3.0/) in the linked first-party dataset attribution files. Librosa’s documentation identifies the latter as a song with vocals; the implementation agent subsequently confirmed every frame of the selected excerpt received the music veto. [Example recording descriptions](https://librosa.org/doc/dev/recordings.html).

## Initial calibration observations

The native implementation agent subsequently tested independent thresholds of 0.5 for speech and 0.5 for music/singing. The trumpet excerpt produced 40 music and 13 quiet 100 ms frames, with no speech frames; the six-second vocal excerpt produced 60 music frames and no speech frames. Silence, synthetic noise/tone, these music excerpts, and tested music/speech mixtures produced no qualifying two-second speech runs. All twelve level-normalized FLEURS Swahili fixtures qualified. Some original very quiet recordings did not qualify. These are limited fixture results, not a universal quality guarantee.

A lower music threshold of 0.15 falsely vetoed roughly 75% of the tested Swahili seminar. A higher 0.65 threshold leaked qualifying speech in tested music/speech mixtures. Therefore 0.5 is the selected initial music threshold. Faint music false negatives, quiet-speech misses, and speech false positives remain possible. Detailed evaluation output belongs with the implementation validation record.

## Idle WebSocket lifecycle investigation

Official API semantics permit later audio after `audioStreamEnd`; it ends an audio stream, not necessarily the WebSocket session. [WebSocket API reference](https://ai.google.dev/api/live). Dedicated TranscribeLive documentation allows up to ten minutes of continuous streaming. General Live session management describes connection lifetime around ten minutes and early termination, with resumption mechanisms. Neither inspected page specifies an idle timeout or requires sending silent PCM as a heartbeat. [Live session management](https://ai.google.dev/gemini-api/docs/live-api/session-management), [TranscribeLive limits](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe#limitations).

A local app test nevertheless encountered close code 1008 after roughly two to three minutes of a gated session. That code alone does not establish idle timeout: WebSocket 1008 means a policy violation and does not identify a particular condition. [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455#section-7.4.1). Do not silently retry authorization, quota, unsupported-protocol, or unknown policy errors based solely on that code.

Recommended resilience policy (engineering inference): keep local capture/classification active, close a cloud connection deliberately after an idle interval once accepted audio is finalized, and reconnect lazily only for the next qualified speech. Preserve the translation pipeline/context, pending translation queue, and monotonically unique utterance identities across connections. A clean disconnect must not discard already accepted captions or replay past PCM. Wait for final transcript/turn completion rather than closing immediately after stream-end; use a bounded completion deadline and report failure if accepted input never finalizes. This avoids relying on undocumented idle socket longevity without uploading synthetic silence.

Drain caveat: the WebSocket schema says input transcription arrives independently without guaranteed ordering. Its `startOffset`/`endOffset` fields are optional protobuf durations relative to the start of audio; the dedicated TranscribeLive guide does not promise their presence or define offset reset across separate `audioStreamEnd` segments. Nor does it document a correlated stream-end acknowledgement. A late final for segment A after uploading B cannot prove B has finalized; `turnComplete` alone also lacks a client-input sequence identifier. Therefore “any final seen, no partial” is only an idle-drain heuristic and must not be described as an exact acknowledgement. Reliable offset-based completion requires verified offset semantics and coverage of the latest accepted speech. If those signals are unavailable, retain the connection for a bounded drain period, clearly report uncertain completion when applicable, and do not silently claim every uploaded sample was finalized. [WebSocket schema](https://ai.google.dev/api/live).
