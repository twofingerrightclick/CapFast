# Windows and macOS capture

Both platforms now use the bundled Silero VAD model with CPU ONNX Runtime.
macOS uses the Swift helper solely for PCM capture/conversion and local audio
monitoring; Windows uses NAudio WASAPI shared-mode capture. Speech detection
runs in .NET, off the UI and native capture callback threads. There is no
music detection or automatic music rejection. Pause captions during music.

## Audio contract

Capture produces little-endian PCM16, 16 kHz, mono. Windows asks the shared
WASAPI engine to convert to that format. The stream adapter accepts arbitrary
byte boundaries, including split samples. Silero processes contiguous 512-sample
windows, preserving recurrent state and 64 samples of context. A probability
of at least 0.5 marks a window as speech; any overlap marks a 100 ms gate frame
as speech. The original PCM is emitted unchanged. Classification adds at most
32 ms of chunk alignment delay; the existing two-second speech qualification
and 400 ms quiet timeout still apply. A final incomplete window/frame is
discarded on stop, never padded or uploaded after pause.

Windows capture has a bounded 200-callback queue; classified audio retains the
existing 200-frame queue. Overflow, stopped devices, missing runtime/model,
and capture timeout stop the session visibly. Windows microphone access is
checked when WASAPI starts; enable microphone access for desktop apps in
Windows Settings if capture fails. The selected endpoint is fixed for a
session; pause and restart after changing the system default device.

## Packaging

- `bash scripts/publish-macos.sh [osx-arm64|osx-x64]` builds and signs the native helper.
- `pwsh scripts/publish-windows.ps1 [-Runtime win-x64|win-arm64]` publishes a self-contained folder
  at `artifacts/windows/<runtime>/YYYY-MM-DD-HHmmss/`. Its executable is named
  `CapFast-YYYY-MM-DD-HHmmss.exe` using local build time, which also identifies
  the product version. Keep each build's files together when distributing updates.
- Distribute the entire output including `Models/silero_vad.onnx`, ONNX Runtime
  native libraries, and licenses. Windows may require the Microsoft Visual C++
  2015–2022 Redistributable for its architecture.
- Local input metering and listen-through monitoring are implemented on macOS and Windows.
  Windows uses a separate 48 kHz, 16-bit stereo WASAPI capture stream for local
  sound checks, including while captions are paused. Playback targets the selected
  fixed output endpoint in shared mode, with a 250 ms bounded buffer; it never
  changes the system default. Stop immediately gates new playback reads to silence
  and asynchronously disposes the engines. Rapid toggles wait for previous teardown.
  Device removal, selected default-device changes, and missing audio stop monitoring.
  This does not use cloud services or require a Google key.
- Linux capture is not implemented.

## Validation

The [45-case sample-audio sweep](local-speech-validation.md) covers the complete English/Swahili talks, quiet FLEURS recordings, alternating playlists, and negative controls. It found significant detected speech withheld by the retained two-second gate; see the results before treating smoke-test success as caption coverage.

Run `dotnet run --project tests/CapFast.Checks` for regression checks, including
real ONNX inference on silence and state reset, byte-exact framing, and the
existing gate/recovery/caption checks. For local speech fixtures, convert WAV
files with the Mac helper's `--pcm-file input.wav > output.pcm` mode, then run
`dotnet run --project tests/CapFast.Checks -- --local-speech-checks output.pcm`.
This path exercises actual Silero plus the production gate without cloud use.

Before calling a Windows release hardware-validated, test on Windows: device
listing and saved selection, default and USB microphones, privacy denial,
unplugging a device, silence, quiet speech, pause/resume, repeated starts, and
app shutdown. Check both selected and default devices at 44.1/48 kHz. A Mac
cross-publish proves artifact generation, not Windows capture behavior.

## Primary references

- https://github.com/naudio/NAudio/blob/main/Docs/WasapiRecorder.md
- https://github.com/snakers4/silero-vad
- https://onnxruntime.ai/docs/get-started/with-csharp.html
- https://onnxruntime.ai/docs/install/

## Local implementation results

On the development Apple Silicon Mac, the real ONNX model passed silence and
reset checks. Production PCM framing and the gate processed an existing 12.0 s
English clip (6.7 s speech labels, 2.7 s through the gate) and a 6.2 s Swahili
clip (5.2 s speech labels and through the gate). These are smoke tests, not an
accuracy benchmark: the retained two-second gate still omits short utterances.
The full executable regression suite passed. The Mac arm64 helper compiled;
the signed Mac arm64 bundle and self-contained Windows x64 folder published.
Windows microphone access and ARM64 hardware have not been exercised here.

### Windows monitoring validation

The shared monitor health and playback-buffer checks run on macOS without Windows
hardware: startup/stall deadlines, sample-preview exclusion, dBFS calculations,
byte-exact playback, underrun silence, overflow bounds, and cancellation silence.
The Windows APIs compile here; physical Windows capture and playback still require
validation: test default/USB input and headphones, output selection, Listen/Stop,
rapid toggles, closing the dialog and app, permission denial, unplugging either
endpoint, changing defaults, and running captions concurrently. Verify no sound
continues after Stop and no delayed audio builds up during a prolonged sound check.

### macOS monitoring startup regression

On 2026-10-25, BlackHole input monitoring exited immediately with
`MONITOR_DEVICE_CHANGED`, while transcription continued normally. Selecting the
input delivered a delayed engine configuration notification after startup with
the capture engine still running. The helper now compares the current device,
format, and running state before stopping; it observes playback only when listening.
Native monitor exit details are also recorded in Diagnostics.

Validated in the signed Apple Silicon app with BlackHole 2ch input and MacBook Pro
Speakers output: paused sound check, sustained level updates, Listen/Stop/restart,
and user-confirmed audible monitoring. The full .NET checks passed. The shared
checks do not reproduce CoreAudio notification timing; repeat this hardware test
after native monitor changes. Device unplugging and format changes still need
separate hardware validation.
