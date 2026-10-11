# CapFast

A small Avalonia/.NET desktop prototype for live Swahili → English seminar captions, with optional French captions alongside English. By default, Google Transcribe Live recognizes Swahili and Gemini Flash translates stable speech fragments into English. Live Translate and Azure Speech are also selectable. The main window shows white captions filling a black screen from top to bottom.

## Run on a Mac

Install the .NET 10 SDK and Apple's Command Line Tools (`xcode-select --install`) for the Swift audio helper. From this directory:

```sh
dotnet build src/CapFast/CapFast.csproj
dotnet run --project tests/CapFast.Checks/CapFast.Checks.csproj
bash scripts/publish-macos.sh
open artifacts/macos/osx-arm64/CapFast.app
```

The script defaults to Apple Silicon. Pass `osx-x64` for an Intel Mac. It creates a self-contained `.app` with the microphone permission description and signs it locally with an ad hoc signature. Launch this bundle for live microphone testing. It is a local prototype build, not a notarized distribution installer. `dotnet run --project src/CapFast/CapFast.csproj` is also useful for UI/demo development, but microphone permission attribution may differ when launched through a terminal.

## Use it

1. Open CapFast. The audience caption window opens **paused** with Settings alongside it; no audio is captured until you start.
2. In Settings, enter your Google key, or select Azure and enter its key and region. Google defaults to **Swahili transcription → English translation**; **Live Translate (comparison)** selects the previous direct service. Keys entered in Settings stay in memory; you can load a key from a local `.env` file. Reopen Settings with **CapFast → Settings…** or **⌘,**.

   Turn on **Appearance → Light mode** for dark captions on a white background and light settings and error windows. Turn it off to return to dark mode. Appearance changes immediately, including during transcription, and is saved for the next launch.

   While paused, turn on **Add French captions** under Google to show **English on the left and French on the right**. Speech remains Swahili (`sw-KE`): Google TwoStage transcribes it once, then Flash translates each stable fragment directly into both languages with separate source context. English/French pairs arrive together in speech order. This adds one Flash request per fragment; the pair waits for both translations. Switching caption languages clears visible and queued captions. French is off by default, is available with Google TwoStage, and stays in memory until the app closes. The sample preview includes both languages when enabled.
3. While paused, choose your microphone or USB mixer in Settings. The selected device and availability remain visible there. **System default** follows the Mac's input selection; **Refresh inputs** reloads connected devices.
4. Use **Start captions** and **Pause captions** at the top of Settings to control capture. The nearby `·`, `•••`, and `♫` indicators show paused, live transcription, and detected music. Allow microphone access when starting; if denied, enable CapFast in **System Settings → Privacy & Security → Microphone** and relaunch.
5. Move the caption window to the projector. **Toggle display fullscreen** is in Settings; **F11** toggles it from the display, and **Esc** exits it. Adjust caption size or clear captions in Settings. **Preview sample captions** exercises the display without a cloud connection or microphone access.
6. Read the newest issue and recent connection, recovery, and error events directly below the Settings commands. The history keeps recent entries and can be cleared there. Closing the caption window stops transcription and closes secondary windows.

Google capture on Mac filters audio locally before upload. **Reject detected music and singing** is off by default in Settings. With it off, music-labeled audio counts toward the speech gate and can be sent to Google; pause manually when music plays. Turn it on while paused to reject music and singing on the next start. The Settings status shows `·` while paused, `•••` during live transcription, and `♫` for detected music. After seven continuous seconds of rejected music, captions fade out over 800 ms and queued captions clear. Captions also fade after fifteen seconds without an accepted nonempty translation; new caption text restarts that timer and cancels an in-progress fade. Pausing cancels pending timeouts and preserves captions. Speech must accumulate two seconds before upload, with short natural pauses allowed. Its opening audio is buffered and sent once when qualified. Shorter replies are intentionally ignored. Quiet/noise and rejected audio stay on the Mac. Previously accepted speech can finish translating while the live indicator is showing.

The on-device Apple classifier is conservative and can occasionally reject speech, particularly very quiet sources. Filtering adds local detection/qualification delay even when music rejection is off. It does not promise perfect music recognition. The music choice stays in memory until the app closes and defaults off on each launch. This filter applies to Google Mac capture; the legacy Azure SDK path remains unchanged. See [speech activity research](docs/speech-activity-gating-research.md) and [fixture validation](docs/speech-activity-gating-validation.md).

Google's default mode uses `gemini-3.5-transcribe-live` with a `sw-KE` hint and verbatim transcription, then `gemini-3.8-flash` with low thinking for English text translation. Your key/project needs access and sufficient quota for both models. Live Translate uses `gemini-3.5-live-translate-preview`, automatic language detection, and English as the fixed target. Azure supports the configured `sw-KE` or `sw-TZ` source locale.

Store your key in `~/Library/Application Support/CapFast/.env` on macOS. If that file is absent, this development build falls back to `/Users/austen/Documents/Repo/CapFast/.env`. The project file is ignored by Git; configuration is never copied into the app bundle. Add:

```dotenv
GEMINI_API_KEY=your_google_api_key
```

In Settings, **Open API key file** opens the displayed file in your text editor; **Reload file** loads it while paused. The caption window opens before background configuration loading, including Finder launches. If file access stalls, the window stays usable and reports the delay. The Open button creates an empty file if needed. Rebuilding leaves configuration intact. Keys typed only in Settings remain in memory; saving requires editing the file. Late file loading preserves keys you entered while waiting.

Process environment variables override file values. Supported names are `GEMINI_API_KEY` (or `GOOGLE_API_KEY` fallback), `AZURE_SPEECH_KEY`, and `AZURE_SPEECH_REGION`. File values are plain text; do not commit or share this file.

Use **Check microphone permission** in Settings to request/check Mac permission without starting capture or connecting to a cloud service. Starting Google translation also checks permission before connecting. If permission was already denied, macOS will not show a fresh prompt: enable CapFast under **System Settings → Privacy & Security → Microphone** and relaunch. Startup errors distinguish permission denial, network/HTTP connection failure, service access/quota/configuration rejection, and audio-helper failure. The Settings diagnostic area shows the latest issue and recent history without raw response bodies or credentials.

Live mode sends audio to the selected service and incurs its charges. Two-stage mode also sends stable Swahili text fragments and recent source context to Flash for translation. Direct Live Translate generates translated audio alongside transcript text; CapFast does not play that audio, but generation can still be billed. The app does not save audio or transcripts. Evaluation tools explicitly save test transcripts/results locally. Credentials are saved only when you put them in your local configuration file.

## Caption behavior and limits

The existing local blacklist applies to English captions. French captions do not yet have a French blacklist.

An always-on local English blacklist removes profanity, vulgar/sexual terms (including “sex”), and listed phrases before interim or final captions enter the display. Matches are omitted without dots, asterisks, or other replacement text. Matching uses whole words and phrases rather than substrings inside ordinary words. The bundled list is maintained in [EnglishCaptionBlacklist.txt](src/CapFast/Core/EnglishCaptionBlacklist.txt); changes require rebuilding the app. This is a word/phrase blacklist, so unlisted euphemisms and suggestive meaning expressed in otherwise ordinary words are outside its coverage. Phrases match within a caption update; independently finalized chunks can split a multiword phrase. See [coverage and sources](docs/caption-content-filter-research.md).

Captions use the available window height and fill from top to bottom. Each incoming translation reaches the display in full, rather than being reduced to the old 42-word history tail. Completed rows retain their wording; only the active provisional tail can change. New words fade in at ten words per second while occupying their final positions. When the screen fills, it advances one row at a time after that row has had reading time, with a fade on departing rows. A phrase longer than the screen is presented in readable chunks from its beginning.

Settings provides a reading speed of 80–300 words per minute, initially 180. This sets a cumulative reading budget for each row before it can leave; new phrases fill available rows immediately. Reading time starts after the word reveal finishes. Accepted captions continue displaying after transcription pauses. Resizing or increasing the font preserves unread text and may show its opening again. A slower reading pace can delay captions behind the speaker. A bounded backlog pauses capture with an error while preserving accepted queued text, and prevents restarting until there is room. Clear captions explicitly removes visible and queued content.

The caption surface stays mounted and queues accepted text for presentation. Two-stage mode compares successive replacing Swahili interim hypotheses and translates agreed complete sentences before Google finalizes a long spoken turn. When punctuation is absent, after three seconds it can submit 6–18 stable words while retaining three mutable words. Final segments flush the remaining text and reconcile small source corrections without repeating previously submitted words. Translation runs in order through a bounded queue with no extra sentence-coalescing delay, and receives recent source context to help with fragments. This reduces waiting for pauses, but committed interim words can differ from Google's later final transcript; previously displayed English is not rewritten. Network/model delays and a reader backlog can still delay captions. If translation cannot keep up, the app stops with an error rather than silently dropping speech. Azure replaces provisional hypotheses and commits final utterances; its translation stabilization is enabled by default. Direct Live Translate returns translated transcript fragments, which the adapter accumulates and commits at completion boundaries. Completed visual rows remain sealed even if a provisional service result later revises their earlier words. See [the live-caption latency research](docs/live-caption-latency-research.md).

During silence, Google capture continues locally. After 20 seconds of idle and a best-effort transcription drain, the app deliberately releases the cloud connection; the next qualified speech reconnects automatically with its opening buffered. See the [drain limitation and validation](docs/speech-activity-gating-validation.md). Google connections now rotate automatically at eight minutes or on an expiry warning. Temporary transport failures use bounded retries while capture continues into a twenty-second local queue; authentication, quota, configuration, unknown policy errors, or exhausted recovery still pause and require **Space** after the problem is resolved. Possible unfinished transcription loss is recorded in Errors. Google does not provide a verified audio acceptance ledger, so recovery is best-effort. Recording, transcript export, and latency/quality guarantees are outside this prototype. See [recovery validation](docs/session-recovery-validation.md). Authenticated audio replay of the supplied 12-clip pack is recorded in the testing guide; this is not a full-length seminar validation. Test real seminar audio, projector readability, network recovery, and a full-length session before audience use.

The UI targets cross-platform Avalonia. Google currently uses a bundled native macOS audio helper; Google audio capture on Windows/Linux is not implemented. Azure remains available through its cross-platform SDK, but Windows/Linux packaging and hardware behavior have not been validated. The executable checks do not establish cloud service quality or microphone behavior. Research and source links are in [the Azure note](docs/speech-captioning-api-research.md) and [the Google note](docs/google-captioning-research.md).

Build, independent slice review, and manual demo/fullscreen validation are recorded in [docs/prototype-validation.md](docs/prototype-validation.md).

The [Swahili testing guide](docs/swahili-testing.md) covers comparing the pack's JSON transcripts through Gemini or Translation LLM and playing its audio through BlackHole for a manual caption check.
