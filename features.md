# Features and improvements

Working backlog for CapFast. Items describe desired behavior, not completed work. Bug references point to [bugs.md](bugs.md).

## FEAT-002 — Put command buttons first in Settings - seetings becomes a command center as well.

Make the caption control buttons a top-level group at the top of Settings, easy to reach during a live session. Keep configuration controls below the commands. Build a robust command center. It should have options for everyting, and have a top level status indicatcors (the ... indicator that transctiption is running, . when none, music indactor). Overall the display window is just for display, and controls move to the settings window.

Select and change the audio input in Settings. Remove the input selector and related input controls from the audience caption display. The chosen device and its availability should remain visible to the operator in Settings.

## FEAT-003 — Show errors and issues below the Settings commands

Place a visible diagnostic output area immediately below the command button group in Settings. Log JSON parse failures, service errors, connection and recovery events, and other issues there. Keep technical text out of audience captions. Preserve useful error history and make the latest issue clear without opening a separate window. Do not show credentials or raw sensitive provider responses.

**Related:** [CAP-001](bugs.md#cap-001--json-or-parsing-error-text-appears-in-captions), [CAP-003](bugs.md#cap-003--a-pause-or-gap-can-be-hard-to-diagnose-from-the-caption-window).

## FEAT-001 — Optional caption gap

When there is a Gap in audio speech input, then create a double new line. Gap must be 5 setnences.

**Related:** [CAP-003](bugs.md#cap-003--a-pause-or-gap-can-be-hard-to-diagnose-from-the-caption-window), since the operator needs an obvious place to see and control session state.

## FEAT-004 — Recover when captions stop because of an issue

Automatically resume captioning after recoverable failures, with bounded retries and a visible recovery state. Preserve operator intent: a manual Pause stays paused. Show a clear issue and required action when recovery is not possible, such as invalid credentials or exhausted quota. Ensure the caption stream and command controls remain usable after recovery.

**Related:** [CAP-002](bugs.md#cap-002--recoverable-failures-pause-captions-until-manually-restarted), [CAP-003](bugs.md#cap-003--a-pause-or-gap-can-be-hard-to-diagnose-from-the-caption-window).

## FEAT-005 — Cross-platform music and singing interruption control

Keep music and singing rejection as a command toggle. When enabled, require **five continuous seconds** of detected music or singing before interrupting caption requests. A shorter detection must not interrupt them, and speech or other nonmusic sound must reset the five-second count. Applause must not be classified as music. Minimize false positives, especially during speech, applause, and mixed room audio. Use a solution that does not depend on Apple-only APIs and works on Windows.

**Validation needed:** Test representative live speech, singing, instrumental music, applause, and mixed audio on both macOS and Windows. Define how audio is handled during the five-second qualification window so speech is not lost.

**Related:** [CAP-003](bugs.md#cap-003--a-pause-or-gap-can-be-hard-to-diagnose-from-the-caption-window), because music handling must be visibly distinct from a failed or paused session.

## FEAT-006 — Manual speaker marker and alternating colors

Add a **Begin new speaker** command button. Pressing it assigns all subsequent audio and captions to the next speaker color; each press alternates **green** and **pink**. Show a colored bar at the left edge of caption text. Scale the bar with the current text size and keep it associated with the correct speaker as captions scroll or are revised.

**Definition needed:** Whether the initial speaker starts green or pink, and how a speaker change applies to speech already buffered or awaiting translation.

## FEAT-007 — English, Swahili, and French language combinations

Support every directional source-to-target pair among English, Swahili, and French: English → Swahili, English → French, Swahili → English, Swahili → French, French → English, and French → Swahili. Keep language selection understandable in Settings and show the selected direction in session state. Also there is the issue where sometimes there are two speakers, one speaking langauge a and the other speaking lagage b. This is really complicated, because its hard to determine which langauge to should be the 2 that are outputted. In this case just pick one as the input. The next time there is a 5 second gap in speech then reset the candidates for the 2 outputs. Because this is tricky, there should be a command added ti the control panel to reset outputted languges. Resetting should only chnage whatever outputted languge needs to change - so if foo is audio, then bar a baz are captioned, but if audio becomes baz, then bar captions should remain on the screen.

There should be three "on air" looking components that show whats being captioned and whats the input availabe in the command console.

**Definition needed:** Whether “all combos” also includes simultaneous output in two languages, automatic source-language detection, or same-language transcription.

## FEAT-009 — Secure Wi-Fi command web app with QR pairing

Add a web command app that pairs with the desktop app over local Wi-Fi using a QR code. The top portion shows current session state and captions; command buttons sit below. The command app should expose the live controls needed by an operator, including Start/Pause, Begin new speaker, and the music/singing toggle.

**Security requirements:** Pairing requires an explicit, short-lived code or token shown by the desktop app. An unpaired device cannot view captions or send commands. Provide a way to revoke a paired device or end its session. Do not expose API keys or service credentials through the web app. Define behavior for Wi-Fi loss and reconnects.

**Related:** [CAP-002](bugs.md#cap-002--recoverable-failures-pause-captions-until-manually-restarted), [CAP-003](bugs.md#cap-003--a-pause-or-gap-can-be-hard-to-diagnose-from-the-caption-window), since the remote view must show recovery and failure states accurately.
