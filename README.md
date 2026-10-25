# CapFast

A small Avalonia/.NET desktop prototype for live seminar captions. Google transcription and translation detects each emitted speech segment’s input language and translates it into the other two among English, Swahili and French. Other input languages produce English and French. The audience window shows two caption surfaces on a dark or light background.

## Run on a Mac

Install the .NET 10 SDK and Apple's Command Line Tools (`xcode-select --install`) for the Swift audio helper. From this directory:

```sh
dotnet build src/CapFast/CapFast.csproj
dotnet run --project tests/CapFast.Checks/CapFast.Checks.csproj
bash scripts/publish-macos.sh
open artifacts/macos/osx-arm64/CapFast.app
```

The script defaults to Apple Silicon. Pass `osx-x64` for an Intel Mac. It requires the **CapFast Local Signing** code-signing identity in the login Keychain (or set `CAPFAST_SIGN_IDENTITY` to another installed identity). It signs the audio helper and app with fixed identifiers, then verifies the bundle. This Mac has the local identity installed; on another Mac, create or import a persistent code-signing identity before publishing. The self-signed local build is for microphone testing, not notarized distribution. `dotnet run --project src/CapFast/CapFast.csproj` is also useful for UI/demo development, but microphone permission attribution may differ when launched through a terminal.

## Run on Windows

Install the .NET 10 SDK, then run `pwsh scripts/publish-windows.ps1` and launch
`artifacts/windows/win-x64/CapFast.exe`. Pass `-Runtime win-arm64` for ARM64.
Distribute the whole published folder. ONNX Runtime may require the Microsoft
Visual C++ 2015–2022 Redistributable matching the architecture. Enable
microphone access for desktop apps in Windows Settings. Windows capture is
implemented but still needs hardware validation; see [audio setup and validation](docs/cross-platform-audio.md).

## Use it

1. Open CapFast. The audience caption window opens **paused** with Settings alongside it; no audio is captured until you start.
2. In Settings, enter your Google key. **Auto transcription → two output languages** is the caption workflow. Keys entered in Settings stay in memory; you can load a key from a local `.env` file. Reopen Settings with **CapFast → Settings…** or **⌘,**.

   Turn on **Appearance → Light mode** for dark captions on a white background and light settings and error windows. Turn it off to return to dark mode. Appearance changes immediately, including during transcription, and is saved for the next launch.

   Every run starts in **Auto**. The input indicator follows sustained language evidence; individual segments are translated in their own language, including alternating speakers. Five seconds without speech releases the assignment. The three compact language controls show the input in red and the two outputs in green. Click a language to hint the next utterance, or use **Reset output languages** to wait for fresh detection. The session state shows input → both outputs. English and Swahili share the left caption surface; French stays on the right, so alternating speakers do not clear either column. During Detecting, captions remain until replaced or their usual inactivity timeout.

3. While paused, choose your microphone or USB mixer in Settings. The selected device and availability remain visible there. **System default** uses the system input selected when capture starts; **Refresh inputs** reloads connected devices. CapFast remembers the last audio input, caption size, and reading speed in `~/Library/Application Support/CapFast/settings.json` and restores them on launch. If the saved input is disconnected, choose another input.
4. Use **Start captions** and **Pause captions** in the left column of Settings to control capture. The nearby `·` and `•••` indicators show idle/paused and speech activity. Pause captions when music plays; resume when speech begins. Allow microphone access when starting; if denied, enable CapFast in **System Settings → Privacy & Security → Microphone** and relaunch.
   On macOS and Windows, the transcription column also shows a peak **input level** meter (dBFS, red near clipping) with **Monitor** on its right. Open Monitor to check audio locally, even while captions are paused. Choose an audio output (headphones, USB interface, or system default), then click **Listen**; the same button becomes **Stop listening**. Stop before changing outputs. Closing the dialog or Settings stops listening. Monitoring does not upload audio or change the system’s default output. Use headphones to avoid feedback. The main meter runs during captions; opening Monitor enables it for a paused sound check. Device changes stop monitoring; refresh outputs and listen again after reconnecting.

5. Move the caption window to the projector. **Fullscreen** is beside the live output heading in Settings; **F11** toggles it from the display, and **Esc** exits it. Adjust caption size or clear captions in Settings. **Preview sample captions** exercises the display without a cloud connection or microphone access.
6. Read the newest issue and recent connection, recovery, and error events in the Diagnostics expander at the bottom of Settings. The history keeps recent entries and can be cleared there. Closing the caption window stops transcription and closes secondary windows.

### Text slideshows

Below the slideshow preview and navigation controls in Settings, drop one `.pptx` into the drop area, or use **Choose PowerPoint…**. CapFast reads slide titles and text boxes locally; PowerPoint does not need to be installed. Slides use a simple white-on-black layout, with a matching preview. Each paragraph retains its left, center, right, or justified alignment, including alignment inherited from the slide layout or master. Images, animations, and other original PowerPoint formatting are omitted; image-only decks are rejected. The limit is 500 slides and 100 MB.

Use **Previous / Next** to select a slide. Click the **Slideshow** or **Transcription** preview to select audience content, or focus a preview and press Enter or Space. A green outline marks the selected source. The large **Live output** monitor mirrors the audience window; its **Showing** label identifies the active source. Selecting the active preview keeps that output showing. Navigation updates the audience immediately while slideshow output is on, or just prepares the preview while translations are on. Captions continue processing behind the slides. Loading a new deck starts on slide 1 with translations showing. Closing and reopening Settings preserves the deck and selection until the app exits.

Google capture on macOS and Windows detects speech locally with bundled Silero VAD before upload. Pause captions manually when music plays and resume when speech begins. There is no music detection, automatic music rejection, or music-triggered caption suppression.

Captions fade after fifteen seconds without an accepted nonempty translation, once all queued text has received its reading time. This timeout measures caption arrivals, so it can occur during ongoing speech if translations are delayed. New caption text restarts that timer and cancels an in-progress fade. Pausing cancels pending timeouts and preserves captions. Speech must accumulate two seconds before upload, with short natural pauses allowed. Its opening audio is buffered and sent once when qualified. Shorter replies are intentionally ignored.

The local Silero speech detector can miss speech, particularly very quiet sources. Speech filtering adds local detection/qualification delay and runs identically on macOS and Windows. The earlier music-detection research and fixture results in `docs/speech-activity-gating-*.md` are historical and do not describe the current feature set.

Google uses `gemini-3.5-transcribe-live` with English/Swahili/French recognition hints and verbatim transcription, then `gemini-3.8-flash` with low thinking to identify each fragment and return structured English, Swahili, and French renderings in one request. The app selects the two output languages from that result. Your key/project needs access and sufficient quota for both models.

Store your key in `~/Library/Application Support/CapFast/.env` on macOS. If that file is absent, this development build falls back to `/Users/austen/Documents/Repo/CapFast/.env`. The project file is ignored by Git; configuration is never copied into the app bundle. Add:

```dotenv
GEMINI_API_KEY=your_google_api_key
```

In Settings, **Open API key file** opens the displayed file in your text editor; **Reload file** loads it while paused. The caption window opens before background configuration loading, including Finder launches. If file access stalls, the window stays usable and reports the delay. The Open button creates an empty file if needed. Rebuilding leaves configuration intact. Keys typed only in Settings remain in memory; saving requires editing the file. Late file loading preserves keys you entered while waiting.

Process environment variables override file values. Supported names are `GEMINI_API_KEY` (or `GOOGLE_API_KEY` fallback). File values are plain text; do not commit or share this file.

Use **Check microphone permission** in Settings to request/check Mac permission without starting capture or connecting to a cloud service. Starting Google translation also checks permission before connecting. If permission was already denied, macOS will not show a fresh prompt: enable CapFast under **System Settings → Privacy & Security → Microphone** and relaunch. Startup errors distinguish permission denial, network/HTTP connection failure, service access/quota/configuration rejection, and audio-helper failure. The Settings diagnostic area shows the latest issue and recent history without raw response bodies or credentials.

Captions send audio to Google Transcribe Live and stable source text fragments with recent context to Flash for translation. Both stages incur their model charges. The app does not save audio or transcripts. Evaluation tools explicitly save test transcripts/results locally. Credentials are saved only when you put them in your local configuration file.

## Caption behavior and limits

The existing local blacklist applies to English captions. Swahili and French captions do not yet have language-specific blacklists.

An always-on local English blacklist removes profanity, vulgar/sexual terms (including “sex”), and listed phrases before interim or final captions enter the display. Matches are omitted without dots, asterisks, or other replacement text. Matching uses whole words and phrases rather than substrings inside ordinary words. The bundled list is maintained in [EnglishCaptionBlacklist.txt](src/CapFast/Core/EnglishCaptionBlacklist.txt); changes require rebuilding the app. This is a word/phrase blacklist, so unlisted euphemisms and suggestive meaning expressed in otherwise ordinary words are outside its coverage. Phrases match within a caption update; independently finalized chunks can split a multiword phrase. See [coverage and sources](docs/caption-content-filter-research.md).

Captions use the available window height and fill from top to bottom. Incoming translations are queued in full. Automatic caption catch-up may skip older queued fragments when newer captions are waiting; turning catch-up off preserves the complete display queue. Completed rows retain their wording; only the active provisional tail can change. New words fade in at ten words per second while occupying their final positions. When the screen fills, it advances one row at a time after that row has had reading time, with a fade on departing rows. A phrase longer than the screen is presented in readable chunks from its beginning.

The **Reading speed & pacing** expander in Settings provides a default reading speed of 80–300 words per minute, initially 180, with optional English, Swahili, and French overrides. Overrides follow each caption’s output language, including English and Swahili sharing the left column. This sets a cumulative reading budget for each row before it can leave; new phrases fill available rows immediately. Reading time starts after the word reveal finishes. Accepted captions continue displaying after transcription pauses. Resizing or increasing the font preserves unread text and may show its opening again. A slower reading pace can delay captions behind the speaker. **Automatic caption catch-up** is on by default: once queued text has waited three seconds it shortens reading holds and animations; older fragments waiting over six seconds may be skipped when a newer fragment exists. It does not discard the only remaining fragment. Settings shows the oldest queued text’s display wait separately for each column, a catch-up indicator, and the count of skipped fragments. Display wait excludes speech recognition, translation API time, and text already fully presented; it is not an end-to-end latency measurement. Catch-up and all reading-speed preferences are saved across launches. A bounded backlog pauses capture with an error while preserving accepted queued text, and prevents restarting until there is room. Clear captions explicitly removes visible and queued content.

The caption surface stays mounted and queues accepted text for presentation. Two-stage mode compares successive replacing source interim hypotheses and translates agreed complete sentences before Google finalizes a long spoken turn. When punctuation is absent, after two seconds it can submit 4–18 stable words while retaining two mutable words. This favors timely meaning over waiting for word-perfect final transcription. Final segments flush the remaining text and reconcile small source corrections without repeating previously submitted words. Up to two source fragments are translated concurrently with source context captured in acceptance order; captions appear in that same order within each speech block. This reduces waiting for pauses, but committed interim words can differ from Google's later final transcript; previously displayed captions are not rewritten. Network/model delays and a reader backlog can still delay captions. Accepted fragments no longer expire while waiting in the translation queue. A request still pending after four seconds may start one identical spare request and use whichever valid response arrives first. This can incur an extra Google API charge for slow requests; one spare request and three total HTTP calls are allowed in flight. Each attempt has a twelve-second timeout with a bounded retry; exhausted failures pause visibly rather than skipping speech and restarting repeatedly. Provider-blocked text is withheld with a diagnostic. If the bounded source queue fills, capture still stops with an error. Slow service requests can delay ordered captions. Automatic inactivity fading waits for unread captions to finish their presentation and reading time. Completed visual rows remain sealed even if a provisional service result later revises their earlier words. See [the live-caption latency research](docs/live-caption-latency-research.md).

During silence, Google capture continues locally. After 20 seconds of idle and a best-effort transcription drain, the app deliberately releases the cloud connection; the next qualified speech reconnects automatically with its opening buffered. See the [drain limitation and validation](docs/speech-activity-gating-validation.md). Google connections now rotate automatically at eight minutes or on an expiry warning. Temporary transport failures use bounded retries while capture continues into a twenty-second local queue; authentication, quota, configuration, unknown policy errors, or exhausted recovery still pause and require **Space** after the problem is resolved. Possible unfinished transcription loss is recorded in Errors. Google does not provide a verified audio acceptance ledger, so recovery is best-effort. Recording, transcript export, and latency/quality guarantees are outside this prototype. See [recovery validation](docs/session-recovery-validation.md). Authenticated audio replay of the supplied 12-clip pack is recorded in the testing guide; this is not a full-length seminar validation. Test real seminar audio, projector readability, network recovery, and a full-length session before audience use.

The UI targets cross-platform Avalonia. Audio capture uses a bundled native helper on macOS and NAudio WASAPI on Windows, with shared Silero detection. Windows microphone hardware behavior has not yet been validated. Linux capture is not implemented. See [cross-platform audio](docs/cross-platform-audio.md). The executable checks do not establish cloud service quality or microphone behavior. Research and source links are in [the Google note](docs/google-captioning-research.md).

Build, independent slice review, and manual demo/fullscreen validation are recorded in [docs/prototype-validation.md](docs/prototype-validation.md).

The [Swahili testing guide](docs/swahili-testing.md) covers comparing the pack's JSON transcripts through Gemini or Translation LLM and playing its audio through BlackHole for a manual caption check.
