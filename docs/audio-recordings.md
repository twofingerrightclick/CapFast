# Local audio recordings for QA

Real caption capture automatically records the same 16 kHz, 16-bit mono PCM that enters local speech detection. This includes silence, short speech, and audio captured while Google is disconnected or recovering. Preview sample captions and standalone monitoring do not record. Starting/stopping the caption capture owns the recorder lifetime; pause or shutdown flushes queued audio and finalizes the last file.

**View recordings** beside **Preview sample captions** opens the root folder with the platform shell. Windows uses `%LOCALAPPDATA%\CapFast\Recordings\YYYY-MM-DD\`; the exact platform path is also displayed in **Audio, appearance & configuration**. Users pause captions, then copy/share the relevant date folder themselves. There is no automatic upload, transcript export, or automatic deletion. Each start creates new timestamped files with collision-resistant suffixes; continuous capture splits at 30 minutes and changes folders when the local date changes.

## Quality and storage

FLAC losslessly preserves every captured sample. It is the caption system's speech-band input, rather than the device's original higher-rate or multichannel stream. The small managed encoder uses the [RFC 9639 FLAC format](https://www.rfc-editor.org/rfc/rfc9639.html), fixed predictors of order 0–2, Rice residual coding, constant frames for silence, and verbatim frames when compression would increase size. It writes frame CRCs and finalizes the source sample count and MD5. No new native dependencies or external codec executables are shipped.

The supplied `alternating-eng-swa-long-pause-83s.wav` encoded to 1.38 MB, projecting to **59.9 MB/hour**, with exact sample recovery. This is a sample measurement, not a size cap: noisy/incompressible input can approach 116 MB/hour. Source quality is preserved regardless of compression ratio.

A single background worker owns the files and codec. Capture only copies into a bounded queue of at most 100 packets of up to 3,200 bytes (320 KB, normally ten seconds). Overflow or storage errors stop recording for that caption session and report a diagnostic, while caption capture continues. Restarting captions retries recording. Completed FLAC frames flush about once per second; abrupt process exit can lose the queue and unfinished frame, but completed frames have an independently decodable stream header with unknown duration/checksum until finalization.

## Validation

Run the normal checks or just the recording checks:

```sh
dotnet run --project tests/CapFast.Checks/CapFast.Checks.csproj --configuration Release -- --audio-recording-checks
```

Set `CAPFAST_FFMPEG` to an installed FFmpeg executable to additionally decode test recordings with an independent implementation and compare every PCM byte. FFmpeg is a development check only and is not needed by CapFast. Without that environment variable the checks explicitly report the independent decode as skipped.

Coverage includes silence, constant signal, tones, ramps, random full-range noise, alternating minimum/maximum samples, odd capture byte boundaries, partial final frames, two-/three-byte frame numbers, source MD5/sample count, midnight rollover with a sample spanning callbacks, segment rotation, restart without overwrites, empty capture, and storage failure isolation. The full existing caption checks and independent decode checks passed on macOS. The app and Windows x64 publish build with no warnings or errors. Windows live microphone recording and File Explorer integration still require a Windows smoke test.

To simulate recording a local fixture with no microphone, key, or cloud connection:

```sh
dotnet run --project tests/CapFast.Checks/CapFast.Checks.csproj --configuration Release -- \
  --record-audio-fixture sampleTalk/alternating-snippets/alternating-eng-swa-long-pause-83s.wav /tmp/qa-recording.flac
```

The fixture must be 16 kHz, 16-bit mono PCM WAV; output must not already exist. The command reports encode time, size/hour projection, and source checksum validation, and runs the independent decode check when configured.
