# CapFast

A small Avalonia/.NET desktop prototype for live Swahili → English seminar captions. By default, Google Transcribe Live recognizes Swahili and Gemini Flash translates stable speech fragments into English. Live Translate and Azure Speech are also selectable. The main window shows white captions filling a black screen from top to bottom.

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

1. Open CapFast. The black caption window opens **paused**; no audio is captured until you start.
2. Open **CapFast → Settings…** or press **⌘,**. If configuration is missing, the caption window also shows an **Open settings** button. Enter your Google key, or select Azure and enter its key and region. Google defaults to **Swahili transcription → English translation**; **Live Translate (comparison)** selects the previous direct service. Settings does not start transcription. Keys entered in Settings stay in memory; you can load a key from a local `.env` file.
3. While paused, choose your microphone or USB mixer from the audio dropdown at bottom left. **System default** follows the Mac's input selection; **Refresh** reloads connected devices. The two bars at bottom right indicate pause.
4. Return to the caption window and press **Space** to start. Press **Space** again to pause and stop capture/streaming while keeping the last captions visible. Allow microphone access when starting; if denied, enable CapFast in **System Settings → Privacy & Security → Microphone** and relaunch.
5. Move the caption window to the projector. **F11** toggles fullscreen; **Esc** exits it. Adjust caption size or clear captions in Settings. **Preview sample captions** exercises the display without a cloud connection or microphone access.
6. Open **CapFast → Errors…** to view errors. The log keeps the latest 100 entries, automatically removing older entries; **Clear** empties it. Closing the caption window stops transcription and closes secondary windows.

Google capture on Mac now filters audio locally before upload. The corner shows a blue dot while waiting and three softly pulsing dots when speech is detected; detected music shows `♫`, and pause still shows `||`. After ten continuous seconds of detected music, visible and queued captions clear. Late translations remain hidden until music ends. Silence or speech resets that countdown. Speech must accumulate two seconds before upload, with short natural pauses allowed. Its opening audio is buffered and sent once when qualified. Shorter replies are intentionally ignored. Detected music or singing vetoes the entire interval, including speech over music. Quiet/noise and rejected audio stay on the Mac. Previously accepted speech can finish translating while the blue dot is showing.

The on-device Apple classifier is conservative and can occasionally reject speech, particularly very quiet sources. Filtering adds local detection/qualification delay. It does not promise perfect music recognition. This filter applies to Google Mac capture; the legacy Azure SDK path remains unchanged. See [speech activity research](docs/speech-activity-gating-research.md) and [fixture validation](docs/speech-activity-gating-validation.md).

Google's default mode uses `gemini-3.5-transcribe-live` with a `sw-KE` hint and verbatim transcription, then `gemini-3.8-flash` with low thinking for English text translation. Your key/project needs access and sufficient quota for both models. Live Translate uses `gemini-3.5-live-translate-preview`, automatic language detection, and English as the fixed target. Azure supports the configured `sw-KE` or `sw-TZ` source locale.

Store your key in `~/Library/Application Support/CapFast/.env` on macOS. If that file is absent, this development build falls back to `/Users/austen/Documents/Repo/CapFast/.env`. The project file is ignored by Git; configuration is never copied into the app bundle. Add:

```dotenv
GEMINI_API_KEY=your_google_api_key
```

In Settings, **Open API key file** opens the displayed file in your text editor; **Reload file** loads it while paused. The caption window opens before background configuration loading, including Finder launches. If file access stalls, the window stays usable and reports the delay. The Open button creates an empty file if needed. Rebuilding leaves configuration intact. Keys typed only in Settings remain in memory; saving requires editing the file. Late file loading preserves keys you entered while waiting.

Process environment variables override file values. Supported names are `GEMINI_API_KEY` (or `GOOGLE_API_KEY` fallback), `AZURE_SPEECH_KEY`, and `AZURE_SPEECH_REGION`. File values are plain text; do not commit or share this file.

Use **Check microphone permission** in Settings to request/check Mac permission without starting capture or connecting to a cloud service. Starting Google translation also checks permission before connecting. If permission was already denied, macOS will not show a fresh prompt: enable CapFast under **System Settings → Privacy & Security → Microphone** and relaunch. Startup errors now distinguish permission denial, network/HTTP connection failure, service access/quota/configuration rejection, and audio-helper failure; open **CapFast → Errors…** for the specific message. Service errors retain numeric codes without logging raw response bodies or credentials.

Live mode sends audio to the selected service and incurs its charges. Two-stage mode also sends stable Swahili text fragments and recent source context to Flash for translation. Direct Live Translate generates translated audio alongside transcript text; CapFast does not play that audio, but generation can still be billed. The app does not save audio or transcripts. Evaluation tools explicitly save test transcripts/results locally. Credentials are saved only when you put them in your local configuration file.

## Caption behavior and limits

Captions use the available window height and fill from top to bottom. Each incoming translation reaches the display in full, rather than being reduced to the old 42-word history tail. Completed rows retain their wording; only the active provisional tail can change. New words fade in at ten words per second while occupying their final positions. When the screen fills, it advances one row at a time after that row has had reading time, with a fade on departing rows. A phrase longer than the screen is presented in readable chunks from its beginning.

Settings provides a reading speed of 80–300 words per minute, initially 180. This sets a cumulative reading budget for each row before it can leave; new phrases fill available rows immediately. Reading time starts after the word reveal finishes. Accepted captions continue displaying after transcription pauses. Resizing or increasing the font preserves unread text and may show its opening again. A slower reading pace can delay captions behind the speaker. A bounded backlog pauses capture with an error while preserving accepted queued text, and prevents restarting until there is room. Clear captions explicitly removes visible and queued content.

The caption surface stays mounted and queues accepted text for presentation. Two-stage mode compares successive replacing Swahili interim hypotheses and translates agreed complete sentences before Google finalizes a long spoken turn. When punctuation is absent, after three seconds it can submit 6–18 stable words while retaining three mutable words. Final segments flush the remaining text and reconcile small source corrections without repeating previously submitted words. Translation runs in order through a bounded queue with no extra sentence-coalescing delay, and receives recent source context to help with fragments. This reduces waiting for pauses, but committed interim words can differ from Google's later final transcript; previously displayed English is not rewritten. Network/model delays and a reader backlog can still delay captions. If translation cannot keep up, the app stops with an error rather than silently dropping speech. Azure replaces provisional hypotheses and commits final utterances; its translation stabilization is enabled by default. Direct Live Translate returns translated transcript fragments, which the adapter accumulates and commits at completion boundaries. Completed visual rows remain sealed even if a provisional service result later revises their earlier words. See [the live-caption latency research](docs/live-caption-latency-research.md).

During silence, Google capture continues locally. After 20 seconds of idle and a best-effort transcription drain, the app deliberately releases the cloud connection; the next qualified speech reconnects automatically with its opening buffered. See the [drain limitation and validation](docs/speech-activity-gating-validation.md). Actual service failures still require **Space** to reconnect, including Google session rollover. Transcribe Live sessions last up to **10 minutes**; automatic rollover is not implemented. Automatic failure recovery, recording, transcript export, and latency/quality guarantees are outside this prototype. Authenticated audio replay of the supplied 12-clip pack is recorded in the testing guide; this is not a full-length seminar validation. Test real seminar audio, projector readability, network recovery, and a full-length session before audience use.

The UI targets cross-platform Avalonia. Google currently uses a bundled native macOS audio helper; Google audio capture on Windows/Linux is not implemented. Azure remains available through its cross-platform SDK, but Windows/Linux packaging and hardware behavior have not been validated. The executable checks do not establish cloud service quality or microphone behavior. Research and source links are in [the Azure note](docs/speech-captioning-api-research.md) and [the Google note](docs/google-captioning-research.md).

Build, independent slice review, and manual demo/fullscreen validation are recorded in [docs/prototype-validation.md](docs/prototype-validation.md).

The [Swahili testing guide](docs/swahili-testing.md) covers comparing the pack's JSON transcripts through Gemini or Translation LLM and playing its audio through BlackHole for a manual caption check.
