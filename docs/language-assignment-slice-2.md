# FEAT-007 slice 2 validation

Google TwoStage now presents three compact Settings controls: detected input in red and both derived outputs in green. Detecting shows neutral language choices. Clicking a role hints its language as the input for this speech block. Reset output languages releases the assignment immediately, retires the recognition generation, and waits for a fresh utterance. The status shows input → both outputs, including an external input language with English/French output. Every run starts Auto; Google TwoStage has no start-language picker or optional-French toggle. Google transcription and translation is the only supported flow.

The audience has two dynamic surfaces. Language-to-physical-surface routing retains an output language’s existing surface and animation across assignment changes, including when its logical output order changes. Only vacated output surfaces clear. Detecting retains both surfaces until new assignment or normal fade. Caption pairs route by their language codes. English filtering applies only to English output, regardless of side. Sample preview supplies both English and French.

Credential editors are collapsed until explicitly opened so routine Settings accessibility snapshots do not expose loaded API keys.

## Automated verification

Focused checks exercise surface retention, reversed output order, Detecting retention, external-input labels, reset waiting for a fresh utterance, old-generation rejection, and activity updates while waiting for speech. The full existing caption/presentation/recovery/controller suite also runs.

## Runtime fixtures

English and Swahili supplied fixtures are `sampleTalk/alternating-snippets/alternating-eng-swa-77s.wav` and `alternating-eng-swa-long-pause-83s.wav`. French speech was generated locally using macOS Thomas voice into `artifacts/french-input-test.wav`; it is synthetic French, not an edition of the supplied source talk.

## Real provider verification

A direct Google replay of the supplied 12-second English clip produced 27 transcription events whose `inputTranscription` objects contained only `text`, with no `languageCode`. The raw classifier labeled 110 of its 120 frames Speech and 10 Music; the Swahili clip contained 115 Speech and 5 Quiet frames. Missing provider language metadata, rather than wholesale music misclassification, prevented assignment.

TwoStage now prefers provider metadata and otherwise asks Flash to identify the language once per substantial utterance (at least four interim words, or final text). It uses at most 512 characters, caches only within that utterance, and permits one final retry after an unknown interim result. Thus a separate translator utterance receives separate identification. Recognition generation and manual-revision checks prevent obsolete detection from choosing a new block. This adds a small Flash request and its latency per utterance when provider metadata is absent; current detection failure follows the existing safe session error path, while failures from retired work cannot fail the new block.

The real Google replay with fallback assigned English → Swahili/French and emitted four translation pairs. Reports are local artifacts at `artifacts/language-metadata-english-complete/results.json` and `artifacts/language-auto-english/results.json`. The evaluation tool now always records detected direction and both outputs through the two-stage pipeline.

The full automated checks pass, including text-only transcription, bounded detection, provider-code priority, next-utterance classification, unknown results, cancellation, HTTP request shape, and safe errors.

## Earlier app runtime limit

CUA selected BlackHole and controlled the packaged app. Before the fallback was added, the app reached Live and the 77-second, 83-second, short Swahili and synthetic French fixtures played through BlackHole, but no assignments/captions appeared because transcript metadata was absent. These runs do not validate the corrected behavior.

After the fallback, two bounded packaged-app attempts stalled in microphone preflight. The original ad-hoc-signed run logged Connecting at 15:13:25 and the exact issue at 15:14:28: “Microphone permission request timed out. Respond to the Mac permission dialog, then start again.” Earlier System Settings inspection showed CapFast microphone entries ON; no visible permission prompt was available. A first helper-identifier experiment did not improve startup and was reverted at that time. No TCC reset or password entry was performed. French input is covered by synthetic automated cases and direct provider replay below; its generated audio has not been validated in the packaged app.

CUA sample preview inspection showed Swahili INPUT / English OUTPUT / Français OUTPUT and both populated audience surfaces (“Welcome to today's seminar” and “Bienvenue au séminaire d'aujourd'hui”). Demo role controls are disabled and the theme renders them gray. Preview ended and the app was left Paused.

## Persistent Keychain signing follow-up

The login Keychain now holds a self-signed **CapFast Local Signing** identity for local testing. `scripts/publish-macos.sh` signs the helper as `app.capfast.captions.audio-capture`, signs bundled files, then signs the app as `app.capfast.captions`. `scripts/check-macos-signing.sh` verifies both identities and the full bundle. Two successive publishes had identical designated requirements for app and helper, each using the same certificate leaf. Temporary certificate and private-key export files were removed after Keychain import.

With this bundle, CapFast's microphone preflight reported **granted**. CUA selected BlackHole 2ch and started the Google TwoStage session. Playback of a supplied English clip showed live **English → Swahili + Français** with red input and green outputs. After five seconds without speech, the console returned to Detecting. A supplied Swahili clip then showed **Swahili → English + Français**; replaying it with the audience window visible displayed actual English and French captions, including “We have been grateful for prayers and continuous support for many” and “Nous avons été reconnaissants pour les prières et le soutien continu de beaucoup.” The diagnostics showed no issue. Capture was paused afterward. The synthetic French clip was verified through direct provider replay, not through this packaged-app run.

## Slice 2 review corrections

Caption callbacks now check the actual session assignment at UI dispatch time. Reset/hint invalidate queued results without clearing retained caption surfaces. Physical surfaces clear only when their bound output language changes.

Recognition receives transcript events into an ordered, bounded per-connection queue; Flash classification no longer blocks socket receiving or next-block connection creation. A final already received before a natural five-second gap retains its original audio generation and manual revision. It can finish classification/translation with a separate cancellation lifetime without changing the next input assignment. Old output text routes only to still-valid physical languages; detecting retains the last surfaces. Manual reset/hint invalidates these pending finals and queued caption delivery. Retired failures are isolated. Tests cover a short text-only final, delayed classification, natural stream close, resumed Swahili speech, and completion of the older English result with French still eligible.

A hint overrides wrong or absent automatic language for the first current utterance and its continuations. The hint is reserved to that utterance; subsequent other-language translator utterances remain excluded. Regression checks cover wrong provider code, unknown detection, continuation alignment and translator exclusion.

## All six provider directions

The direct real-provider replay in `artifacts/language-auto-swahili-french-verified/results.json` completed without service errors or pending translations. The supplied 12-second Swahili clip was identified as `sw` and produced three English/French pairs; synthetic Thomas French was identified as `fr` and produced four English/Swahili pairs. Together with the previous real English replay (`en` → Swahili/French), this exercises all six output directions. Example French source “Aujourd'hui, nous parlons de notre communauté.” produced English “Today, we are talking about our community.” and Swahili “Leo, tunazungumza kuhusu jamii yetu.” This confirms routing and provider output, not a formal translation-accuracy score or live BlackHole validation.

The final re-review corrections isolate source segmenters by originating audio generation: draining an older final cannot close or consume a new block's interim/final alignment. Interim chunks and final suffixes share a bounded translator pair for their original assignment, with per-block serialization; a drained suffix waits earlier queued chunks, and a newer block uses independent context. Legacy surface binding resets the Auto routing state, so returning to Auto with a drain-only first result binds both derived output languages. Regressions exercise new interim → old classifier/final completion → new final, ordered prefix/suffix translation, source-context continuity across a drained chunk chain, new-block context isolation, and Auto → legacy → Auto drain routing.

Natural retirement now preserves already accepted stable prefix chunks as well as received finals. Accepted chunks use the manual revision cancellation lifetime, so five-second silence resets candidates without canceling consumed source work. Same-block translation order and the prefix-before-suffix barrier remain intact; bounded independently scheduled block work lets new speech translate while an old prefix is still in flight. Explicit Reset/Hint still cancels old work and rejects queued caption delivery. The regression receives `Prefix. Suffix.`, expires its block before `Prefix.` finishes, confirms new Swahili work progresses, then releases the prefix and verifies both original captions appear in order with no consumed-prefix loss.
