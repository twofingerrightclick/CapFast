# Bugs

Track observed problems here as features and fixes are planned. Findings below describe the current checkout; they do not establish which service event caused a particular live incident.

Related planned work is tracked in [features.md](features.md).

## CAP-001 — JSON or parsing error text appears in captions

**Status:** Mitigated in FEAT-003; live provider validation pending.
**Observed:** JSON and parsing error text has appeared on the caption surface.  
**Impact:** Audience-facing captions can show technical text instead of speech translation.

**Investigation:** A successful Gemini Flash response is accepted when its first candidate has `finishReason: STOP` and a nonempty `content.parts[].text`. The text is not checked for JSON, error messages, or other nontranslation output before the translation pipeline publishes it. The display filter removes blacklisted terms, not technical error text. Direct Live Translate similarly publishes `outputTranscription.text` without a content check. Actual JSON parsing exceptions and non-success HTTP responses follow the failure path; they are not intentionally rendered as captions.

A temporary read-only probe against the production parser, filter, and translation pipeline supplied `JSON parsing error: {"unexpected": true}` as successful model text. It passed unchanged through the filter and was published as a caption. This proves the route exists, but does not identify which route produced the observed live text.

**Relevant code:** `src/CapFast/Speech/FlashTextTranslator.cs` (`ParseResponse`), `src/CapFast/Speech/SourceTranslationPipeline.cs` (`Caption`), `src/CapFast/Core/GeminiTranscript.cs`, `src/CapFast/Core/CaptionController.cs` (`ApplyCaption`).

**Resolution criteria:** Technical errors and structured service output never reach the audience caption surface. A regression check covers an error-like successful model response in both Google caption modes. A rejected result is handled without exposing raw provider text or credentials.

**Planned improvement:** [FEAT-003 — Settings diagnostics](features.md#feat-003--show-errors-and-issues-below-the-settings-commands).

## CAP-002 — Recoverable failures pause captions until manually restarted

**Status:** Open  
**Observed:** Captions sometimes stop after running for a while and do not restart on their own.  
**Impact:** Captioning can remain unavailable during a live talk.

**Investigation:** A translation failure cancels and completes `SourceTranslationPipeline`; it accepts no later speech. The session emits `Ended`, and `CaptionController` sets its desired live state to false and disposes the session. A temporary probe confirmed that the pipeline rejects a new source after a translation failure. Google Live transport interruptions and some HTTP translation failures already have bounded retries, but exhausted retries and other failures terminate the session. Additional deliberate pause paths include a translation queue overflow, a display reading backlog, audio helper exit, and a 30-second timeout while waiting for accepted speech to finish transcribing. Azure cancellation or session end also leads to a controller pause.

**Relevant code:** `src/CapFast/Speech/SourceTranslationPipeline.cs` (`ProcessAsync`), `src/CapFast/Speech/GeminiCaptionSession.cs` (`ReceiveAsync`, `PumpAudioAsync`, `Fail`), `src/CapFast/Core/AudioConnectionPolicy.cs` (`Poll`), `src/CapFast/Core/CaptionController.cs` (`PauseWithErrorAsync`).

**Resolution criteria:** Transient failures recover with a bounded, observable restart while preserving safe session state. Authentication, quota, configuration, and other terminal failures remain stopped with a clear operator action. Regression checks exercise each recovery and terminal path.

**Planned improvement:** [FEAT-004 — Automatic recovery](features.md#feat-004--recover-when-captions-stop-because-of-an-issue).

## CAP-003 — A pause or gap can be hard to diagnose from the caption window

**Status:** Operator diagnostic visibility improved in FEAT-002/003; further live gap classification pending.
**Observed:** Captions can appear to pause for no apparent reason.  
**Impact:** The operator may not know whether capture stopped, is reconnecting, is waiting for translation, or is still live but has no accepted speech.

**Investigation:** While the controller is live, `CaptionWindow.Refresh` hides its status text, including connection and recovery updates. Failure details are stored in **CapFast → Errors…** and appear in the small footer only once paused. Separately, after 15 seconds without a new accepted translation, the controller fades the existing captions while remaining live. The local speech gate waits for two seconds of classified speech before upload; shorter utterances produce no caption. A display backlog can also deliberately pause capture. These mechanisms can look similar from the caption area.

**Relevant code:** `src/CapFast/Views/CaptionWindow.cs` (`Refresh`), `src/CapFast/Core/CaptionController.cs` (inactivity timer and error pause), `src/CapFast/Core/SpeechAudioGate.cs`, `src/CapFast/Core/CaptionPresentation.cs`.

**Resolution criteria:** The caption window clearly distinguishes live listening, qualifying speech, translation delay, reconnecting, and paused failure states. A failure is visible without opening the error log. An inactivity fade does not imply that capture stopped.

**Planned improvements:** [FEAT-002 — Settings commands](features.md#feat-002--put-command-buttons-first-in-settings), [FEAT-003 — Settings diagnostics](features.md#feat-003--show-errors-and-issues-below-the-settings-commands), and [FEAT-004 — Automatic recovery](features.md#feat-004--recover-when-captions-stop-because-of-an-issue).

## Evidence needed from the next live incident

- Note the time, selected provider and Google mode, and whether French captions and music rejection are enabled.
- Record the newest **CapFast → Errors…** entry after the gap. Do not include API keys or raw provider responses.
- Note whether paused controls appear, whether the audio activity indicator remains visible, and whether pressing Space restores captions.

The existing check suite passed on 2026-10-11. It does not cover error-like text returned as a successful translation or establish the cause of the reported live pause.
