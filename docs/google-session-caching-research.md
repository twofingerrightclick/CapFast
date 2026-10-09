# Google session continuity and translation caching

Primary-source research checked 2026-10-09. This note covers the current Gemini Developer API models used by CapFast; it is not a new cloud test or a guarantee that generic Live features work with the dedicated transcription model.

## Practical recommendation

Keep local audio capture and speech qualification independent of WebSocket lifetime. Keep the existing bounded source context for English translation. Add automatic planned ASR rollover and bounded transport recovery with explicit handling for uncertain input completion. Do not replace translation with an ever-growing chat history solely to avoid repeating instructions: server-side history and caching are different features, and neither makes processing free.

## ASR limits and reconnect support

The dedicated `gemini-3.5-transcribe-live` guide and model feature table specify ten minutes per live session. It emits interim hypotheses and authoritative final transcriptions; hybrid VAD accepts `audioStreamEnd` for prompt finalization. [Live Transcribe](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe), [Transcribe model feature table](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-transcribe).

Generic Live documentation describes connection lifetime around ten minutes and `goAway.timeLeft` before termination. Configuring `setup.sessionResumption` yields `sessionResumptionUpdate`; pass a valid handle in a later setup. Handles remain valid for two hours after session termination. The schema marks snapshots `resumable` and may refuse resumption during active processing. [Session management](https://ai.google.dev/gemini-api/docs/live-api/session-management), [WebSocket reference](https://ai.google.dev/api/live).

The inspected dedicated TranscribeLive pages do not explicitly promise session resumption, context compression, or `goAway` support for this particular model. They use the shared protocol but that alone is not a capability guarantee. If enabling resumption, perform a bounded compatibility test and retain fresh-session fallback. Do not repeatedly retry a rejected setup field or treat a generic model example as proof of TranscribeLive support.

Engineering design inferred from these constraints:

- Roll before the hard limit, preferably at a natural accepted-speech boundary. Treat a server `goAway` as an immediate scheduling signal. Also track wall-clock connection age so missing warnings cannot leave the app dependent on one socket indefinitely.
- Prepare the next connection without stopping device capture. Preserve pending translations, the caption ledger, stable source fragments, and unique identities across sockets. Never clear visible captions as part of connection rotation.
- Bound any handoff buffer and drain work. An unexpected network failure can leave audio whose server acceptance is unknown. Replaying it may duplicate captions; discarding it may lose speech. Text deduplication must not remove legitimately repeated utterances. Explicitly record recovery/gap status instead of asserting zero loss.
- Retry transient transport/service errors with backoff, a finite attempt/time budget, and cancellation. Authentication, quota, invalid setup/model, and unknown policy errors must surface clearly; an arbitrary WebSocket `1008` is not proof of idle timeout.
- Close idle cloud sessions deliberately after accepted speech drains, reconnecting only after new speech qualifies. Maintain local classification continuously. This avoids sending silence or fake heartbeat audio.

The input-final ordering and optional-offset caveat in [speech-activity-gating-research.md](speech-activity-gating-research.md) also applies: no documented correlated audio-stream-end acknowledgement proves every uploaded sample was transcribed. Overlap or replay can improve recovery but cannot make a zero-loss claim without a validated acceptance ledger.

## Translation state and caching

`gemini-3.8-flash` supports text generation with an input limit of 1,048,576 tokens and output limit of 65,536. It does not support the Live API; a persistent ASR socket is not a persistent Flash translation session. [Model documentation](https://ai.google.dev/gemini-api/docs/models/gemini-3.8-flash).

CapFast currently calls `generateContent`, sends translation instructions each time, and includes at most the last 48 words of the immediately previous source fragment. The translator reuses its HTTP client, not a server conversation. The next translation begins only after a prior translation succeeds. These are local implementation observations from `FlashTextTranslator.cs`, not billing measurements.

The Interactions API offers server-side conversation history through `previous_interaction_id`. However, it preserves inputs/outputs only: `system_instruction`, tools, and generation settings remain per-interaction and must be specified again. Stored history therefore does not eliminate the translation rules or cap the accumulated context automatically. Explicit caching is not available through Interactions; implicit caching is. [Interactions state documentation](https://ai.google.dev/gemini-api/docs/interactions-overview#server-side-state-management).

Implicit caching is automatic for newer Gemini models, with a 4,096-token minimum for 3.8 Flash. Prefix similarity and closely spaced calls improve hit chances; there is no guarantee. Short instructions plus our bounded source fragments are below that minimum, so expect no cache savings unless usage metadata demonstrates otherwise. [Caching overview](https://ai.google.dev/gemini-api/docs/caching).

Explicit `generateContent` caching can store a fixed system instruction and source content once, referenced by cache name. It has a default one-hour TTL, model-dependent minimum input size, cache-read charges, and storage charges; cached content still consumes context and rate limits. It is appropriate for a genuinely large fixed glossary or seminar reference pack. Padding a short prompt merely to qualify is wasteful. [Explicit caching](https://ai.google.dev/gemini-api/docs/generate-content/caching).

## Cost illustration, not a measured app bill

Current standard paid 3.8 Flash prices through 2026-12-31 are $0.75 per million uncached input tokens, $3.75 output tokens including thinking, $0.075 cached input, and $0.50 per million cached tokens per storage hour. TranscribeLive lists an estimated blended audio/transcript rate near $0.009 per input-audio minute, based on typical output volume. Future pricing is date-dependent. [Official pricing](https://ai.google.dev/gemini-api/docs/pricing).

Calculated illustration for 600 translation requests: a fixed 100,000-token context costs $45 in uncached input, or $4.50 in cache reads plus $0.05 for one hour of storage, before new input and output. A hypothetical 300-token bounded request costs $0.135 for all 600 input requests. The 300-token value is illustrative, not a token count of our Swahili payloads. A larger context could improve particular ambiguous translations, but no accuracy experiment here establishes that benefit.

Recommendation: shorten unnecessary instruction wording while retaining the rules that prevented mistranslation, keep a small bounded context, and optionally add a bounded name/glossary list if evidence warrants it. Measure input, cached, output, and thinking counts from response usage metadata before changing architecture for cost.

## Token-minimal long-duration test

An actual elapsed-time soak can run for 65 minutes with thirteen 30-second Swahili bursts at minutes 0, 5, 10, …, 60, ending observation after minute 65. That sends at most 6.5 minutes of source audio, while idle capture stays local; estimated ASR cost is about $0.0585 using the published typical blended rate, before translation and any deliberate recovery replay. Log actual uploaded duration and usage rather than treating the estimate as a cap.

This sparse test verifies repeated idle close/reopen, hour-long app survival, translation-context continuity, captions after every burst, and bounded silence traffic. It does **not** exercise ten minutes of continuous speech on a single connection. Test rollover separately using injected connection-age/goAway signals and deterministic fake transport checks, plus a small authorized real-session rollover test if compatibility remains uncertain. Simulate transport interruption in the local harness; avoid repeatedly sending additional paid audio just to poll whether recovery works.

Useful pass criteria: thirteen burst outcomes with retained starts/ends; no stale duplicated captions; no audio upload during idle; finite reconnect attempts; capture/classifier still responsive after a reconnect; no growing queues; explicit error for rejected auth/quota/setup; and measured token/duration accounting. Never put API keys, resumption handles, or raw server close text in the validation report.
