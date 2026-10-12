# Alternating English / Swahili playback fixture

`alternating-eng-swa-77s.wav` alternates six 12-second speech excerpts with one second of silence between turns. Total duration: **77 seconds**. Ready for later playback into BlackHole; creating this fixture does not test app captioning.

| File | Source offset | Duration | Playlist start |
| --- | ---: | ---: | ---: |
| early-eng-12s.wav | 30 s | 12 s | 0 s |
| early-swa-12s.wav | 30 s | 12 s | 13 s |
| middle-eng-12s.wav | 393 s | 12 s | 26 s |
| middle-swa-12s.wav | 393 s | 12 s | 39 s |
| late-eng-12s.wav | 697 s | 12 s | 52 s |
| late-swa-12s.wav | 697 s | 12 s | 65 s |

Sources are the supplied `2026-04-1050-clark-g-gilbert-360p-eng.mp4` and `2026-04-1050-clark-g-gilbert-360p-swa.mp4` in the parent directory. They are English and Swahili editions of Clark G. Gilbert’s “Come Home” talk according to their filenames and metadata. No French source was found. Language was not independently recognized in this extraction task.

Matching offsets and durations simulate translator turn timing; they do not establish sentence-aligned translation pairs. Excerpts may start or end mid-sentence. Early, middle, and late portions provide variety. Source audio gain and speed are retained. Output is mono 16 kHz signed 16-bit PCM WAV. Original sources were preserved.

`manifest.json` records every exact ffmpeg extraction command, tool version, source offset, sample count, duration, RMS level, checksum, and playlist start. ffmpeg is the repository’s bundled imageio-ffmpeg binary; ffmpeg/ffprobe were not available on PATH. Each command uses `-n` to prevent overwriting.

The playlist was assembled with Python’s `wave` module in table order, inserting 16,000 zero-valued 16-bit mono samples after each of the first five clips. Its duration and the six clip durations were checked from WAV sample counts. All six clips have nonzero RMS levels.

## Longer silence fixture

`alternating-eng-swa-long-pause-83s.wav` preserves the six snippets and inserts a **7-second digital silence from 38–45 seconds**, between middle English and middle Swahili, to exercise language reassignment after more than five seconds without speech. The other four gaps remain one second. Total duration is **83 seconds**, verified from WAV sample counts. The original 77-second playlist is preserved.

| Clip | Start | End |
| --- | ---: | ---: |
| early-eng-12s.wav | 0 s | 12 s |
| early-swa-12s.wav | 13 s | 25 s |
| middle-eng-12s.wav | 26 s | 38 s |
| middle-swa-12s.wav | 45 s | 57 s |
| late-eng-12s.wav | 58 s | 70 s |
| late-swa-12s.wav | 71 s | 83 s |

Assembly uses Python `wave` with the same six existing WAVs, inserting `[1, 1, 7, 1, 1]` seconds of zero-valued samples after the first five clips. Exact timing and checksum are in `manifest.json` under `longPausePlaylist`. App reassignment behavior has not been tested by this fixture creation.
