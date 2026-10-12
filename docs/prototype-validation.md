# Historical prototype validation

This records earlier implementations, including retired providers and direct translation. The current app supports only Google transcription followed by automatic two-output text translation.

# Prototype build and review

## English and French captions — October 11, 2026

The local annotated `v1` tag points to commit `482c0bc`, the committed baseline before French support. Preexisting uncommitted appearance changes were preserved; Git tags do not include uncommitted files.

Google still defaults to TwoStage: `gemini-3.5-transcribe-live`, verbatim `sw-KE`, then `gemini-3.8-flash` English translation. While paused, **Add French captions** enables a second Flash translator using the same original Swahili fragment and independent source context. English/French pairs publish together in source order; French forces TwoStage. Each display column uses the same word reveal, reading pace, scrolling, resize replay, and fade behavior. Changing caption languages clears visible and queued captions.

Separate agents reviewed the translation and UI slices; the UI reviewer also reviewed final packaging. No blocking findings remained. The review's top-alignment suggestion was applied to the extracted caption canvas.

- Debug build and complete executable checks passed. New checks cover the French prompt and Swahili context, ordered pairs, cancellation while French is pending, incomplete-pair failure, English filtering with preserved French text, French-only inactivity, and clearing on language changes.
- Apple Silicon Release bundle published; strict signature verification and plist validation passed. NuGet reported NU1900 because its vulnerability metadata endpoint was unreachable; cached packages allowed the build and publish to complete.
- Packaged runtime sample preview visibly confirmed English left, French right, scrolling after demo capture paused, fullscreen reflow, and return to the original window size. Disabling French cleared both surfaces and restored a single English column. The app was left empty, paused, and with French disabled.

This slice used local sample captions and fake service responses; it made no paid cloud requests and does not establish French translation accuracy or real microphone-to-caption latency. French adds a Flash request per fragment and pairs wait for the slower translation. The English blacklist remains English-only. Dynamic speech/target-language selection remains future work.

Validated October 7, 2026 on macOS 15.7, Apple Silicon, with .NET SDK 10.0.101.

## Reviewed slices

A build agent completed compilation, packaging, and documentation. A separate review agent inspected caption state, Azure session lifecycle, presentation controls, and macOS packaging.

Review fixes included replacing audio-offset identities with session-local utterance sequencing, discarding provisional text on terminal no-match results, bounding caption layout to the bottom of the screen, and limiting unusually long Unicode text. The final review reported no blocking findings.

## Verification completed

- `dotnet build src/CapFast/CapFast.csproj --no-restore`: zero warnings and errors.
- `dotnet run --project tests/CapFast.Checks/CapFast.Checks.csproj --no-restore`: passed. Covers partial corrections, finalization, duplicate final results, no-match results, sequence boundaries, Unicode limits, and clear behavior.
- `bash scripts/publish-macos.sh`: self-contained Apple Silicon `.app` published and locally signed.
- Bundle signature verification with `codesign --verify --deep --strict` and published plist validation with `plutil -lint`: passed.
- Native UI: app launched; demo completed with translated sample text in the preview and caption window. Visually checked white captions in the bottom region on black, fullscreen with F11, and exit with Escape.

## Still to validate

Live Azure authentication, microphone permission/capture, Swahili translation quality and delay, projector hardware, long sessions, and network recovery have not been tested. Intel, Windows, and Linux builds have not been validated. Demo text does not establish real service translation quality or timing.

## Google integration — October 8, 2026

Added Google Gemini Live Translate as the default provider while retaining Azure. A build agent implemented the WebSocket adapter, transcript accumulator, and bundled Swift Mac audio helper; a separate reviewer checked protocol framing, startup/stop behavior, caption completion, packaging, and tests. No blocking review findings remain.

- Debug build passed with zero warnings/errors. Apple Silicon Release bundle published with the native helper; strict bundle signature and plist validation passed.
- Caption and Gemini checks passed, including accumulated transcript fragments, repeated text, completion-marker deduplication, interruption, source/audio filtering, and bounded memory.
- UI validation: the new bundle launches with Google selected and a masked key field. Starting without a key stays local and displays the missing-key message. Provider switching shows Azure fields and returns to Google correctly.
- No API key was supplied during validation. Live Google authentication, microphone permission/capture, translation timing/quality, and session expiry have not been tested. Session expiry currently requires an operator restart; seamless seminar rollover remains future work.

## Caption-first usability — October 8, 2026

The main window now opens black and paused. Settings and errors are secondary windows opened through the Mac app menu. Paused controls show an input selector on the left and pause bars on the right. Missing credentials show an explicit Settings prompt. Space toggles capture; pause retains captions while disposing the session. The in-memory error log evicts older entries beyond 100.

A UI agent and audio/build agent implemented separate slices; an independent reviewer checked source selection, focus handling, native menu wiring, controller transitions, and error bounds. Review identified a competing demo-start race, which was fixed by serializing demo setup.

- All caption, Google transcript, and controller checks pass. Controller checks cover startup paused, selected-input snapshot, pause while connecting, rapid pause/resume, stale callback suppression, caption retention, shutdown, missing configuration, and oldest-error eviction.
- Debug builds pass with zero warnings/errors; Swift helper compiles without warnings.
- Permission-free CoreAudio enumeration finds BlackHole 2ch and MacBook Pro Microphone. Explicit UID selection is routed to the Google helper and Azure SDK without changing the Mac system default.
- Actual capture from the selected hardware and live cloud translation are not established by these checks.
- Packaged runtime inspection confirms caption-first paused launch, missing-configuration prompt, pause bars, keyboard Settings shortcut, and selectable BlackHole/built-in microphone entries. Space during the sample preview returns to paused; settings do not auto-start capture.
- Runtime inspection found application-menu setup happened after native menu initialization. Moved it into `App.Initialize` before creating the caption window, following Avalonia's application-menu initialization pattern.
- Final packaged confirmation: native CapFast menu visibly includes Settings… and Errors… after the initialization fix. The paused-window Settings button remains available as the direct configuration entry point.

## Permission diagnostics and local configuration — October 8, 2026

Google startup previously attempted service setup before requesting Mac microphone permission. Service rejection could therefore prevent any permission prompt and the controller discarded the reason into a generic credential/microphone message. Startup now runs a permission-only helper first, captures no audio until service setup succeeds, and surfaces curated microphone, HTTP/service-code, setup, and capture errors. Settings provides a standalone permission check. Pause/quit cancels Google startup and Settings closure cancels its permission-only helper.

Added an ignored project `.env`, an empty `.env.example`, and a small allowlisted configuration loader. The built app finds the project file even when launched from Finder; copies outside the project use `~/Library/Application Support/CapFast/.env`. Environment variables take precedence. Settings displays the exact path, opens the file, and reloads it while paused. Configuration is not included in the bundle, and rebuilding does not replace it.

Independent review covered diagnostic sanitization, HTTP status opt-in, preflight cancellation, helper cleanup, environment precedence, permissions, and create-without-overwrite behavior. All executable checks pass, including local-file parsing/precedence/limits, safe versus arbitrary exception messages, and prompt-start cancellation during shutdown. Swift and .NET builds pass. No raw server response or credential is logged.
- Packaged Settings displays the project `.env` path with Open API key file, Reload file, and Check microphone permission controls. A permission-only runtime check returned **Microphone permission granted**, without capturing audio or making a cloud request.
- The user's subsequent service attempts appear in the error viewer as Google WebSocket close code 1011. Those attempts passed the separate microphone preflight; translation success is not established by this permission fix.
- Final bundle also classifies known Google WebSocket close-reason phrases into fixed authentication, quota/billing, model, setup, or service messages and reports whether setup was accepted. Unknown 1011 remains explicitly inconclusive. Classification tests ensure arbitrary reason text is not echoed. Final publish and strict signature verification pass; the rebuilt app opens paused with local configuration loaded.

## Two-stage captions and authenticated audio — October 9, 2026

Google now defaults to `gemini-3.5-transcribe-live` with a Swahili hint, followed by `gemini-3.8-flash` English text translation. Settings retains direct Live Translate for comparison. An implementation agent and independent reviewer checked final/interim recognition semantics, ordered bounded queues, translation response limits, pause cancellation, and sentence-fragment buffering. Checks cover three-second timeout flush, punctuation, merge identities, size limits, preserving the following segment, and suppressed late output after cancellation. Builds pass with zero warnings/errors.

Authenticated replay exposed Google JSON arriving in binary WebSocket frames; the app now accepts bounded UTF-8 JSON in text or binary frames. The 12-clip reference pack was replayed through both services, with separate volume diagnostics because several source recordings are unusually quiet. Results and limitations are in [the audio report](../artifacts/swahili-audio/comparison.md).

The supplied 13:07 talk was extracted to MP3 and three normal-volume 30-second samples at 0:30, 6:33, and 11:37. Both modes produced captions without recorded service errors. The two-stage path translated all 13 authoritative source segments with no pending work. Its captions were generally more readable, but names remain recognition weaknesses; there is no aligned English gold reference or numerical accuracy claim. A separate direct-mode echo diagnostic is disclosed in [the talk comparison](../artifacts/sample-talk/comparison.md).

The final Apple Silicon app bundle was rebuilt and launched paused. Its new two-stage/default and Live Translate/comparison choices were inspected. Actual playback into BlackHole, without changing Mac defaults, produced English captions from clip 1570 and from the talk's 6:33 sample. Space paused capture and retained captions. The final app is left open and paused with BlackHole selected. File replay bypasses the UI; these actual app smoke checks separately establish capture-to-caption operation for the tested clips. Automatic session rollover, full-length seminars, projector hardware, and Windows/Linux capture remain unvalidated. Transcribe Live currently requires an operator restart at its session limit of up to 10 minutes.

## Fixed caption rows — October 9, 2026

An implementation agent added a fixed bottom caption area with at most three rows, filled from its top row downward. Existing rows stay in position as another row appears. Overflow replaces the whole page rather than scrolling rows upward. Updates that fit a fresh page keep their opening words; oversized updates retain a bounded tail. Short windows reduce the number of rows to avoid partial lines.

An independent reviewer found no blocking issues and reran the executable checks. Coverage includes row filling, page replacement, atomic incoming sentences, rolling source head truncation, tail corrections, repeated words, resize, Unicode graphemes, and one-row fallback. The build passed with zero warnings/errors, the Apple Silicon bundle was republished, and strict signature verification passed. A local sample preview in the rebuilt app visibly confirmed two rows at the top of the reserved bottom band with the third row remaining empty below them. The temporary preview font adjustment was restored to 48; this display validation made no cloud requests.

## Rolling captions with sealed rows — October 9, 2026

The working no-motion display was preserved in commit `6e07695`, tagged `no-motion-baseline`, before this revision. The three-row bottom area now scrolls upward one row over 200 ms while fading the outgoing top row. Completed visual rows keep their wording; corrections apply only to the active tail. Caption source identity distinguishes a revised partial from a wholly new finalized translation after the bounded source buffer loses all overlap.

An implementation agent and independent reviewer validated ordinary multi-row batches, intermediate scroll frames, sealed rows, same-source corrections, new-source no-overlap updates, Unicode, resize, and one-row fallback. Controller checks cover retained source identity across pause/settings, stale callback suppression, and clear. All checks passed; the app build had zero warnings/errors. Motion resets on resize, font changes, clear, pause, and close. The animation queue is bounded to 24 frames; extreme backlogs discard oldest queued snapshots to limit delay.

The Apple Silicon bundle was republished. A local preview at a narrowed window and font size 72 produced the expected three-row overflow tail. Clearing the preview returned the running app to an empty paused caption window; font 48 and normal window dimensions were restored. This runtime check establishes overflow rendering and cleanup, not a frame-by-frame measurement of animation smoothness, and made no cloud requests. Audio segmentation was unchanged: Google finalization plus punctuation/three-second fragment buffering remains in use.

## Full-height captions with reading time — October 9, 2026

Caption presentation now receives complete accepted results before the legacy history preview trims them. Phrases fill the available height from top to bottom. Only the rows needed by incoming text scroll away, over 300 ms; outgoing phrases must first receive their cumulative reading-time budget. Settings defaults to 180 words per minute, adjustable from 80 to 300. Oversized phrases begin at their opening and advance in paced chunks. Resize and font changes preserve unread source spans; partial revisions charge only newly exposed text. Pausing input leaves accepted captions draining. Clear removes both visible and queued text.

The queue preserves a bounded overflow item and pauses capture with a curated error when full, rather than silently replacing unread captions. Sustained speech faster than the selected reading pace can introduce delay. This policy provides time to read; it does not establish comprehension for every reader.

An implementation agent and independent reviewer checked cumulative deadlines, long phrases, multiple-row eviction, resize replay, partial corrections, Unicode, empty final updates, clear, stale callbacks, and queue limits. Executable checks pass. A packaged local preview visibly filled ten rows at font 48 and continued displaying accepted captions after returning to paused. This check made no cloud requests and did not measure animation frame timing or validate full-length seminars.

Runtime inspection also exposed a native file-open stall on the project `.env` before the first window appeared. Local configuration discovery and file actions now run off the UI thread; the caption window opens immediately, and late configuration preserves credentials edited manually meanwhile. The existing key was copied without overwriting to the private application-support `.env`; that location now takes precedence over project discovery. The original project file remains intact. Background loading and edit-preservation checks pass, and the rebuilt window opened in approximately two seconds.

The final Apple Silicon bundle passed strict signature and property-list verification, opened with configuration already loaded, and was left empty and paused with BlackHole selected. The previous fixed no-motion version remains available at `no-motion-baseline`.

## Continuous speech and word reveal — October 9, 2026

Replaced final-only source acceptance with stable interim sentence/fragment submission, removed the live-mode coalescing wait, and added recent source context for Flash. Display now admits a prefix into free rows and protects reading time per row instead of blocking a paragraph on whole-screen deadlines. New words fade in at ten words per second with fixed layout; reading time begins after reveal. Research, measured replay results, independent review, and the accuracy tradeoff are recorded in [the latency validation](live-caption-latency-validation.md) and [the primary-source research](live-caption-latency-research.md). Earlier historical validation sections describe the versions tested at those times.

## Speech and music gate — October 9, 2026

Google Mac capture now classifies audio locally, requires two seconds of speech while retaining its opening, and vetoes detected music even with speech present. The corner shows a blue idle dot or three gently pulsing speech dots. Silence does not generate audio uploads or new translation requests; previously accepted work can finish. Planned cloud idle closure and qualified-speech reconnection retain the translation pipeline. The classifier calibration, limited BlackHole runtime checks, and best-effort transcription drain limitation are recorded in [speech gate validation](speech-activity-gating-validation.md). Implementation and independent review agents checked this slice; complete checks and Apple Silicon packaging passed.

## Google session recovery — October 9, 2026

Added proactive eight-minute connection rotation, goAway handling, bounded transient recovery, and selected temporary translation-request retries. Capture, queued translations, bounded source context, and visible captions survive connection replacement. Terminal authentication/quota errors still pause with a clear error, and uncertain unfinished ASR records a diagnostic. The packaged app completed a 65-minute sparse BlackHole test with thirteen 30-second bursts and no manual restart; a separate 30-second real-cloud probe produced English from both sides of a forced fresh-connection rollover. Independent review and executable checks passed. See [session recovery validation](session-recovery-validation.md) for observations and limitations, and [Google context/cache research](google-session-caching-research.md) for the decision to retain bounded translation context. The latest bundle is open, empty, and paused with BlackHole selected.

## English caption omission — October 9, 2026

Added an always-on local blacklist of 1,314 English words and phrases, including 164 multiword entries, covering profanity, expletives, sexual/suggestive vocabulary, vulgarities, slurs and explicit variants. Every provider's interim/final update is sanitized before history and animated presentation; matches vanish without placeholders. Whole-token boundaries preserve innocent substrings. Fully omitted interim revisions retract the active tail without sealing the source, so subsequent clean content still appears. The reviewed source list, licensing, strict contextual tradeoffs and cross-finalized-chunk phrase limitation are recorded in [filter research and validation](caption-content-filter-research.md).

Complete checks and independent review passed against the final list; app build had zero warnings/errors. The Apple Silicon bundle was republished, passed strict signature/property-list verification, and contains source attribution and both upstream licenses. The rebuilt caption window opened empty and paused; BlackHole was selected. No audio playback or paid cloud requests were used for this slice.
