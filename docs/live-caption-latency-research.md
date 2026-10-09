# Continuous-speech caption latency

Research checked 9 October 2026 against first-party API documentation and primary research. Recommendations below are implementation policies to test on the supplied Swahili seminar audio, not measured guarantees.

## What the APIs actually stream

Gemini `gemini-3.5-transcribe-live` sends replacing `interimInputTranscription` hypotheses while speech continues. Its authoritative `inputTranscription` arrives when speech is finalized, including a pause or completed turn. The dedicated transcription model is a streaming recognition pipeline rather than a conversational agent. It accepts the same 16 kHz PCM / approximately 100 ms audio chunks CapFast already sends. Utterance timestamps are available; individual word timestamps are not. The dedicated model's documented session limit remains ten minutes. [Google Live transcription guide](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe)

Google Cloud streaming STT has the same useful distinction: interim hypotheses versus `isFinal` segments. Its interim `stability` estimates whether the hypothesis will change, rather than transcription correctness. A final result covers a portion of audio, so receiving one does not mean the complete stream ended. Gemini's documented transcription messages do not expose an equivalent numeric stability score; an application must infer agreement between hypotheses if it wants to commit before finalization. [Cloud STT streaming result schema](https://docs.cloud.google.com/speech-to-text/docs/reference/rest/v2/StreamingRecognitionResult), [Gemini schema](https://ai.google.dev/api/live)

Reducing VAD silence thresholds helps only when there are actual pauses. The general Gemini capabilities guide recommends roughly 500–800 ms silence tolerance and warns that aggressive thresholds can fragment speech and harm quality. That guide describes native conversational Live models; these values should be treated as a starting experiment, not a verified optimum for Swahili transcribe-live. Hybrid VAD can send `audioStreamEnd` on a real client-detected speech ending to prompt finalization. [Google VAD guidance](https://ai.google.dev/gemini-api/docs/live-api/capabilities#voice-activity-detection-vad)

`audioStreamEnd` denotes ending/pausing the audio stream with automatic activity detection enabled; subsequent audio can reopen it. Manual `activityStart` / `activityEnd` are used only when automatic detection is disabled. Sending arbitrary timer-driven stream ends during uninterrupted speech is not the documented equivalent of a sentence boundary and risks splitting words/context. [Canonical Live realtime input schema](https://ai.google.dev/api/live#BidiGenerateContentRealtimeInput)

## Where CapFast currently waits

Repository inspection, before this task's code changes:

1. `GeminiInputTranscript.Apply` parses both interim and final messages, but `GeminiCaptionSession.ReceiveAsync` explicitly drops non-final messages in two-stage mode. The translation queue also accepts only final source updates. Consequently, English cannot appear while a long server utterance remains provisional, even though Swahili interim text may already exist.
2. `SourceTranslationPipeline` waits up to another three seconds to join short finalized fragments lacking sentence-ending punctuation. That is a bounded post-final wait, not a thirty-second service timer.
3. Text translation is serial and returns a whole translation. Network/model execution and queued batches add separate delays.
4. `CaptionPresentation` deliberately defers arrivals that would remove unread rows. At 180 WPM, thirty English words require ten seconds of allocated reading time; ninety words require thirty seconds. Once the screen is full, an apparently inactive display may therefore be protecting earlier text rather than waiting for recognition.

The first issue is a code-supported explanation for continuous-speech bursts, not proof of which stage caused the particular reported thirty-second gap. Instrumentation must distinguish those stages before attributing all delay to Google.

## What simultaneous systems do differently

Simultaneous translation chooses when to read more input and when to emit output before the source sentence ends. The primary STACL work uses a trained prefix-to-prefix model and a fixed word lag (`wait-k`); it also shows that simply imposing the same rule on a conventional full-sentence model is not equivalent. Different word order creates a quality/latency tradeoff. Its evaluated languages do not establish Swahili performance. [STACL, ACL 2019](https://aclanthology.org/P19-1289/)

A practical alternative is repeated translation plus agreement: commit only a common prefix supported by successive hypotheses, leaving a small provisional tail. Pinch-AST's 2026 cascaded system uses character-level longest-common-prefix retranslation; the paper reports other language pairs and a GPU deployment, not an API recipe or Swahili guarantee. This supports the policy pattern, not a promise that generic Gemini will produce identical stable prefixes. [Pinch-AST, IWSLT 2026](https://aclanthology.org/2026.iwslt-1.30/)

## Recommended changes and measurements

Use interim source hypotheses rather than waiting exclusively for utterance-final messages. Compare successive whole hypotheses at word boundaries, retain the last few source words as a revisable tail, and send agreed prefixes at punctuation/clause boundaries. Add a bounded age/size policy for continuous speech so missing punctuation cannot postpone all progress indefinitely. A first experiment is a roughly one-second minimum cadence, a few seconds maximum age, and a small word reserve; these are tunable app choices.

A source prefix can still change later. Record the exact submitted source span and reconcile the final transcript against that ledger. Consume each span once, avoid translating the full cumulative interim repeatedly as fresh text, and explicitly handle corrections before a committed boundary. Do not silently pretend an interim prefix is provider-final.

Provide recent source/English context to the translator, marked as context only, and request translation of the new span alone. Independent fragments can otherwise lose pronouns or the meaning of an unfinished clause. Throttle requests and bound queues; translating every interim event increases cost and stale-work backlog. Preserve ordered output if requests are ever concurrent.

Instrument monotonic timestamps for first/last interim, final receipt, chunk submission, translation start/completion, caption receipt, actual first visible word, and reading-budget blocking. Log durations/counts and queue depths rather than credentials or full transcript content. A continuous-speech fixture should verify bounded *submission* latency without a final message; model/network latency remains external.

Keep the requested word fade separate from reading pace. Reserve the final line layout immediately, reveal only newly added words at about ten words per second, and keep existing words opaque. Thirty words then reveal in approximately three seconds. Begin eviction reading time after the relevant words are visible; never replay the reveal for unchanged text. This animation identifies arrivals but cannot eliminate waiting for transcription, translation, or unread-row protection.

Use the existing Swahili clips plus a sustained-speech sample to compare end-to-end visible latency, correction rate, omissions/duplication, translation meaning, request count, and queue age. Treat reduced latency as a success only if translation remains usable. If speech continually exceeds reader throughput, lossless readable captions inevitably accumulate delay; reveal animation does not change that arithmetic.
